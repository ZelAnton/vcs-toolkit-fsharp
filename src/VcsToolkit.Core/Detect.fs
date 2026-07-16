namespace VcsToolkit.Core

open System.IO
open System.Text

/// The result of `Detect.detect`: which backend, and the repository root it was found at.
type Located =
    {
        /// The detected backend.
        Kind: BackendKind
        /// The directory holding `.git`/`.jj` — the worktree root.
        Root: string
    }

/// Pure filesystem detection of the repository backing a directory.
[<RequireQualifiedAccess>]
module Detect =

    /// Whether `path` (a candidate `.git`) is a real git repository marker — a `.git`
    /// **directory**, or a **gitlink file** (a linked worktree / submodule) whose
    /// content starts with `gitdir:`. A stray/garbage file merely *named* `.git` is
    /// rejected, so it can't shadow a real repository higher up the tree; a binary or
    /// unreadable file is rejected too. Symmetric with the `.jj` `is-dir` probe: both
    /// require a *valid* marker, not mere existence.
    ///
    /// Not `private`: `VcsToolkit.Watch`'s colocation probe (`Paths.stateDirs`) reuses this
    /// exact predicate to decide whether a `.jj` repo is genuinely colocated with git,
    /// rather than re-deriving (and risking drifting from) the same "valid marker" rule.
    let isGitMarker (path: string) : bool =
        if Directory.Exists path then
            true
        elif File.Exists path then
            try
                // A gitlink file is tiny (`gitdir: <path>`), so read only a small prefix:
                // `detect` walks up to the filesystem root, so a huge/garbage file merely
                // named `.git` in an ancestor we don't own must not force an unbounded
                // read. The `gitdir:` marker is ASCII and within the first bytes.
                use fs = File.OpenRead path
                let buf = Array.zeroCreate<byte> 32
                // Loop over short reads (a single `Read` may return fewer than requested), up
                // to 32 bytes or EOF — matching Rust's `take(32).read_to_end`.
                let mutable total = 0
                let mutable n = 1

                while n > 0 && total < buf.Length do
                    n <- fs.Read(buf, total, buf.Length - total)
                    total <- total + n

                (Encoding.UTF8.GetString(buf, 0, total))
                    .TrimStart()
                    .StartsWith("gitdir:", System.StringComparison.Ordinal)
            with _ ->
                // An unreadable / binary / locked file named `.git` is not a valid
                // marker; treat any read failure as "not a repository marker".
                false
        else
            false

    /// Whether `path` (a candidate `.jj`) is a real jj repository marker — a `.jj` **directory**
    /// that owns a `repo` store. A stray/empty `.jj` (an aborted `jj init`, or a bare `mkdir .jj`)
    /// must NOT shadow a healthy colocated `.git` — requiring `.jj/repo` (M19) matches Rust
    /// `is_jj_marker` (`path.is_dir() && path.join("repo").exists()`).
    let private isJjMarker (path: string) : bool =
        Directory.Exists path && Path.Exists(Path.Combine(path, "repo"))

    /// Walk up from `start` to the filesystem root looking for a repository. A valid `.jj`
    /// marker wins over `.git` (colocated repos are driven through jj); `.git` may be a
    /// directory or a gitlink file. Pure filesystem probing — no subprocess.
    ///
    /// `start` is walked via the parent chain, so pass an **absolute** path to search
    /// ancestors — a relative path like `"."` has no ancestor chain. (`Repo.Open`
    /// absolutises for you.)
    let detect (start: string) : Located option =
        let mutable current = start
        let mutable result = None
        let mutable searching = true

        while searching do
            if isJjMarker (Path.Combine(current, ".jj")) then
                result <-
                    Some
                        { Kind = BackendKind.Jj
                          Root = current }

                searching <- false
            elif isGitMarker (Path.Combine(current, ".git")) then
                result <-
                    Some
                        { Kind = BackendKind.Git
                          Root = current }

                searching <- false
            else
                match Path.GetDirectoryName current with
                | null -> searching <- false
                | parent when parent = "" || parent = current -> searching <- false
                | parent -> current <- parent

        result
