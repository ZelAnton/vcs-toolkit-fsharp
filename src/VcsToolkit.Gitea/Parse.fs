namespace VcsToolkit.Gitea

open System
open System.Globalization
open VcsToolkit.Diff

// `tea` 0.9.2 does NOT support `--output json` on its list commands (K-049): asking for it
// returns exit 0 with the literal text `unknown output type 'json', available types are:
// - csv: ...`. The supported machine-readable formats are `csv`/`tsv`/`yaml`. This wrapper
// drives `--output csv`, which tea renders with its `outputdsv` writer: every row is printed
// as `"cell1","cell2",...` — each cell wrapped in double quotes and joined by the literal
// three-character delimiter `","`, with a leading header row and NO RFC-4180 quote-escaping
// inside a cell. Parsing that exact contract keeps commas, ordinary quotes and Unicode intact;
// the only genuinely ambiguous input (a cell literally containing `","`, or an embedded
// newline) fails honestly as a column-count mismatch rather than shifting DTO fields silently.
// Because this wrapper pins the columns via `--fields`, the parsers read cells positionally by
// the known order rather than trusting tea's header-cell names.

/// A pull request (`tea pr list --output csv`), flattened from tea's `--fields` columns
/// (`index`/`title`/`state`/`head`/`base`/`url`).
type PullRequest =
    {
        /// PR number (tea's `index` column).
        Number: uint64
        /// PR title.
        Title: string
        /// State, e.g. `"open"`, `"closed"`, `"merged"` — tea folds the merge flag into
        /// this column (a merged PR reads `"merged"`, not `"closed"`).
        State: string
        /// Whether the PR has been merged — derived from `state = "merged"` (tea has no
        /// separate merged column).
        Merged: bool
        /// Source (head) branch name (tea's `head` column, a flat branch name).
        HeadBranch: string
        /// Target (base) branch name (tea's `base` column, a flat branch name).
        BaseBranch: string
        /// Web URL (tea's `url` column).
        Url: string
    }

/// An issue (`tea issues list --output csv`). The single-issue view is synthesized by
/// paging that same listing and filtering by number — `tea` 0.9.2's bare-index view
/// (`tea issues <n>`) renders Markdown and ignores `--output`, so there is no structured
/// detail read to parse.
type Issue =
    {
        /// Issue number (tea's `index`).
        Number: uint64
        /// Issue title.
        Title: string
        /// State, e.g. `"open"`, `"closed"`.
        State: string
        /// Issue body / description.
        Body: string
        /// Web URL (tea's `url`).
        Url: string
    }

/// A release (`tea releases list --output csv`), flattened from tea's fixed release-table
/// columns (`Tag-Name`/`Title`/`Published At`/`Status`/`Tar URL`, in that order). **`tea
/// releases` exposes no web-page URL** (only a combined tar/zip download URL, deliberately
/// not surfaced), so `Url` is always empty for Gitea.
type Release =
    {
        /// Git tag the release points at (tea's `Tag-Name` column).
        Tag: string
        /// Release title (tea's `Title` column).
        Title: string
        /// Publish timestamp, e.g. `"2023-07-26T13:02:36Z"` (tea's `Published At`
        /// column); empty for an unpublished draft.
        PublishedAt: string
        /// Whether the release is a draft (derived from tea's `Status` column).
        Draft: bool
        /// Whether the release is a pre-release (derived from tea's `Status` column).
        Prerelease: bool
        /// **Always empty for Gitea.** `tea releases list` has no release-page URL column.
        Url: string
    }

