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
        /// **Full** commit id it points at — a stable identity that cross-references
        /// against a `RepoSnapshot.Head` / git oid, not a display-truncated prefix.
        /// Empty when the bookmark has no single normal target (a conflicted bookmark,
        /// which is still *present*).
        Target: string
    }

/// A bookmark from `jj bookmark list -a` — local *or* remote-tracking.
type BookmarkRef =
    {
        /// Bookmark name.
        Name: string
        /// The remote it lives on (e.g. `origin`/`git`); `None` for a local bookmark.
        Remote: string option
        /// **Full** commit id it points at (empty for a conflicted bookmark) — a stable
        /// cross-referenceable identity, not a display-truncated prefix.
        Target: string
        /// Whether this remote-tracking bookmark is tracked (`false` for locals).
        Tracked: bool
    }

/// A workspace from `jj workspace list`.
type Workspace =
    {
        /// Workspace name (`default` for the main one).
        Name: string
        /// **Full** commit id of the workspace's working-copy commit — the identity the
        /// facade's `WorktreeInfo.Commit` carries so it compares directly against a
        /// `RepoSnapshot.Head`; not a display-truncated prefix.
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
        /// Author name of the change that introduced the line (`commit.author().name()`).
        Author: string
        /// Author date of the change, ISO-8601 with an offset
        /// (`commit.author().timestamp().format("%Y-%m-%dT%H:%M:%S%:z")` — the same shape as
        /// `Operation.Time`, and matching the git backend's `%aI` dates).
        Time: string
        /// Line number in the annotated file (1-based).
        Line: int
        /// The line's content (the raw bytes jj reports for the line, with only the
        /// `\n` row separator removed; a trailing `\r` from a CRLF-terminated source
        /// file is preserved, not stripped).
        Content: string
    }

/// One git remote jj knows about, from `jj git remote list` — a remote name and its URL.
type Remote =
    {
        /// The remote's name, e.g. `origin`.
        Name: string
        /// The URL jj records for the remote.
        Url: string
    }

