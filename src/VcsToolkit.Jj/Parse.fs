namespace VcsToolkit.Jj

open System
open VcsToolkit.Diff

/// A jj change, parsed from a `\t`-delimited template row.
type Change =
    {
        /// Short change id (`change_id.short()`).
        ChangeId: string
        /// Short commit id (`commit_id.short()`).
        CommitId: string
        /// `true` when the change makes no file modifications.
        Empty: bool
        /// First line of the description (empty for an undescribed change).
        Description: string
    }

/// A jj bookmark, parsed from `jj bookmark list` output.
type Bookmark =
    {
        /// Bookmark name.
        Name: string
        /// Short id of the commit it points at.
        Target: string
    }

/// A bookmark from `jj bookmark list -a` — local *or* remote-tracking.
type BookmarkRef =
    {
        /// Bookmark name.
        Name: string
        /// The remote it lives on (e.g. `origin`/`git`); `None` for a local bookmark.
        Remote: string option
        /// Short id of the commit it points at (empty for a conflicted bookmark).
        Target: string
        /// Whether this remote-tracking bookmark is tracked (`false` for locals).
        Tracked: bool
    }

/// A workspace from `jj workspace list`.
type Workspace =
    {
        /// Workspace name (`default` for the main one).
        Name: string
        /// Short commit id of the workspace's working-copy commit.
        Commit: string
        /// Local bookmarks pointing at that commit (empty when none).
        Bookmarks: string list
    }

/// One entry from `jj diff --summary`: a single-letter status (`M`/`A`/`D`/…) and
/// the (forward-slash-normalised) path it applies to — the *new* path for a
/// rename/copy, with the original on `OldPath`.
type ChangedPath =
    {
        /// Status letter (`M` modified, `A` added, `D` deleted, `R` renamed, `C` copied).
        Status: char
        /// The path the status applies to — the *new* path for a rename/copy.
        Path: string
        /// For a rename (`R`) or copy (`C`), the original path; `None` otherwise.
        OldPath: string option
    }

/// One entry of `jj op log` (an operation-log row).
type Operation =
    {
        /// Short operation id — what `opRestore`/`opUndo` take.
        Id: string
        /// The OS-level `user@host` that ran the operation (not the configured jj author).
        User: string
        /// Start timestamp, ISO 8601 with offset.
        Time: string
        /// First line of the operation description, e.g. `new empty commit`.
        Description: string
    }

/// One line of `jj file annotate` output: which change last touched it.
type AnnotationLine =
    {
        /// Short change id of the change that introduced the line.
        ChangeId: string
        /// Line number in the annotated file (1-based).
        Line: int
        /// The line's content (the raw bytes jj reports for the line, with only the
        /// `\n` row separator removed; a trailing `\r` from a CRLF-terminated source
        /// file is preserved, not stripped).
        Content: string
    }

