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
    let private isGitMarker (path: string) : bool =
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
                let n = fs.Read(buf, 0, buf.Length)
                (Encoding.UTF8.GetString(buf, 0, n)).TrimStart().StartsWith("gitdir:", System.StringComparison.Ordinal)
            with _ ->
                // An unreadable / binary / locked file named `.git` is not a valid
                // marker; treat any read failure as "not a repository marker".
                false
        else
            false

    /// Walk up from `start` to the filesystem root looking for a repository. A `.jj`
    /// directory wins over `.git` (colocated repos are driven through jj); `.git` may be
    /// a directory or a gitlink file. Pure filesystem probing — no subprocess.
    ///
    /// `start` is walked via the parent chain, so pass an **absolute** path to search
    /// ancestors — a relative path like `"."` has no ancestor chain. (`Repo.Open`
    /// absolutises for you.)
    let detect (start: string) : Located option =
        let mutable current = start
        let mutable result = None
        let mutable searching = true

        while searching do
            if Directory.Exists(Path.Combine(current, ".jj")) then
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
