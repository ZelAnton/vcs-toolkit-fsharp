namespace VcsToolkit.Git

open System
open System.Text
open VcsToolkit.Diff

/// One entry from `git status --porcelain=v1 -z` (`XY <path>`, NUL-delimited).
type StatusEntry =
    {
        /// Two-character status code, e.g. `" M"`, `"??"`, `"A "`, `"R "`.
        Code: string
        /// Path the status applies to (the *new* path for a rename/copy). Raw bytes.
        Path: string
        /// For a rename/copy, the original path; `None` otherwise.
        OldPath: string option
    }

/// A combined branch + working-tree snapshot from `git status --porcelain=v2 --branch -z`.
type BranchStatus =
    {
        /// The HEAD commit's full object id; `None` on an unborn repo.
        Head: string option
        /// Current branch name; `None` when detached.
        Branch: string option
        /// Upstream tracking branch; `None` when unset.
        Upstream: string option
        /// Commits ahead of the upstream; `None` when no upstream.
        Ahead: uint64 option
        /// Commits behind the upstream; `None` when no upstream.
        Behind: uint64 option
        /// Count of changed *tracked* entries (the `1`/`2`/`u` records).
        TrackedChanges: uint64
        /// Count of untracked files (the `?` records).
        Untracked: uint64
        /// Count of unmerged (conflicted) entries (the `u` records).
        Conflicts: uint64
    }

    /// An empty snapshot (all unset / zero).
    static member Empty =
        { Head = None
          Branch = None
          Upstream = None
          Ahead = None
          Behind = None
          TrackedChanges = 0UL
          Untracked = 0UL
          Conflicts = 0UL }

    /// Whether the working tree has any change at all — tracked or untracked.
    member this.IsDirty = this.TrackedChanges > 0UL || this.Untracked > 0UL

/// A commit, parsed from a unit-separator-delimited `git log` line.
type Commit =
    {
        /// Full commit hash (`%H`).
        Hash: string
        /// Abbreviated commit hash (`%h`).
        ShortHash: string
        /// Author name (`%an`).
        Author: string
        /// Author date, strict ISO-8601 (`%aI`).
        Date: string
        /// Subject line (`%s`).
        Subject: string
    }

/// A local branch from `git branch`.
type Branch =
    {
        /// Branch name.
        Name: string
        /// Whether this is the checked-out branch (the `*` marker).
        Current: bool
    }

/// One configured remote from `git remote -v` — a remote name and its URL.
type Remote =
    {
        /// The remote's name, e.g. `origin`.
        Name: string
        /// The remote's URL. `git remote -v` lists a fetch and a push line per remote; this is
        /// the fetch URL (the two are the same unless a push URL was set separately).
        Url: string
    }

/// A worktree from `git worktree list --porcelain`.
type Worktree =
    {
        /// Absolute path to the worktree.
        Path: string
        /// Short branch name (`refs/heads/` stripped); `None` when detached or bare.
        Branch: string option
        /// The checked-out commit (`HEAD <sha>`); `None` for a bare entry.
        Head: string option
        /// The main worktree of a bare repository.
        Bare: bool
        /// Checked out at a detached HEAD (no branch).
        Detached: bool
        /// Locked against pruning.
        Locked: bool
    }

/// One line of `git blame --line-porcelain` output.
type BlameLine =
    {
        /// Full hash of the commit that last changed the line.
        Commit: string
        /// Line number in that commit's version of the file (1-based).
        OrigLine: int
        /// Line number in the blamed version of the file (1-based).
        FinalLine: int
        /// Author name of that commit.
        Author: string
        /// Author timestamp as a unix epoch (seconds).
        AuthorTime: int64
        /// Author timezone offset, e.g. `+0200`.
        AuthorTz: string
        /// The line's content (without the trailing newline).
        Content: string
    }