/// Pure parsers and the `jj` templates that feed them. No process execution, so
/// these are hermetic and total: arbitrary CLI text in, never an exception.
[<RequireQualifiedAccess>]
module internal JjParse =

    /// Parse a root path from stdout that has already been decoded into a .NET string. Only one
    /// final LF or CRLF line terminator is removed; trailing spaces, tabs, and all other content
    /// are preserved. Because the process output was decoded as UTF-8 before this function sees
    /// it, invalid UTF-8 bytes have already become U+FFFD and cannot be recovered losslessly.
    let parseRoot (output: string) : string =
        if output.EndsWith("\r\n", StringComparison.Ordinal) then
            output.Substring(0, output.Length - 2)
        elif output.EndsWith("\n", StringComparison.Ordinal) then
            output.Substring(0, output.Length - 1)
        else
            output

    // --- Templates -----------------------------------------------------------
    // Each is a jj template-language expression. The literal `\t` / `\n` sequences
    // are passed verbatim to jj (its template language interprets them), so they
    // are written as `\"\\t\"` — a quote, a backslash, a `t`, a quote — exactly as
    // the Rust source spells them.
    //
    // Machine-template framing/escaping contract. The identity templates below
    // (WORKSPACE / BOOKMARK_ALL / BOOKMARK_LIST / REACHABLE_BOOKMARKS) render into a
    // byte stream we parse back into typed rows, so the framing has to stay
    // *unambiguous* even for exotic free text — a git-imported bookmark name can carry
    // a comma; a workspace name a tab/newline. The contract these templates obey:
    //
    //   * Rows are separated by a literal `\n`; fields within a row by a literal `\t`.
    //   * A field holding arbitrary user text (a bookmark/workspace *name*) is rendered
    //     through jj's `.escape_json()` — a standard JSON string literal (`"…"` with
    //     `\t`/`\n`/`\r`/`\"`/`\\`/`\uXXXX` escapes; raw UTF-8 otherwise, verified on jj
    //     0.42). An escaped field can never contain a literal `\t`/`\n`, so the framing
    //     stays unambiguous and `decodeJsonField` recovers the exact original.
    //   * A *list* field (a commit's/workspace's local bookmark names) is the
    //     `.escape_json()` of each element joined by a single space. Bookmark names can
    //     never hold a space (a git-ref rule jj enforces), so the space-joined JSON
    //     strings split back apart cleanly (`decodeNameList`).
    //   * Structurally-constrained fields — hex ids, `0`/`1` and `true`/`false` flags, a
    //     remote name (no whitespace by git-ref rule) — are rendered raw; they cannot
    //     contain a separator.
    //
    // Identity/cross-reference commit ids on these templates carry the *full* id (not
    // `.short()`) so they can be matched against a git oid / `RepoSnapshot.Head` without a
    // short-prefix collision. The history-display CHANGE_TEMPLATE/EVOLOG_TEMPLATE rows (a
    // display abbreviation, never a cross-reference key) are a deliberately separate
    // concern and keep their short ids / raw description.

    /// Template used by the change commands: tab-separated, one change per line.
    let CHANGE_TEMPLATE =
        "change_id.short() ++ \"\\t\" ++ commit_id.short() ++ \"\\t\" ++ if(empty, \"true\", \"false\") ++ \"\\t\" ++ description.first_line() ++ \"\\n\""

    /// `jj workspace list -T` template: `"<name>"\t<full-commit>\t<bookmarks>`, where the
    /// name is `.escape_json()`-framed (a workspace name may hold a tab/newline), the commit
    /// is the **full** id (identity — see the framing contract), and the bookmarks are the
    /// space-joined `.escape_json()` of each local bookmark name.
    let WORKSPACE_TEMPLATE =
        "name.escape_json() ++ \"\\t\" ++ target.commit_id() ++ \"\\t\" ++ target.local_bookmarks().map(|b| b.name().escape_json()).join(\" \") ++ \"\\n\""

    /// `jj log -T` template rendering a commit's local bookmark names as a space-joined
    /// `.escape_json()` list. Drives `currentBookmark`/`trunk`.
    let BOOKMARKS_TEMPLATE =
        "local_bookmarks.map(|b| b.name().escape_json()).join(\" \")"

    /// `jj bookmark list -a -T` template: `"<name>"\t<remote>\t<tracked 1/0>\t<full-commit>`,
    /// one row per local *and* remote-tracking bookmark. The name is `.escape_json()`-framed;
    /// `remote` is raw (a remote name carries no whitespace) and the commit is the **full** id.
    let BOOKMARK_ALL_TEMPLATE =
        "name.escape_json() ++ \"\\t\" ++ remote ++ \"\\t\" ++ if(tracked, \"1\", \"0\") ++ \"\\t\" ++ if(normal_target, normal_target.commit_id(), \"\") ++ \"\\n\""

    /// `jj bookmark list -T` template (no `-a`, so local bookmarks only):
    /// `"<name>"\t<full-commit>`, one row per local bookmark. The name is
    /// `.escape_json()`-framed and the commit is the **full** id (identity).
    let BOOKMARK_LIST_TEMPLATE =
        "name.escape_json() ++ \"\\t\" ++ if(normal_target, normal_target.commit_id(), \"\") ++ \"\\n\""

    /// `jj log -T` template: `"1"` when the commit has a conflict, else `"0"`.
    let CONFLICT_TEMPLATE = "if(conflict, \"1\", \"0\")"

    /// `jj file list -T` template driving `parseResolveList`: nothing for a non-conflicted
    /// entry, or a single `.escape_json()`-framed field holding `path.display()` (the
    /// cwd-relative, OS-native-separator rendering — mirrors `jj resolve --list`'s own
    /// path rendering) followed by `\n` for a conflicted one. See `parseResolveList`'s
    /// doc comment for why this replaced parsing `jj resolve --list` directly.
    let CONFLICTED_PATHS_TEMPLATE =
        "if(conflict, path.display().escape_json() ++ \"\\n\")"

    /// `jj log -T` template emitting one short commit id per line — for counting a revset.
    let COUNT_TEMPLATE = "commit_id.short() ++ \"\\n\""

    /// `jj log -T` template for `reachableBookmarks`: the commit's local bookmark
    /// names as space-joined `.escape_json()` strings (a name can't contain a space,
    /// so the join stays reversible even for a comma/quote-carrying name), then a tab,
    /// then the **full** commit id (identity — see the framing contract).
    let REACHABLE_BOOKMARKS_TEMPLATE =
        "local_bookmarks.map(|b| b.name().escape_json()).join(\" \") ++ \"\\t\" ++ commit_id ++ \"\\n\""

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

    /// `jj file annotate -T` template: `change-id\tauthor-name(escape_json)\tauthor-date\tcontent`.
    /// The author name is `.escape_json()`-framed (a name can hold a tab/newline; decoded back via
    /// `decodeJsonField`); the date uses the same `%Y-%m-%dT%H:%M:%S%:z` format as `OP_TEMPLATE`
    /// (strict ISO-8601, extended `+02:00` offset — matches the git backend's `%aI` shape). Annotate
    /// emits one row per source line and separates them itself — no trailing `\n` here, or every
    /// row would be double-spaced.
    let ANNOTATE_TEMPLATE =
        "commit.change_id().short() ++ \"\\t\" ++ commit.author().name().escape_json() ++ \"\\t\" ++ commit.author().timestamp().format(\"%Y-%m-%dT%H:%M:%S%:z\") ++ \"\\t\" ++ content"

    // --- Helpers -------------------------------------------------------------

    /// Split like Rust's `str::lines()`: break on `\n`, strip one trailing `\r` per
    /// line (so a `\r\n` terminator is consumed), and yield no phantom final empty
    /// line from a trailing newline.
    let private lines (s: string) : string list =
        let parts = s.Split('\n') |> Array.toList
        // Drop the single empty segment a trailing '\n' leaves behind.
        let trimmed =
            match List.rev parts with
            | last :: rest when last.Length = 0 -> List.rev rest
            | _ -> parts

        trimmed
        |> List.map (fun l ->
            if l.EndsWith("\r", StringComparison.Ordinal) then
                l.Substring(0, l.Length - 1)
            else
                l)

    // Verified: no drive-letter heuristic found in jj workspace-path parsing.
    // Match `JjFileset.Path`: only Windows treats backslash as a path separator.
    let private normalize (p: string) =
        if OperatingSystem.IsWindows() then
            p.Replace(char 92, '/')
        else
            p

    /// Hex digit → its 0-15 value, or `-1` for a non-hex char (total; drives the
    /// `\uXXXX` branch of `decodeJsonField`).
    let private hexVal (c: char) : int =
        if c >= '0' && c <= '9' then int c - int '0'
        elif c >= 'a' && c <= 'f' then int c - int 'a' + 10
        elif c >= 'A' && c <= 'F' then int c - int 'A' + 10
        else -1

    /// Decode a single JSON string literal as emitted by a jj template's
    /// `.escape_json()` — e.g. `"a\tb"` → `a⇥b`, `"co,mma"` → `co,mma`. The inverse of
    /// the machine-template framing contract's per-field escaping.
    ///
    /// Lenient by design (these parsers must never throw on unexpected jj output): a
    /// field that is *not* a `"…"` literal is returned verbatim (so a hex id, a flag, or
    /// a legacy raw field passes through unchanged), and a truncated or malformed escape
    /// simply stops decoding rather than erroring. Only the escapes jj's `escape_json`
    /// actually emits are recognised (`\" \\ \/ \b \f \n \r \t \uXXXX`); any other
    /// backslash pair yields its second char.
    let private decodeJsonField (field: string) : string =
        // A JSON string starts with a quote; anything else is returned as-is.
        if field.Length = 0 || field.[0] <> '"' then
            field
        else
            let sb = System.Text.StringBuilder(field.Length)
            let mutable i = 1
            let mutable stop = false

            while not stop && i < field.Length do
                match field.[i] with
                | '"' ->
                    // Closing quote — ignore any trailing bytes.
                    stop <- true
                | '\\' when i + 1 >= field.Length ->
                    // A trailing lone backslash: stop rather than emit a dangling escape.
                    stop <- true
                | '\\' ->
                    (match field.[i + 1] with
                     | '"' -> sb.Append('"') |> ignore
                     | '\\' -> sb.Append('\\') |> ignore
                     | '/' -> sb.Append('/') |> ignore
                     | 'b' -> sb.Append('\b') |> ignore
                     | 'f' -> sb.Append('\f') |> ignore
                     | 'n' -> sb.Append('\n') |> ignore
                     | 'r' -> sb.Append('\r') |> ignore
                     | 't' -> sb.Append('\t') |> ignore
                     | 'u' ->
                         // `\uXXXX` — up to four hex digits. jj only escapes control chars
                         // this way, so a full escape always builds a single BMP char. A
                         // truncated or malformed escape (fewer than four valid hex
                         // digits) stops decoding instead of emitting a partial/NUL char.
                         let mutable code = 0
                         let mutable k = 0
                         let mutable go = true

                         while go && k < 4 && i + 2 + k < field.Length do
                             match hexVal field.[i + 2 + k] with
                             | -1 -> go <- false
                             | d ->
                                 code <- code * 16 + d
                                 k <- k + 1

                         if k = 4 then
                             sb.Append(char code) |> ignore
                         else
                             stop <- true
                         // Skip the hex digits consumed (the `\u` pair is skipped below).
                         i <- i + k
                     | other -> sb.Append(other) |> ignore)

                    // Skip the escaped char (the leading backslash is skipped below).
                    i <- i + 1
                | other -> sb.Append(other) |> ignore

                i <- i + 1

            sb.ToString()

    /// Decode a space-joined list of `.escape_json()` names (the framing contract's list
    /// field) back into the individual names. Splitting on the space is exact — a
    /// bookmark name can never contain one (a git-ref rule jj enforces) — so each token
    /// is one whole JSON string literal.
    let decodeNameList (field: string) : string list =
        field.Split(' ')
        |> Array.filter (fun tok -> tok <> "")
        |> Array.map decodeJsonField
        |> Array.toList

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
            if line.Length = 0 then
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
            if line.Length = 0 then
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
    /// CRLF-terminated source line stays in the content. `line.Split([|'\t'|], 4)` caps the
    /// split at 4 fields, so a literal tab INSIDE the source line's own content (real code)
    /// stays part of `Content` instead of being mistaken for a field separator. The empty
    /// final segment a trailing newline leaves carries no tab, so it fails the `f.Length <
    /// 4` check below and the line numbering stays exact.
    let parseAnnotate (output: string) : AnnotationLine list =
        output.Split('\n')
        |> Array.mapi (fun idx line -> (idx, line))
        |> Array.choose (fun (idx, line) ->
            let f = line.Split([| '\t' |], 4)

            if f.Length < 4 then
                None
            else
                Some
                    { ChangeId = f.[0]
                      Author = decodeJsonField f.[1]
                      Time = f.[2]
                      Line = idx + 1
                      Content = f.[3] })
        |> Array.toList

    // --- Bookmarks -----------------------------------------------------------

    /// Parse rows produced by `BOOKMARK_LIST_TEMPLATE`: `"<name>"\t<full-commit>`, one
    /// row per local bookmark. The name is `.escape_json()`-framed (so a tab/comma/quote
    /// in it round-trips via `decodeJsonField`); a row whose decoded name is empty
    /// contributes nothing. `decodeJsonField` passes a non-quoted field through verbatim,
    /// so a raw/legacy name still parses.
    let parseBookmarks (output: string) : Bookmark list =
        lines output
        |> List.choose (fun line ->
            if line.Length = 0 then
                None
            else
                let f = line.Split('\t')
                let name = decodeJsonField f.[0]

                if name.Length = 0 then
                    None
                else
                    Some
                        { Name = name
                          Target = (if f.Length >= 2 then f.[1] else "") })

    /// Parse rows produced by `BOOKMARK_ALL_TEMPLATE`:
    /// `"<name>"\t<remote>\t<tracked 1/0>\t<full-commit>` per local/remote bookmark. The
    /// name is `.escape_json()`-framed and decoded here (a non-quoted raw/legacy name
    /// passes through); a row whose decoded name is empty contributes nothing.
    let parseBookmarksAll (output: string) : BookmarkRef list =
        lines output
        |> List.choose (fun line ->
            if line.Length = 0 then
                None
            else
                let f = line.Split('\t')
                let name = decodeJsonField f.[0]

                if name.Length = 0 then
                    None
                else
                    let remote = if f.Length >= 2 then f.[1] else ""

                    Some
                        { Name = name
                          Remote = (if remote.Length = 0 then None else Some remote)
                          Tracked = (f.Length >= 3 && f.[2] = "1")
                          Target = (if f.Length >= 4 then f.[3] else "") })

    /// Parse rows produced by `REACHABLE_BOOKMARKS_TEMPLATE`:
    /// `"<name>"[ "<name>"…]\t<full-commit>` (names `.escape_json()`-framed). A commit
    /// with several bookmarks yields one `Bookmark` per name, all sharing that commit as
    /// the target. A row with no bookmark names (empty first field) contributes nothing.
    let parseReachableBookmarks (output: string) : Bookmark list =
        [ for line in lines output do
              if line <> "" then
                  let f = line.Split([| '\t' |], 2)
                  let names = f.[0]
                  let target = if f.Length >= 2 then f.[1] else ""

                  for name in decodeNameList names do
                      yield { Name = name; Target = target } ]

    // --- Resolve / workspaces ------------------------------------------------

    /// Parse `jj file list -T CONFLICTED_PATHS_TEMPLATE` output: one `.escape_json()`-framed
    /// conflicted path per line (the framing contract), decoded and forward-slash normalised.
    ///
    /// This intentionally does **not** parse `jj resolve --list`'s human-readable output.
    /// Investigated on jj 0.42.0: that format renders each conflicted path followed by a
    /// *dynamically sized* run of spaces — padding aligned to the width of the longest
    /// conflicted path in the same invocation, with a minimum of one space — then a human
    /// description (e.g. `file    2-sided conflict`). Because the padding width depends on
    /// the other paths present in that specific call, no fixed separator (neither the
    /// original "first double space" nor a later "last N-space run" attempt) reliably
    /// distinguishes an internal run of spaces in the path from column padding: a single
    /// long conflicted path collapses the padding to exactly one space, indistinguishable
    /// from a real single space in the name, and can even make a naive multi-space search
    /// find no separator at all. `jj file list -T` sidesteps this entirely — it names
    /// exactly the conflicted paths, unambiguously JSON-framed — so it is the reliable
    /// alternative source used here instead.
    let parseResolveList (output: string) : string list =
        lines output
        |> List.choose (fun line ->
            if line.Length = 0 then
                None
            else
                Some(normalize (decodeJsonField line)))

    /// Parse rows produced by `WORKSPACE_TEMPLATE`:
    /// `"<name>"\t<full-commit>\t<bookmarks>`, where the name is `.escape_json()`-framed
    /// (so a name holding a tab/newline still splits on the column separators and round-
    /// trips) and the bookmarks are space-joined `.escape_json()` names (and may be empty).
    let parseWorkspaces (output: string) : Workspace list =
        lines output
        |> List.choose (fun line ->
            if line.Length = 0 then
                None
            else
                let f = line.Split('\t')
                let bookmarks = if f.Length >= 3 then decodeNameList f.[2] else []

                Some
                    { Name = decodeJsonField f.[0]
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
            if line.Length = 0 then
                None
            elif line.Length < 2 || line.[1] <> ' ' then
                None
            else
                let status = line.[0]
                let raw = line.Substring(2)

                if raw.Length = 0 then
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
                | Some tok -> TextParse.parseUInt64Or0 tok
                | None -> 0UL

            if part.Contains "file" then
                files <- n
            elif part.Contains "insertion" then
                insertions <- n
            elif part.Contains "deletion" then
                deletions <- n

        DiffStat.Create(files, insertions, deletions)

    // --- Git remotes ---------------------------------------------------------

    /// Parse `jj git remote list` output: one `<name> <url>` line per configured remote,
    /// space-separated (verified against jj 0.42). The URL is the remainder after the first
    /// space — a remote name carries no whitespace (a git-ref rule jj enforces), so the first
    /// space is an unambiguous separator. A blank or name-less line contributes nothing.
    let parseGitRemoteList (output: string) : Remote list =
        [ for line in lines output do
              let trimmed = line.Trim()

              if trimmed <> "" then
                  match trimmed.IndexOf ' ' with
                  | -1 -> ()
                  | idx ->
                      let name = trimmed.Substring(0, idx)

                      if name <> "" then
                          yield
                              { Name = name
                                Url = (trimmed.Substring(idx + 1)).Trim() } ]