/// Pure parsers and the `jj` templates that feed them. No process execution, so
/// these are hermetic and total: arbitrary CLI text in, never an exception.
[<RequireQualifiedAccess>]
module JjParse =

    // --- Templates -----------------------------------------------------------
    // Each is a jj template-language expression. The literal `\t` / `\n` sequences
    // are passed verbatim to jj (its template language interprets them), so they
    // are written as `\"\\t\"` — a quote, a backslash, a `t`, a quote — exactly as
    // the Rust source spells them.

    /// Template used by the change commands: tab-separated, one change per line.
    let CHANGE_TEMPLATE =
        "change_id.short() ++ \"\\t\" ++ commit_id.short() ++ \"\\t\" ++ if(empty, \"true\", \"false\") ++ \"\\t\" ++ description.first_line() ++ \"\\n\""

    /// `jj workspace list -T` template: `name\t<commit>\t<bookmarks,comma-joined>`.
    let WORKSPACE_TEMPLATE =
        "name ++ \"\\t\" ++ target.commit_id().short() ++ \"\\t\" ++ target.local_bookmarks().map(|b| b.name()).join(\",\") ++ \"\\n\""

    /// `jj log -T` template rendering a commit's local bookmark names, comma-joined.
    /// Drives `currentBookmark`/`trunk`.
    let BOOKMARKS_TEMPLATE = "local_bookmarks.map(|b| b.name()).join(\",\")"

    /// `jj bookmark list -a -T` template: `name\t<remote>\t<tracked 1/0>\t<commit>`,
    /// one row per local *and* remote-tracking bookmark.
    let BOOKMARK_ALL_TEMPLATE =
        "name ++ \"\\t\" ++ remote ++ \"\\t\" ++ if(tracked, \"1\", \"0\") ++ \"\\t\" ++ if(normal_target, normal_target.commit_id().short(), \"\") ++ \"\\n\""

    /// `jj bookmark list -T` template (no `-a`, so local bookmarks only):
    /// `name\t<commit>`, one row per local bookmark.
    let BOOKMARK_LIST_TEMPLATE =
        "name ++ \"\\t\" ++ if(normal_target, normal_target.commit_id().short(), \"\") ++ \"\\n\""

    /// `jj log -T` template: `"1"` when the commit has a conflict, else `"0"`.
    let CONFLICT_TEMPLATE = "if(conflict, \"1\", \"0\")"

    /// `jj log -T` template emitting one short commit id per line — for counting a revset.
    let COUNT_TEMPLATE = "commit_id.short() ++ \"\\n\""

    /// `jj log -T` template for `reachableBookmarks`: the commit's local bookmark
    /// names (space-joined; jj names can't contain spaces) then a tab then the short
    /// commit id.
    let REACHABLE_BOOKMARKS_TEMPLATE =
        "local_bookmarks.map(|b| b.name()).join(\" \") ++ \"\\t\" ++ commit_id.short() ++ \"\\n\""

    /// `jj evolog -T` template. Evolog renders in a *commit* context where the bare
    /// keywords (`change_id`, …) don't exist — the `commit.` method form is required.
    /// Columns mirror `CHANGE_TEMPLATE`, so `parseChanges` reads it.
    let EVOLOG_TEMPLATE =
        "commit.change_id().short() ++ \"\\t\" ++ commit.commit_id().short() ++ \"\\t\" ++ if(commit.empty(), \"true\", \"false\") ++ \"\\t\" ++ commit.description().first_line() ++ \"\\n\""

    /// `jj op log -T` template: `id\tuser\tstart-time\tdescription`, one row per operation. The
    /// time uses `%:z` (extended offset `+02:00`), NOT `%z` (basic `+0200`): strict RFC-3339 / ISO-
    /// 8601 parsers reject the basic form, and `+02:00` matches the git backend's `%aI` dates so a
    /// cross-backend consumer sees one timestamp shape.
    let OP_TEMPLATE =
        "id.short() ++ \"\\t\" ++ user ++ \"\\t\" ++ time.start().format(\"%Y-%m-%dT%H:%M:%S%:z\") ++ \"\\t\" ++ description.first_line() ++ \"\\n\""

    /// `jj file annotate -T` template: `change-id\tcontent`. Annotate emits one row
    /// per source line and separates them itself — no trailing `\n` here, or every
    /// row would be double-spaced.
    let ANNOTATE_TEMPLATE = "commit.change_id().short() ++ \"\\t\" ++ content"

    // --- Helpers -------------------------------------------------------------

    /// Split like Rust's `str::lines()`: break on `\n`, strip one trailing `\r` per
    /// line (so a `\r\n` terminator is consumed), and yield no phantom final empty
    /// line from a trailing newline.
    let private lines (s: string) : string list =
        let parts = s.Split('\n') |> Array.toList
        // Drop the single empty segment a trailing '\n' leaves behind.
        let trimmed =
            match List.rev parts with
            | "" :: rest -> List.rev rest
            | _ -> parts

        trimmed
        |> List.map (fun l ->
            if l.EndsWith("\r", StringComparison.Ordinal) then
                l.Substring(0, l.Length - 1)
            else
                l)

    /// Digit-only, invariant-culture parse matching Rust's `usize::from_str` (which
    /// rejects signs/whitespace), so a malformed token reads as 0.
    let private parseIntOr0 (s: string) : uint64 =
        if s.Length > 0 && s |> Seq.forall Char.IsAsciiDigit then
            match UInt64.TryParse(s, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
            | true, v -> v
            | _ -> 0UL
        else
            0UL

    let private normalize (p: string) = p.Replace(char 92, '/')

    // --- Version -------------------------------------------------------------

    /// Parse `jj --version` output (`jj 0.38.0`) into the shared `Version`: the first
    /// dotted-numeric token wins; non-numeric trailers (`-dev`, build hashes) are
    /// ignored; a missing patch reads as `0`.
    let parseJjVersion (raw: string) : Version option = parseDottedVersion raw

    // --- Changes / operations / annotations ----------------------------------

    /// Parse rows produced by `CHANGE_TEMPLATE` (or the column-identical `EVOLOG_TEMPLATE`).
    let parseChanges (output: string) : Change list =
        lines output
        |> List.choose (fun line ->
            if line = "" then
                None
            else
                // Split into at most 4 so a trailing description keeps any literal tabs.
                let f = line.Split([| '\t' |], 4)

                if f.Length < 3 then
                    None
                else
                    Some
                        { ChangeId = f.[0]
                          CommitId = f.[1]
                          Empty = (f.[2] = "true")
                          Description = (if f.Length >= 4 then f.[3] else "") })

    /// Parse rows produced by `OP_TEMPLATE`.
    let parseOperations (output: string) : Operation list =
        lines output
        |> List.choose (fun line ->
            if line = "" then
                None
            else
                let f = line.Split([| '\t' |], 4)

                if f.Length < 3 then
                    None
                else
                    Some
                        { Id = f.[0]
                          User = f.[1]
                          Time = f.[2]
                          Description = (if f.Length >= 4 then f.[3] else "") })

    /// Parse rows produced by `ANNOTATE_TEMPLATE`: one row per source line, the
    /// 1-based line number is the row index.
    ///
    /// Splits on `\n` (not the line helper) so a trailing `\r` belonging to a
    /// CRLF-terminated source line stays in the content. The empty final segment a
    /// trailing newline leaves carries no tab, so the tab filter drops it and the
    /// line numbering stays exact.
    let parseAnnotate (output: string) : AnnotationLine list =
        output.Split('\n')
        |> Array.mapi (fun idx line -> (idx, line))
        |> Array.choose (fun (idx, line) ->
            match line.IndexOf('\t') with
            | -1 -> None
            | i ->
                Some
                    { ChangeId = line.Substring(0, i)
                      Line = idx + 1
                      Content = line.Substring(i + 1) })
        |> Array.toList

    // --- Bookmarks -----------------------------------------------------------

    /// Parse rows produced by `BOOKMARK_LIST_TEMPLATE`: `name\t<commit>`, one row per
    /// local bookmark. A row with an empty name contributes nothing.
    let parseBookmarks (output: string) : Bookmark list =
        lines output
        |> List.choose (fun line ->
            if line = "" then
                None
            else
                let f = line.Split('\t')
                let name = f.[0].Trim()

                if name = "" then
                    None
                else
                    Some
                        { Name = name
                          Target = (if f.Length >= 2 then f.[1].Trim() else "") })

    /// Parse rows produced by `BOOKMARK_ALL_TEMPLATE`:
    /// `name\t<remote>\t<tracked 1/0>\t<commit>` per local/remote bookmark. A row
    /// whose name field is empty contributes nothing.
    let parseBookmarksAll (output: string) : BookmarkRef list =
        lines output
        |> List.choose (fun line ->
            if line = "" then
                None
            else
                let f = line.Split('\t')
                let name = f.[0].Trim()

                if name = "" then
                    None
                else
                    let remote = if f.Length >= 2 then f.[1] else ""

                    Some
                        { Name = name
                          Remote = (if remote = "" then None else Some remote)
                          Tracked = (f.Length >= 3 && f.[2] = "1")
                          Target = (if f.Length >= 4 then f.[3] else "") })

    /// Parse rows produced by `REACHABLE_BOOKMARKS_TEMPLATE`: `<name>[ <name>…]\t<commit>`.
    /// A commit with several bookmarks yields one `Bookmark` per name, all sharing
    /// that commit as the target. A row with no bookmark names contributes nothing.
    let parseReachableBookmarks (output: string) : Bookmark list =
        [ for line in lines output do
              if line <> "" then
                  let f = line.Split([| '\t' |], 2)
                  let names = f.[0]
                  let target = if f.Length >= 2 then f.[1] else ""

                  for name in names.Split([| ' '; '\t'; '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) do
                      yield { Name = name; Target = target } ]

    // --- Resolve / workspaces ------------------------------------------------

    /// Parse `jj resolve --list` output: each line is a conflicted path left-aligned
    /// in a column, then a run of spaces, then a human conflict description. Take the
    /// path (the text before the first 2-space gap), forward-slash normalised (jj
    /// emits the OS-native separator here, like `--summary`).
    let parseResolveList (output: string) : string list =
        lines output
        |> List.choose (fun line ->
            let path = (line.Split([| "  " |], StringSplitOptions.None)).[0].Trim()

            if path = "" then None else Some(path.Replace(char 92, '/')))

    /// Parse rows produced by `WORKSPACE_TEMPLATE`: `name\t<commit>\t<bookmarks>`,
    /// where bookmarks are comma-joined (and may be empty).
    let parseWorkspaces (output: string) : Workspace list =
        lines output
        |> List.choose (fun line ->
            if line = "" then
                None
            else
                let f = line.Split('\t')

                let bookmarks =
                    if f.Length >= 3 then
                        f.[2].Split(',') |> Array.filter (fun s -> s <> "") |> Array.toList
                    else
                        []

                Some
                    { Name = f.[0]
                      Commit = (if f.Length >= 2 then f.[1] else "")
                      Bookmarks = bookmarks })

    // --- Diff summary / stat -------------------------------------------------

    /// Expand jj's rename/copy path form `prefix{left => right}suffix` into
    /// `(old, new)` full paths. Falls back to `(raw, raw)` when the brace/arrow form
    /// isn't present, so a plain path is returned unchanged.
    let expandRename (raw: string) : string * string =
        let plain () = (raw, raw)
        let openI = raw.IndexOf('{')
        let closeI = raw.IndexOf('}')

        if openI < 0 || closeI < 0 || openI >= closeI then
            plain ()
        else
            let inner = raw.Substring(openI, closeI - openI)

            match inner.IndexOf(" => ", StringComparison.Ordinal) with
            | -1 -> plain ()
            | rel ->
                let arrow = openI + rel
                let prefix = raw.Substring(0, openI)
                let left = raw.Substring(openI + 1, arrow - (openI + 1))
                let right = raw.Substring(arrow + 4, closeI - (arrow + 4))
                let suffix = raw.Substring(closeI + 1)
                (prefix + left + suffix, prefix + right + suffix)

    /// Parse `jj diff --summary`: each line is `<status-letter> <path>`. A rename
    /// (`R`) or copy (`C`) renders the path as `prefix{old => new}suffix`, expanded
    /// here into the real new path (the old path captured on `OldPath`). Paths are
    /// forward-slash normalised — jj's `--summary` uses the OS-native separator,
    /// unlike its `--git` diff — keeping the DTO consistent across platforms.
    let parseDiffSummary (output: string) : ChangedPath list =
        lines output
        |> List.choose (fun line ->
            if line = "" then
                None
            elif line.Length < 2 || line.[1] <> ' ' then
                None
            else
                let status = line.[0]
                let raw = line.Substring(2)

                if raw = "" then
                    None
                elif status = 'R' || status = 'C' then
                    let (oldRaw, newRaw) = expandRename raw
                    let oldN, newN = normalize oldRaw, normalize newRaw
                    // A non-brace R/C path expands to old == new; don't report that as
                    // a self-rename, so `OldPath <> Some Path` stays a reliable test.
                    Some
                        { Status = status
                          Path = newN
                          OldPath = (if oldN <> newN then Some oldN else None) }
                else
                    Some
                        { Status = status
                          Path = normalize raw
                          OldPath = None })

    /// Parse the summary footer of `jj diff --stat`, e.g. `4 files changed, 157
    /// insertions(+), 137 deletions(-)`. The footer is the last line mentioning
    /// "changed"; no such line → all zeros.
    let parseDiffStat (output: string) : DiffStat =
        let summary =
            lines output
            |> List.rev
            |> List.tryFind (fun line -> line.Contains "changed")
            |> Option.defaultValue ""

        let mutable files = 0UL
        let mutable insertions = 0UL
        let mutable deletions = 0UL

        for rawPart in summary.Split(',') do
            let part = rawPart.Trim()

            let n =
                match
                    part.Split([| ' '; '\t'; '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.tryHead
                with
                | Some tok -> parseIntOr0 tok
                | None -> 0UL

            if part.Contains "file" then
                files <- n
            elif part.Contains "insertion" then
                insertions <- n
            elif part.Contains "deletion" then
                deletions <- n

        DiffStat.Create(files, insertions, deletions)