/// One entry from `git clean -n`/`git clean -f` output (`Would remove <path>` / `Removing
/// <path>`).
type CleanEntry =
    {
        /// The path git would remove (dry run) or actually removed, C-unquoted.
        Path: string
        /// Whether this is a dry-run entry (`Would remove`) rather than an actual removal
        /// (`Removing`).
        DryRun: bool
    }

/// Pure parsers for git's machine-readable output. No process execution.
[<RequireQualifiedAccess>]
module internal GitParse =

    let private nul = char 0
    let private unitSep = char 0x1f
    let private tab = char 9

    /// Decode a git C-quoted path. Mirrors `VcsToolkit.Diff.Parse.unquoteGitPath` exactly (that
    /// helper is `private` to its own module and this project can't reach across the assembly
    /// boundary to it) — git wraps a path in double quotes and C-escapes it when it has a
    /// control byte, a `"`, a `\`, or (default `core.quotePath`) a non-ASCII byte. An unquoted
    /// path is returned unchanged. Octal escapes decode to raw bytes, so a multi-byte UTF-8
    /// filename round-trips; invalid UTF-8 falls back to the replacement char. Decoding stops at
    /// the first unescaped closing quote.
    let private unquoteGitPath (s: string) : string =
        let bytes = Encoding.UTF8.GetBytes s

        if bytes.Length = 0 || bytes.[0] <> byte '"' then
            s
        else
            let out = ResizeArray<byte>(bytes.Length)
            let mutable i = 1
            let mutable stop = false

            while not stop && i < bytes.Length do
                let b = bytes.[i]

                if b = byte '"' then
                    stop <- true
                elif b = byte '\\' && i + 1 < bytes.Length then
                    i <- i + 1
                    let e = bytes.[i]

                    match char e with
                    | 'a' -> out.Add 0x07uy
                    | 'b' -> out.Add 0x08uy
                    | 't' -> out.Add(byte '\t')
                    | 'n' -> out.Add(byte '\n')
                    | 'v' -> out.Add 0x0Buy
                    | 'f' -> out.Add 0x0Cuy
                    | 'r' -> out.Add(byte '\r')
                    | '"' -> out.Add(byte '"')
                    | '\\' -> out.Add(byte '\\')
                    | c when c >= '0' && c <= '7' ->
                        // Up to 3 octal digits → one byte (`\NNN`, NNN ≤ 0o377).
                        let mutable v = uint32 (e - byte '0')
                        let mutable taken = 0

                        while taken < 2
                              && i + 1 < bytes.Length
                              && bytes.[i + 1] >= byte '0'
                              && bytes.[i + 1] <= byte '7' do
                            i <- i + 1
                            v <- v * 8u + uint32 (bytes.[i] - byte '0')
                            taken <- taken + 1

                        out.Add(byte v)
                    | _ -> out.Add e // unknown escape: keep the byte

                    i <- i + 1
                else
                    out.Add b
                    i <- i + 1

            Encoding.UTF8.GetString(out.ToArray())

    // Digit-only, invariant-culture parse matching Rust's integer `from_str` (rejects
    // signs/whitespace), so a malformed numeric field reads as 0 rather than a sign-led value.
    // Deliberately **int32**-width and kept local: git blame's `OrigLine`/`FinalLine` are `int`
    // fields, so a `uint64` parser would not fit them. The `usize`-width fields (diff stats,
    // ahead/behind, change counts) instead use the shared `TextParse.parseUInt64Or0`.
    let private parseIntOr0 (s: string) =
        if s.Length > 0 && s |> Seq.forall Char.IsAsciiDigit then
            match Int32.TryParse(s, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
            | true, v -> v
            | _ -> 0
        else
            0

    /// Parse `git status --porcelain=v1 -z`: NUL-delimited, raw paths. A rename/copy
    /// entry is followed by its source path as the next NUL record.
    let parsePorcelain (output: string) : StatusEntry list =
        let records = output.Split nul |> Array.filter (fun r -> r <> "")
        let entries = ResizeArray<StatusEntry>()
        let mutable i = 0

        while i < records.Length do
            let record = records.[i]
            i <- i + 1
            // "XY path": skip a record without a well-formed ASCII status code.
            if record.Length >= 3 && Char.IsAscii record.[0] && Char.IsAscii record.[1] then
                let code = record.Substring(0, 2)
                let path = record.Substring 3

                // A rename/copy's source path is the next NUL record; consume it. The `R`/`C`
                // can sit in EITHER status column — the index column (`R ` staged rename) or the
                // worktree column (` R` worktree rename) — so check both. Missing the ` R`/` C`
                // case left the source record as a phantom entry with a garbage code/path.
                let oldPath =
                    if
                        (record.[0] = 'R' || record.[0] = 'C' || record.[1] = 'R' || record.[1] = 'C')
                        && i < records.Length
                    then
                        let op = records.[i]
                        i <- i + 1
                        Some op
                    else
                        None

                entries.Add
                    { Code = code
                      Path = path
                      OldPath = oldPath }

        List.ofSeq entries

    let private parseSignedPrefix (sign: char) (token: string) : uint64 option =
        if token.Length >= 1 && token.[0] = sign then
            let rest = token.Substring 1

            if rest.Length > 0 && rest |> Seq.forall Char.IsAsciiDigit then
                match
                    UInt64.TryParse(rest, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture)
                with
                | true, n -> Some n
                | _ -> None
            else
                None
        else
            None

    /// Parse `git status --porcelain=v2 --branch -z` into a `BranchStatus`.
    let parsePorcelainV2 (output: string) : BranchStatus =
        let records = output.Split nul
        let mutable head = None
        let mutable branch = None
        let mutable upstream = None
        let mutable ahead = None
        let mutable behind = None
        let mutable tracked = 0UL
        let mutable untracked = 0UL
        let mutable conflicts = 0UL
        let oidP = "# branch.oid "
        let headP = "# branch.head "
        let upP = "# branch.upstream "
        let abP = "# branch.ab "
        let mutable i = 0

        while i < records.Length do
            let record = records.[i]
            i <- i + 1

            if record.StartsWith(oidP, StringComparison.Ordinal) then
                let rest = record.Substring oidP.Length
                head <- if rest <> "(initial)" then Some rest else None
            elif record.StartsWith(headP, StringComparison.Ordinal) then
                let rest = record.Substring headP.Length
                branch <- if rest <> "(detached)" then Some rest else None
            elif record.StartsWith(upP, StringComparison.Ordinal) then
                upstream <- Some(record.Substring upP.Length)
            elif record.StartsWith(abP, StringComparison.Ordinal) then
                let parts = (record.Substring abP.Length).Split ' '

                ahead <-
                    if parts.Length >= 1 then
                        parseSignedPrefix '+' parts.[0]
                    else
                        None

                behind <-
                    if parts.Length >= 2 then
                        parseSignedPrefix '-' parts.[1]
                    else
                        None
            elif record.StartsWith("1 ", StringComparison.Ordinal) then
                tracked <- tracked + 1UL
            elif record.StartsWith("2 ", StringComparison.Ordinal) then
                tracked <- tracked + 1UL
                // The rename/copy original path is the next NUL record; consume it.
                if i < records.Length then
                    i <- i + 1
            elif record.StartsWith("u ", StringComparison.Ordinal) then
                tracked <- tracked + 1UL
                conflicts <- conflicts + 1UL
            elif record.StartsWith("? ", StringComparison.Ordinal) then
                untracked <- untracked + 1UL

        { Head = head
          Branch = branch
          Upstream = upstream
          Ahead = ahead
          Behind = behind
          TrackedChanges = tracked
          Untracked = untracked
          Conflicts = conflicts }

    /// Parse `git --version` output into the shared `Version`.
    let parseGitVersion (raw: string) : Version option = parseDottedVersion raw

    /// Parse a NUL-delimited path list (e.g. `git diff --name-only -z`).
    let parseNulPaths (output: string) : string list =
        output.Split nul |> Array.filter (fun p -> p <> "") |> Array.toList

    /// Parse `git log -z --format=%H%x1f%h%x1f%an%x1f%aI%x1f%s` output.
    let parseLog (output: string) : Commit list =
        output.Split nul
        |> Array.filter (fun r -> r <> "")
        |> Array.choose (fun record ->
            // Limited to 5 fields so a trailing subject keeps literal 0x1f, like JjParse.parseChanges keeps trailing tabs.
            // A literal 0x1f in %an still shifts later fields; that ambiguity is inherent to this format.
            let f = record.Split(unitSep, 5)

            if f.Length >= 4 then
                Some
                    { Hash = f.[0]
                      ShortHash = f.[1]
                      Author = f.[2]
                      Date = f.[3]
                      Subject = if f.Length >= 5 then f.[4] else "" }
            else
                None)
        |> Array.toList

    /// `stash@{<digits>}` -> the index, or `None` on anything else. Requires both the `stash@{`
    /// prefix and the closing `}` before trusting the digits in between, so a malformed selector
    /// (rather than a wrong number) is skipped like any other unparseable record.
    let private parseStashIndex (gd: string) : uint32 option =
        if
            gd.StartsWith("stash@{", StringComparison.Ordinal)
            && gd.EndsWith("}", StringComparison.Ordinal)
        then
            let inner = gd.Substring(7, gd.Length - 8)

            if inner.Length > 0 && inner |> Seq.forall Char.IsAsciiDigit then
                match
                    UInt32.TryParse(inner, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture)
                with
                | true, v -> Some v
                | _ -> None
            else
                None
        else
            None

    /// Best-effort branch out of a stash reflog subject (`%gs`): recognizes git's own `"WIP on
    /// <branch>: ..."` and `"On <branch>: ..."` shapes. The first `:` after the prefix always
    /// ends the branch component — a ref name can't contain `:` — so this never needs to guess
    /// where the branch name stops even when the trailing message itself contains colons.
    let private parseStashBranch (gs: string) : string option =
        [ "WIP on "; "On " ]
        |> List.tryPick (fun prefix ->
            if gs.StartsWith(prefix, StringComparison.Ordinal) then
                match gs.Substring(prefix.Length).IndexOf ':' with
                | -1 -> None
                | idx -> Some(gs.Substring(prefix.Length, idx))
            else
                None)

    /// Parse `git stash list -z --format=%gd%x1f%H%x1f%gs` into typed `StashEntry`s. Total: a
    /// record whose `%gd` doesn't match `stash@{<digits>}`, or that is missing a field, is
    /// skipped rather than raising.
    let parseStashList (output: string) : StashEntry list =
        output.Split nul
        |> Array.filter (fun r -> r <> "")
        |> Array.choose (fun record ->
            // Limited to 3 fields so the trailing %gs keeps any literal 0x1f/newline it
            // contains, the same convention as `parseLog`'s 5-field cap for %s.
            let f = record.Split(unitSep, 3)

            if f.Length >= 3 then
                parseStashIndex f.[0]
                |> Option.map (fun index ->
                    { Index = index
                      Hash = f.[1]
                      Branch = parseStashBranch f.[2]
                      Message = f.[2] })
            else
                None)
        |> Array.toList

    /// Parse a NUL-delimited `git log -z --format=%H` hash list into git's own commit order
    /// for the queried revspec — the ranking oracle that restores order across `LogPaths`'s
    /// merged chunk results. Same framing as `parseNulPaths`, kept distinct for its role.
    let parseCommitOrder (output: string) : string list =
        output.Split nul |> Array.filter (fun r -> r <> "") |> Array.toList

    /// Parse `git branch` output. The first column is the `* `/`  `/`+ ` marker.
    let parseBranches (output: string) : Branch list =
        TextParse.linesOf output
        |> List.filter (fun line -> line.Trim() <> "")
        |> List.choose (fun line ->
            let current = line.StartsWith("*", StringComparison.Ordinal)
            let name = (if line.Length >= 1 then line.Substring 1 else "").Trim()
            // Skip the detached-HEAD pseudo-entry, e.g. "* (HEAD detached at …)".
            if name.Length = 0 || name.StartsWith("(", StringComparison.Ordinal) then
                None
            else
                Some { Name = name; Current = current })

    /// Parse `git worktree list --porcelain`.
    let parseWorktreePorcelain (output: string) : Worktree list =
        let worktrees = ResizeArray<Worktree>()
        let mutable current: Worktree option = None

        let flush () =
            match current with
            | Some wt ->
                worktrees.Add wt
                current <- None
            | None -> ()

        for line in TextParse.linesOf output do
            if line.Length = 0 then
                flush ()
            else
                let label, value =
                    match line.IndexOf ' ' with
                    | -1 -> line, None
                    | idx -> line.Substring(0, idx), Some(line.Substring(idx + 1))

                match label with
                | "worktree" ->
                    flush ()

                    current <-
                        Some
                            { Path = defaultArg value ""
                              Branch = None
                              Head = None
                              Bare = false
                              Detached = false
                              Locked = false }
                | "HEAD" -> current <- current |> Option.map (fun wt -> { wt with Head = value })
                | "branch" ->
                    current <-
                        current
                        |> Option.map (fun wt ->
                            { wt with
                                Branch =
                                    value
                                    |> Option.map (fun v ->
                                        if v.StartsWith("refs/heads/", StringComparison.Ordinal) then
                                            v.Substring 11
                                        else
                                            v) })
                | "bare" -> current <- current |> Option.map (fun wt -> { wt with Bare = true })
                | "detached" -> current <- current |> Option.map (fun wt -> { wt with Detached = true })
                | "locked" -> current <- current |> Option.map (fun wt -> { wt with Locked = true })
                | _ -> ()

        flush ()
        List.ofSeq worktrees

    let private isHexId (s: string) =
        (s.Length = 40 || s.Length = 64) && s |> Seq.forall Uri.IsHexDigit

    /// Parse `git blame --line-porcelain` output.
    let parseBlamePorcelain (output: string) : BlameLine list =
        let lines = ResizeArray<BlameLine>()
        let mutable current: BlameLine option = None

        for line in TextParse.linesOf output do
            if line.StartsWith(string tab, StringComparison.Ordinal) then
                // Content line: closes the current record.
                match current with
                | Some entry ->
                    lines.Add
                        { entry with
                            Content = line.Substring 1 }

                    current <- None
                | None -> ()
            else
                let label, value =
                    match line.IndexOf ' ' with
                    | -1 -> line, ""
                    | idx -> line.Substring(0, idx), line.Substring(idx + 1)

                if isHexId label then
                    let nums = value.Split ' '
                    let orig = if nums.Length >= 1 then parseIntOr0 nums.[0] else 0
                    let fin = if nums.Length >= 2 then parseIntOr0 nums.[1] else 0

                    current <-
                        Some
                            { Commit = label
                              OrigLine = orig
                              FinalLine = fin
                              Author = ""
                              AuthorTime = 0L
                              AuthorTz = ""
                              Content = "" }
                else
                    match current with
                    | Some entry ->
                        match label with
                        | "author" -> current <- Some { entry with Author = value }
                        | "author-time" ->
                            let t =
                                // Mirror Rust's `value.parse::<i64>()`: an optional leading
                                // sign + digits only, no surrounding whitespace
                                // (`AllowLeadingSign`, not `Integer` which also permits
                                // leading/trailing whitespace). git emits a clean epoch.
                                match
                                    Int64.TryParse(
                                        value,
                                        Globalization.NumberStyles.AllowLeadingSign,
                                        Globalization.CultureInfo.InvariantCulture
                                    )
                                with
                                | true, v -> v
                                | _ -> 0L

                            current <- Some { entry with AuthorTime = t }
                        | "author-tz" -> current <- Some { entry with AuthorTz = value }
                        | _ -> ()
                    | None -> ()

        List.ofSeq lines

    let private leadingCount (part: string) : uint64 =
        let toks = part.Split([| ' '; tab |], StringSplitOptions.RemoveEmptyEntries)

        if toks.Length >= 1 then
            TextParse.parseUInt64Or0 toks.[0]
        else
            0UL

    /// Parse `git diff --shortstat`, e.g. ` 3 files changed, 12 insertions(+), 4 deletions(-)`.
    let parseShortstat (output: string) : DiffStat =
        let mutable files = 0UL
        let mutable insertions = 0UL
        let mutable deletions = 0UL

        for raw in output.Split ',' do
            let part = raw.Trim()
            let n = leadingCount part

            if part.Contains "file" then
                files <- n
            elif part.Contains "insertion" then
                insertions <- n
            elif part.Contains "deletion" then
                deletions <- n

        DiffStat.Create(files, insertions, deletions)

    /// Parse `git ls-remote --heads <remote>` output into the bare branch names.
    let parseLsRemoteHeads (output: string) : string list =
        TextParse.linesOf output
        |> List.choose (fun line ->
            match line.IndexOf tab with
            | -1 -> None
            | idx ->
                let refname = (line.Substring(idx + 1)).Trim()

                if refname.StartsWith("refs/heads/", StringComparison.Ordinal) then
                    Some(refname.Substring 11)
                else
                    None)

    /// Parse `git remote -v` into one `Remote` per configured remote. git prints two lines per
    /// remote — `<name>\t<url> (fetch)` and `<name>\t<url> (push)` — so this deduplicates to a
    /// single entry per name, keeping the first URL seen (the fetch line, which git emits before
    /// the push one). The trailing ` (fetch)`/` (push)` marker drops off: a URL never contains
    /// whitespace, so the URL is the first whitespace-delimited token of the tab-separated value.
    let parseRemotes (output: string) : Remote list =
        let seen = System.Collections.Generic.HashSet<string>()
        let remotes = ResizeArray<Remote>()

        for line in TextParse.linesOf output do
            match line.IndexOf tab with
            | -1 -> ()
            | idx ->
                let name = line.Substring(0, idx)

                if name <> "" && seen.Add name then
                    let url =
                        match
                            (line.Substring(idx + 1)).Split([| ' '; tab |], StringSplitOptions.RemoveEmptyEntries)
                        with
                        | [||] -> ""
                        | parts -> parts.[0]

                    remotes.Add { Name = name; Url = url }

        List.ofSeq remotes

    /// Parse `git clean -n`/`git clean -f` output: `Would remove <path>` (dry run) or `Removing
    /// <path>` (real removal), one per line. Paths are C-quoted like any other git path output,
    /// so `unquoteGitPath` decodes each — never a naive substring cut, which would leave escape
    /// sequences (or the surrounding quotes) verbatim in the returned path. A line matching
    /// neither prefix is skipped rather than raising.
    let parseClean (output: string) : CleanEntry list =
        let wouldRemove = "Would remove "
        let removing = "Removing "

        TextParse.linesOf output
        |> List.choose (fun line ->
            if line.StartsWith(wouldRemove, StringComparison.Ordinal) then
                Some
                    { Path = unquoteGitPath (line.Substring wouldRemove.Length)
                      DryRun = true }
            elif line.StartsWith(removing, StringComparison.Ordinal) then
                Some
                    { Path = unquoteGitPath (line.Substring removing.Length)
                      DryRun = false }
            else
                None)

    /// Parse `git config --file .gitmodules --list -z` into one `Submodule` per configured
    /// submodule, in first-seen order. The `-z` framing terminates each `key\nvalue` entry with
    /// a NUL (and separates the key from its value with a newline); a valueless boolean key has
    /// no newline. Only `submodule.<name>.{path,url,branch}` entries are consumed — the variable
    /// name is the FINAL dot-component, so everything between `submodule.` and the last dot is
    /// the submodule's logical name (which may itself contain dots or spaces). Total: an entry
    /// that isn't a `submodule.<name>.<var>` key, or names an unrecognised variable, is ignored
    /// rather than raising. A submodule that appears with no `path`/`url` still yields a record
    /// (empty fields) so a malformed `.gitmodules` degrades to a best-effort list.
    let parseSubmoduleConfig (output: string) : Submodule list =
        let prefix = "submodule."
        let order = System.Collections.Generic.List<string>()
        let byName = System.Collections.Generic.Dictionary<string, Submodule>()

        for record in output.Split nul do
            if record <> "" then
                let key, value =
                    match record.IndexOf '\n' with
                    | -1 -> record, ""
                    | idx -> record.Substring(0, idx), record.Substring(idx + 1)

                if key.StartsWith(prefix, StringComparison.Ordinal) then
                    let rest = key.Substring prefix.Length

                    match rest.LastIndexOf '.' with
                    | -1 -> () // no variable component — not a `submodule.<name>.<var>` key
                    | dot ->
                        let name = rest.Substring(0, dot)
                        let field = rest.Substring(dot + 1)

                        let existing =
                            match byName.TryGetValue name with
                            | true, sm -> sm
                            | _ ->
                                order.Add name

                                { Name = name
                                  Path = ""
                                  Url = ""
                                  Branch = None }

                        let updated =
                            match field with
                            | "path" -> { existing with Path = value }
                            | "url" -> { existing with Url = value }
                            | "branch" -> { existing with Branch = Some value }
                            | _ -> existing

                        byName.[name] <- updated

        [ for name in order -> byName.[name] ]

    /// Decode a `git submodule status` line's leading marker character into a `SubmoduleState`.
    /// Anything other than `-`/`+`/`U` (in practice a leading space) reads as `Current`.
    let private submoduleState (marker: char) : SubmoduleState =
        match marker with
        | '-' -> SubmoduleState.Uninitialized
        | '+' -> SubmoduleState.OutOfSync
        | 'U' -> SubmoduleState.Conflict
        | _ -> SubmoduleState.Current

    /// Parse `git submodule status` into typed `SubmoduleStatus` entries. Each line is
    /// `<marker><commit> <path>[ (<describe>)]`: the first character is the sync marker, the
    /// object id runs to the first space, the remainder is the path plus an optional
    /// parenthesised `git describe`. Total: a line with no space after the id is skipped.
    let parseSubmoduleStatus (output: string) : SubmoduleStatus list =
        TextParse.linesOf output
        |> List.choose (fun line ->
            if line.Length = 0 then
                None
            else
                let state = submoduleState line.[0]
                let rest = line.Substring 1

                match rest.IndexOf ' ' with
                | -1 -> None // no path field — malformed, skip
                | sp ->
                    let commit = rest.Substring(0, sp)
                    let after = rest.Substring(sp + 1)

                    // An optional ` (describe)` suffix follows the path; the path itself may
                    // contain spaces, so peel the suffix from the END rather than splitting.
                    let path, describe =
                        if after.EndsWith(")", StringComparison.Ordinal) then
                            match after.LastIndexOf(" (", StringComparison.Ordinal) with
                            | -1 -> after, None
                            | idx -> after.Substring(0, idx), Some(after.Substring(idx + 2, after.Length - idx - 3))
                        else
                            after, None

                    Some
                        { Path = path
                          Commit = commit
                          State = state
                          Describe = describe })
