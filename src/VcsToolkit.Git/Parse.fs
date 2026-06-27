namespace VcsToolkit.Git

open System
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
        Ahead: int option
        /// Commits behind the upstream; `None` when no upstream.
        Behind: int option
        /// Count of changed *tracked* entries (the `1`/`2`/`u` records).
        TrackedChanges: int
        /// Count of untracked files (the `?` records).
        Untracked: int
        /// Count of unmerged (conflicted) entries (the `u` records).
        Conflicts: int
    }

    /// An empty snapshot (all unset / zero).
    static member Empty =
        { Head = None
          Branch = None
          Upstream = None
          Ahead = None
          Behind = None
          TrackedChanges = 0
          Untracked = 0
          Conflicts = 0 }

    /// Whether the working tree has any change at all — tracked or untracked.
    member this.IsDirty = this.TrackedChanges > 0 || this.Untracked > 0

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

/// Pure parsers for git's machine-readable output. No process execution.
[<RequireQualifiedAccess>]
module GitParse =

    let private nul = char 0
    let private unitSep = char 0x1f
    let private lf = char 10
    let private cr = char 13
    let private tab = char 9

    // Digit-only, invariant-culture parse matching Rust's integer `from_str` (rejects
    // signs/whitespace), so a malformed numeric field reads as 0 rather than a sign-led value.
    let private parseIntOr0 (s: string) =
        if s.Length > 0 && s |> Seq.forall Char.IsAsciiDigit then
            match Int32.TryParse(s, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
            | true, v -> v
            | _ -> 0
        else
            0

    /// Lines with terminators stripped (mirrors Rust `str::lines`: strips the `\r` of a
    /// `\r\n`, keeps a bare trailing `\r`, and yields no trailing empty for a final `\n`).
    let private linesOf (text: string) : string[] =
        if text = "" then
            [||]
        else
            let parts = text.Split lf
            let n = parts.Length

            [| for idx in 0 .. n - 1 do
                   let part = parts.[idx]
                   let isLast = idx = n - 1

                   if isLast && part = "" then
                       ()
                   elif (not isLast) && part.EndsWith(string cr, StringComparison.Ordinal) then
                       yield part.Substring(0, part.Length - 1)
                   else
                       yield part |]

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

                let oldPath =
                    if (record.[0] = 'R' || record.[0] = 'C') && i < records.Length then
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

    let private parseSignedPrefix (sign: char) (token: string) : int option =
        if token.Length >= 1 && token.[0] = sign then
            let rest = token.Substring 1

            if rest.Length > 0 && rest |> Seq.forall Char.IsAsciiDigit then
                match
                    Int32.TryParse(rest, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture)
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
        let mutable tracked = 0
        let mutable untracked = 0
        let mutable conflicts = 0
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
                tracked <- tracked + 1
            elif record.StartsWith("2 ", StringComparison.Ordinal) then
                tracked <- tracked + 1
                // The rename/copy original path is the next NUL record; consume it.
                if i < records.Length then
                    i <- i + 1
            elif record.StartsWith("u ", StringComparison.Ordinal) then
                tracked <- tracked + 1
                conflicts <- conflicts + 1
            elif record.StartsWith("? ", StringComparison.Ordinal) then
                untracked <- untracked + 1

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
            let f = record.Split unitSep

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

    /// Parse `git branch` output. The first column is the `* `/`  `/`+ ` marker.
    let parseBranches (output: string) : Branch list =
        linesOf output
        |> Array.filter (fun line -> line.Trim() <> "")
        |> Array.choose (fun line ->
            let current = line.StartsWith("*", StringComparison.Ordinal)
            let name = (if line.Length >= 1 then line.Substring 1 else "").Trim()
            // Skip the detached-HEAD pseudo-entry, e.g. "* (HEAD detached at …)".
            if name = "" || name.StartsWith("(", StringComparison.Ordinal) then
                None
            else
                Some { Name = name; Current = current })
        |> Array.toList

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

        for line in linesOf output do
            if line = "" then
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

        for line in linesOf output do
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
                                match
                                    Int64.TryParse(
                                        value,
                                        Globalization.NumberStyles.Integer,
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

    let private leadingCount (part: string) =
        let toks = part.Split([| ' '; tab |], StringSplitOptions.RemoveEmptyEntries)
        if toks.Length >= 1 then parseIntOr0 toks.[0] else 0

    /// Parse `git diff --shortstat`, e.g. ` 3 files changed, 12 insertions(+), 4 deletions(-)`.
    let parseShortstat (output: string) : DiffStat =
        let mutable files = 0
        let mutable insertions = 0
        let mutable deletions = 0

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
        linesOf output
        |> Array.choose (fun line ->
            match line.IndexOf tab with
            | -1 -> None
            | idx ->
                let refname = (line.Substring(idx + 1)).Trim()

                if refname.StartsWith("refs/heads/", StringComparison.Ordinal) then
                    Some(refname.Substring 11)
                else
                    None)
        |> Array.toList
