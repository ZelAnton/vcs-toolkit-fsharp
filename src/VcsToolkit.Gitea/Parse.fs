namespace VcsToolkit.Gitea

open System
open System.Globalization
open System.Text.Json
open VcsToolkit.CliSupport

// `tea --output json` is NOT the Gitea REST shape. Its *list* commands serialize
// tea's print-table: a JSON array of string-maps whose keys are snake-cased column
// headers and whose values are all JSON *strings* (never typed numbers/bools). Its
// *detail* view (`issues <n>`) is a separate *typed* object. The parsers model both.

/// A pull request (`tea pr list --output json`), flattened from tea's table columns
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

/// An issue (`tea issues list --output json` / `tea issues <index> --output json`).
/// The two tea paths differ — the **list** is a string-table row, the **detail** view
/// a typed object — but both flatten into this struct.
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

/// A release (`tea releases list --output json`), flattened from tea's fixed
/// release-table columns. **`tea releases` exposes no web-page URL** (only a combined
/// tar/zip download URL, deliberately not surfaced), so `Url` is always empty for Gitea.
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

/// Tolerant parsers over `tea … --output json` output. The list parsers model tea's
/// all-strings print-table (`index` cells are JSON strings, parsed to `uint64`); the
/// issue-detail parser models tea's typed single-object view. Building on the shared
/// `Json` helpers, each parser is total — a malformed document yields `Error`, never
/// an exception.
[<RequireQualifiedAccess>]
module GiteaParse =

    /// Parse a tea table cell holding an issue/PR index (always a JSON **string**, e.g.
    /// `"4"`) into a `uint64`, mapping a non-numeric value to an `Error`. A missing
    /// index reads as `""` → `Error` (a row always carries it; a silent `0` would let
    /// `prView` "find" a phantom PR).
    let private parseIndex (value: string) : Result<uint64, string> =
        match UInt64.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, n -> Ok n
        | _ -> Error(sprintf "expected a numeric index, got %A" value)

    let private toPrResult (el: JsonElement) : Result<PullRequest, string> =
        match parseIndex (Json.strOr el "index") with
        | Error e -> Error e
        | Ok number ->
            let state = Json.strOr el "state"

            Ok
                { Number = number
                  Title = Json.strOr el "title"
                  // tea's `state` column already folds in the merge flag.
                  Merged = state.Equals("merged", StringComparison.OrdinalIgnoreCase)
                  State = state
                  HeadBranch = Json.strOr el "head"
                  BaseBranch = Json.strOr el "base"
                  Url = Json.strOr el "url" }

    let private toIssueListResult (el: JsonElement) : Result<Issue, string> =
        match parseIndex (Json.strOr el "index") with
        | Error e -> Error e
        | Ok number ->
            Ok
                { Number = number
                  Title = Json.strOr el "title"
                  State = Json.strOr el "state"
                  Body = Json.strOr el "body"
                  Url = Json.strOr el "url" }

    // The single-issue **detail** view is a typed object (tea's `buildIssueData`):
    // `index` is a real JSON number, keys are `index`/`title`/`state`/`body`/`url`,
    // and `body`/`url` can be a present null (tolerated as empty). The `index` here is
    // read tolerantly (`u64Or`), following the shared `Json` convention for a numeric
    // id that arrives as a JSON *number* — the same as the GitHub/GitLab wrappers. This
    // differs from the *list* path (`parseIndex`), where the id arrives as a JSON
    // *string* and a non-numeric cell can only be a strict `Error` (there is no number
    // to read from `"abc"`).
    let private toIssueDetail (el: JsonElement) : Issue =
        { Number = Json.u64Or el "index"
          Title = Json.strOr el "title"
          State = Json.strOr el "state"
          Body = Json.strOr el "body"
          Url = Json.strOr el "url" }

    let private toReleaseResult (el: JsonElement) : Result<Release, string> =
        // tea's `toSnakeCase` inserts a stray `_` before each capitalised run, so the
        // fixed headers become `tag-_name`/`published _at`; the aliases tolerate a
        // future tea that fixes the quirk or switches to camelCase / the raw header.
        let tag =
            Json.strFirst el [ "tag-_name"; "tag_name"; "tag-name"; "tagName"; "Tag-Name" ]

        if tag = "" then
            // No `tag` column: a real parse failure, not a silent empty tag.
            Error "expected a release tag (the `tag-_name` column)"
        else
            let status = Json.strFirst el [ "status"; "Status" ]

            Ok
                { Tag = tag
                  Title = Json.strFirst el [ "title"; "Title" ]
                  PublishedAt =
                    Json.strFirst
                        el
                        [ "published _at"
                          "published_at"
                          "published-at"
                          "publishedAt"
                          "Published At" ]
                  // tea collapses draft/prerelease/released into one `Status` column.
                  Draft = status.Equals("draft", StringComparison.OrdinalIgnoreCase)
                  Prerelease = status.Equals("prerelease", StringComparison.OrdinalIgnoreCase)
                  // tea's release table carries no web-page URL column.
                  Url = "" }

    /// A `tea` list parser that treats **empty/whitespace output as an empty list**, not a
    /// parse error: some `tea` builds print nothing (not `[]`) for an empty result, so
    /// listing PRs/issues/releases on a brand-new or empty repository yields no output — a
    /// normal state, not a failure. (Matches the Rust `parse.rs` guard.)
    let private parseArrayOrEmpty mapper (json: string) =
        if System.String.IsNullOrWhiteSpace json then
            Ok []
        else
            Json.parseArrayResult mapper json

    /// Parse a `tea pr list` array (all-strings table; `index` → `uint64`).
    let parsePrList = parseArrayOrEmpty toPrResult
    /// Parse a `tea issues list` array (all-strings table; `index` → `uint64`).
    let parseIssueList = parseArrayOrEmpty toIssueListResult
    /// Parse a `tea issues <index>` detail object (typed; `index` is a real number).
    let parseIssue = Json.parseObject toIssueDetail
    /// Parse a `tea releases list` array (all-strings table; a missing tag is an `Error`).
    let parseReleaseList = parseArrayOrEmpty toReleaseResult

    /// Whether `tea login list --output json` reports at least one login — the
    /// "are we logged in" signal (`tea` has no per-instance `auth status`). A non-array
    /// document is an `Error`; an empty array is `Ok false`.
    let parseHasLogins (json: string) : Result<bool, string> =
        Json.withDoc json (fun root ->
            if root.ValueKind = JsonValueKind.Array then
                Ok(root.GetArrayLength() > 0)
            else
                Error "expected a JSON array of logins")