/// Total parsers over `tea … --output csv` output (tea 0.9.2's quoted `outputdsv` tables).
/// Each parser is total — a malformed document, a non-tabular diagnostic (`unknown output
/// type …`), or a column-count mismatch yields `Error`, never an exception.
[<RequireQualifiedAccess>]
module internal GiteaParse =

    // tea 0.9.2's `outputdsv` writer separates cells with this literal three-character
    // sequence (quote, delimiter, quote) and wraps the whole row in a leading/trailing quote.
    [<Literal>]
    let private CELL_DELIMITER = "\",\""

    /// Parse a tea table cell holding an issue/PR index (e.g. `"4"`) into a `uint64`, mapping
    /// a non-numeric value to an `Error`. A missing/blank index reads as `Error` (a row always
    /// carries it; a silent `0` would let `prView`/`issueView` "find" a phantom item).
    let private parseIndex (value: string) : Result<uint64, string> =
        match UInt64.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, n -> Ok n
        | _ -> Error(sprintf "expected a numeric index, got %A" value)

    /// Split tea's raw stdout into its non-empty lines, normalising line endings first (tea
    /// on the CI Linux target emits `\n`; a `\r\n`/`\r` from a Windows-local run is folded).
    let private splitLines (raw: string) : string list =
        raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
        |> Array.toList
        |> List.filter (fun line -> line <> "")

    /// Parse one `outputdsv` row into exactly `columnCount` cells. A line that is not a
    /// well-formed quoted row — most importantly tea's `unknown output type '…'` diagnostic,
    /// which it prints to stdout at exit 0 for an unsupported `--output` (e.g. `json`, K-049)
    /// — is a hard `Error`, never a silently-tolerated empty/short result. A wrong cell count
    /// (an ambiguous embedded `","` or newline in a cell, or unexpected tea output) is an
    /// `Error` too rather than a silent field shift.
    let private parseRow (columnCount: int) (lineNumber: int) (line: string) : Result<string array, string> =
        if line.Length < 2 || not (line.StartsWith('"')) || not (line.EndsWith('"')) then
            Error(
                sprintf
                    "expected a quoted tea csv row at line %d, got %A (tea 0.9.2 wraps every `--output csv` cell in quotes; a non-quoted line usually means an unsupported `--output` type — e.g. tea printing `unknown output type …` at exit 0)"
                    lineNumber
                    line
            )
        else
            let cells =
                line.Substring(1, line.Length - 2).Split([| CELL_DELIMITER |], StringSplitOptions.None)

            if cells.Length = columnCount then
                Ok cells
            else
                Error(
                    sprintf
                        "expected %d tea csv columns at line %d, got %d (an ambiguous embedded `\",\"` or newline in a cell, or an unexpected tea output format)"
                        columnCount
                        lineNumber
                        cells.Length
                )

    /// Parse a `tea … --output csv` table into its data rows (the header row is validated as a
    /// well-formed quoted row and dropped). Empty/whitespace output is an **empty list**, not a
    /// parse error: an empty PR/issue/release listing renders as nothing or as a header-only
    /// table, both a normal state. Any non-tabular first line is an `Error` (see `parseRow`).
    let private parseTable (columnCount: int) (raw: string) : Result<string array list, string> =
        if String.IsNullOrWhiteSpace raw then
            Ok []
        else
            match splitLines raw with
            | [] -> Ok []
            | header :: dataLines ->
                match parseRow columnCount 1 header with
                | Error e -> Error e
                | Ok _ ->
                    (Ok [], dataLines |> List.indexed)
                    ||> List.fold (fun state (index, line) ->
                        match state with
                        | Error _ -> state
                        | Ok rows ->
                            match parseRow columnCount (index + 2) line with
                            | Ok row -> Ok(row :: rows)
                            | Error e -> Error e)
                    |> Result.map List.rev

    /// Parse a csv table and map each data row with the fallible `mapper`; the first failing
    /// row aborts the whole parse.
    let private parseMapped (columnCount: int) (mapper: string array -> Result<'T, string>) (raw: string) =
        parseTable columnCount raw
        |> Result.bind (fun rows ->
            (Ok [], rows)
            ||> List.fold (fun state row ->
                match state with
                | Error _ -> state
                | Ok items -> mapper row |> Result.map (fun item -> item :: items))
            |> Result.map List.rev)

    // Column counts pinned by the `--fields` sets in `Constants` (PR/issue) and by tea's
    // fixed default release columns.
    [<Literal>]
    let private PR_COLUMNS = 6 // index,title,state,head,base,url

    [<Literal>]
    let private ISSUE_COLUMNS = 5 // index,title,state,body,url

    [<Literal>]
    let private RELEASE_COLUMNS = 5 // Tag-Name,Title,Published At,Status,Tar URL

    let private toPrResult (cells: string array) : Result<PullRequest, string> =
        match parseIndex cells[0] with
        | Error e -> Error e
        | Ok number ->
            let state = cells[2]

            Ok
                { Number = number
                  Title = cells[1]
                  // tea's `state` column already folds in the merge flag.
                  Merged = state.Equals("merged", StringComparison.OrdinalIgnoreCase)
                  State = state
                  HeadBranch = cells[3]
                  BaseBranch = cells[4]
                  Url = cells[5] }

    let private toIssueResult (cells: string array) : Result<Issue, string> =
        match parseIndex cells[0] with
        | Error e -> Error e
        | Ok number ->
            Ok
                { Number = number
                  Title = cells[1]
                  State = cells[2]
                  Body = cells[3]
                  Url = cells[4] }

    let private toReleaseResult (cells: string array) : Result<Release, string> =
        let tag = cells[0]

        if tag = "" then
            // No tag in the `Tag-Name` column: a real parse failure, not a silent empty tag.
            Error "expected a release tag in the `Tag-Name` column"
        else
            let status = cells[3]

            Ok
                { Tag = tag
                  Title = cells[1]
                  PublishedAt = cells[2]
                  // tea collapses draft/prerelease/released into one `Status` column.
                  Draft = status.Equals("draft", StringComparison.OrdinalIgnoreCase)
                  Prerelease = status.Equals("prerelease", StringComparison.OrdinalIgnoreCase)
                  // tea's release table carries no web-page URL column.
                  Url = "" }

    /// Parse a `tea pr list --output csv` table (columns pinned by `PR_FIELDS`).
    let parsePrList = parseMapped PR_COLUMNS toPrResult
    /// Parse a `tea issues list --output csv` table (columns pinned by `ISSUE_FIELDS`).
    let parseIssueList = parseMapped ISSUE_COLUMNS toIssueResult
    /// Parse a `tea releases list --output csv` table (tea's fixed release columns; a blank
    /// `Tag-Name` cell is an `Error`).
    let parseReleaseList = parseMapped RELEASE_COLUMNS toReleaseResult

    /// Whether `tea login list --output csv` reports at least one login — the "are we logged
    /// in" signal (`tea` has no per-instance `auth status`). Read leniently: only the row
    /// **count** matters (a header row plus at least one data row = logged in), so tea's exact
    /// login columns need not be pinned. Empty/whitespace output is `Ok false`; a non-tabular
    /// first line (e.g. the `unknown output type …` diagnostic) is an `Error`, never a silent
    /// `false`.
    let parseHasLogins (csv: string) : Result<bool, string> =
        if String.IsNullOrWhiteSpace csv then
            Ok false
        else
            match splitLines csv with
            | [] -> Ok false
            | header :: dataLines ->
                if header.Length < 2 || not (header.StartsWith('"')) || not (header.EndsWith('"')) then
                    Error(
                        sprintf
                            "expected a quoted tea csv header for `login list`, got %A (an unsupported `--output` type?)"
                            header
                    )
                else
                    Ok(not (List.isEmpty dataLines))

    /// Parse `tea --version` output into the shared `Version`; `None` when no `N.N[.N]`
    /// token is present (an unrecognised/empty banner degrades to "unknown", never a throw).
    let parseVersion (raw: string) : Version option = parseDottedVersion raw
