namespace VcsToolkit.Core

open System.IO
open System.Threading.Tasks
open ProcessKit
open VcsToolkit.Diff
open VcsToolkit.Jj

/// Jujutsu-backed implementations of the facade operations. jj's model differs from
/// git's: workspaces are *named*, not path-addressed, and `jj workspace list` carries no
/// path — so worktree lookups resolve a name by matching `jj workspace root --name <n>`
/// against the requested path. The copy-on-write / op-log-rollback creation flow stays
/// in the consumer; the facade only does the plain `jj workspace add` path.
module internal JjBackend =

    // One `jj log -r @` template carrying the working-copy fields the snapshot needs
    // except the change count: the full commit id, the `empty` flag (→ dirty), and the
    // `conflict` flag — all bare keywords valid in the `jj log` commit context.
    [<Literal>]
    let private SnapshotTemplate =
        "commit_id ++ \"\\t\" ++ if(empty, \"1\", \"0\") ++ \"\\t\" ++ if(conflict, \"1\", \"0\")"

    /// Map a `jj diff --summary` status letter to a `ChangeKind`.
    let changeKindFromStatus (status: char) : ChangeKind =
        match status with
        | 'A'
        | 'C' -> ChangeKind.Added
        | 'D' -> ChangeKind.Deleted
        | 'R' -> ChangeKind.Renamed
        | _ -> ChangeKind.Modified

    let private fileChangeFromSummary (entry: ChangedPath) : FileChange =
        { Kind = changeKindFromStatus entry.Status
          Path = entry.Path
          OldPath = entry.OldPath }

    /// Derive a jj workspace name from a branch name. jj workspace names must be valid
    /// identifiers, so substitute path/whitespace characters with `_`. Deterministic so a
    /// later lookup can reconstruct it.
    let workspaceNameFor (branch: string) : string =
        branch
        |> String.map (fun c ->
            match c with
            | '/'
            | '\\'
            | '.'
            | ':'
            | ' '
            | '\t'
            | '\n'
            | '\r' -> '_'
            | other -> other)

    /// Zip two lists truncating to the shorter — matching the Rust backend's defensive,
    /// truncating `zip` (F#'s `List.zip` *throws* on a length mismatch, which inside a
    /// `task` would surface as a faulted Task rather than the `Result` the facade
    /// contracts). `WorkspaceRoots` returns exactly one root per name, so this is a no-op
    /// today; it only degrades gracefully if that 1:1 contract ever drifts.
    let private zipTruncating (xs: 'a list) (ys: 'b list) : ('a * 'b) list =
        let n = min (List.length xs) (List.length ys)
        List.zip (List.truncate n xs) (List.truncate n ys)

    /// Normalise a path for comparison against jj's `workspace root` output: the lexical
    /// full path, upgraded to the resolved final target when the leaf is a symlink (jj
    /// emits a canonicalized root). Falls back to the raw string on a malformed path.
    ///
    /// Limitation vs Rust's `canonicalize`: .NET has no built-in realpath, so a symlinked
    /// *ancestor* (e.g. macOS `/var` → `/private/var`) is not resolved — the raw-string
    /// equality fallback in the caller covers the case where the caller and jj agree on
    /// the path form.
    let private normalizeForCompare (p: string) : string =
        let full =
            try
                Path.GetFullPath p
            with _ ->
                p

        try
            match Directory.ResolveLinkTarget(full, true) with
            | null -> full
            | target -> target.FullName
        with _ ->
            // Not a symlink / inaccessible — the lexical full path is the best we can do.
            full

    /// Find the workspace name whose `jj workspace root` matches `path`. Uses jj's
    /// recorded name rather than a re-derived guess, so a branch containing `/` resolves.
    let private workspaceNameForPath (jj: Jj) (dir: string) (path: string) : Task<Result<string, RepoError>> =
        task {
            let target = normalizeForCompare path

            match! jj.WorkspaceList dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok workspaces ->
                let names = workspaces |> List.map (fun ws -> ws.Name)
                let! roots = jj.WorkspaceRoots(dir, names)
                let paired = zipTruncating workspaces roots

                let found =
                    paired
                    |> List.tryPick (fun (ws, root) ->
                        match root with
                        | Error _ -> None
                        | Ok rootPath ->
                            if normalizeForCompare rootPath = target || rootPath = path then
                                Some ws.Name
                            else
                                None)

                match found with
                | Some name -> return Ok name
                | None ->
                    // A genuine miss (every registered workspace resolved, none matched
                    // `path`) is `WorktreeNotFound`. A *failed* per-name probe collapsing
                    // into the same `None` via `Error _ -> None` above would otherwise
                    // masquerade as the same not-found result — surface it separately,
                    // naming the workspaces whose `workspace root --name` failed, so a
                    // real jj/probe failure isn't misreported as a missing worktree.
                    let failed =
                        paired
                        |> List.choose (fun (ws, root) ->
                            match root with
                            | Error e -> Some(ws.Name, e.Message)
                            | Ok _ -> None)

                    if List.isEmpty failed then
                        return Error(RepoError.WorktreeNotFound path)
                    else
                        let detail =
                            failed
                            |> List.map (fun (name, msg) -> sprintf "%s (%s)" name msg)
                            |> String.concat "; "

                        return
                            Error(
                                RepoError.Io(
                                    sprintf
                                        "failed to resolve workspace root while looking up worktree %s: %s"
                                        path
                                        detail
                                )
                            )
        }

    let currentBranch (jj: Jj) (dir: string) =
        task {
            // jj has no "current branch" in the git sense; report the nearest bookmark
            // reachable from `@` (`heads(::@ & bookmarks())`), which stays non-empty
            // across a commit. Tie-break: pick the lexicographically-smallest name so the
            // answer is deterministic instead of dependent on jj's row order.
            match! jj.ReachableBookmarks dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok bookmarks ->
                let names = bookmarks |> List.map (fun b -> b.Name)

                return Ok(if List.isEmpty names then None else Some(List.min names))
        }

    let trunk (jj: Jj) (dir: string) =
        task {
            let! r = jj.Trunk dir
            return ofVcs r
        }

    let localBranches (jj: Jj) (dir: string) =
        task {
            match! jj.Bookmarks dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok bookmarks -> return Ok(bookmarks |> List.map (fun b -> b.Name))
        }

    let branchExists (jj: Jj) (dir: string) (name: string) =
        task {
            // jj has no direct existence probe; scan the local bookmarks.
            match! jj.Bookmarks dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok bookmarks -> return Ok(bookmarks |> List.exists (fun b -> b.Name = name))
        }

    let hasUncommittedChanges (jj: Jj) (dir: string) =
        task {
            match! jj.CurrentChange dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok change when not change.Empty -> return Ok true
            | Ok _ ->
                // A **conflicted** change is uncommitted state (it needs resolution) even when jj
                // marks it `empty` — so this agrees with `snapshot`'s `conflict ⇒ dirty`. Only
                // probed when `@` is empty, so the common non-empty case stays a single query.
                match! jj.HasWorkingCopyConflict dir with
                | Error e -> return Error(RepoError.Vcs e)
                | Ok conflicted -> return Ok conflicted
        }

    let conflictedFiles (jj: Jj) (dir: string) =
        task {
            let! r = jj.ResolveList(dir, "@")
            return ofVcs r
        }

    let deleteBranch (jj: Jj) (dir: string) (name: string) =
        task {
            let! r = jj.BookmarkDelete(dir, name)
            return ofVcs r
        }

    let renameBranch (jj: Jj) (dir: string) (oldName: string) (newName: string) =
        task {
            let! r = jj.BookmarkRename(dir, oldName, newName)
            return ofVcs r
        }

    let changedFiles (jj: Jj) (dir: string) =
        task {
            match! jj.Status dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok entries -> return Ok(entries |> List.map fileChangeFromSummary)
        }

    let diffStat (jj: Jj) (dir: string) =
        task {
            let! r = jj.DiffStat(dir, "@")
            return ofVcs r
        }

    let snapshot (jj: Jj) (dir: string) =
        task {
            // Spawn 1: head/empty/conflict for `@`.
            match! jj.TemplateQuery(dir, "@", SnapshotTemplate, Some 1) with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok row ->
                let fields = (row.TrimEnd([| '\r'; '\n' |])).Split('\t')

                let head =
                    if fields.Length > 0 && fields.[0] <> "" then
                        Some fields.[0]
                    else
                        None

                // Spawn 2: `branch` via the nearest reachable bookmark.
                match! currentBranch jj dir with
                | Error e -> return Error e
                | Ok branch ->
                    // Read the flags explicitly: `conflict == "1"` ⇒ conflicted; `empty ==
                    // "0"` ⇒ non-empty ⇒ dirty (so a missing/garbled field falls to clean).
                    // A conflicted change is also dirty even when jj marks it empty.
                    let conflicted = fields.Length > 2 && fields.[2] = "1"
                    let dirty = (fields.Length > 1 && fields.[1] = "0") || conflicted

                    let operation =
                        if conflicted then
                            OperationState.Conflict
                        else
                            OperationState.Clear

                    // Spawn 3, only when dirty: the change count.
                    let! countResult =
                        task {
                            if dirty then
                                match! jj.Status dir with
                                | Error e -> return Error(RepoError.Vcs e)
                                | Ok entries -> return Ok(uint64 (List.length entries))
                            else
                                return Ok 0UL
                        }

                    match countResult with
                    | Error e -> return Error e
                    | Ok changeCount ->
                        return
                            Ok
                                { Head = head
                                  Branch = branch
                                  // jj has no git-style upstream tracking.
                                  Tracking = None
                                  Dirty = dirty
                                  ChangeCount = changeCount
                                  Conflicted = conflicted
                                  Operation = operation }
        }

    let commitPaths (jj: Jj) (dir: string) (paths: string list) (message: string) =
        task {
            let filesets = paths |> List.map JjFileset.Path
            let! r = jj.CommitPaths(dir, filesets, message)
            return ofVcs r
        }

    /// Map a jj-typed `Change` (change-id/commit-id/empty/description) into the facade DTO. jj's
    /// typed log surfaces no authorship or timestamp, so `Author`/`Date` are `None` (see the
    /// `Commit` DTO docs) rather than a fabricated value.
    let private commitFromChange (c: Change) : VcsToolkit.Core.Commit =
        { Id = c.CommitId
          Description = c.Description
          Author = None
          Date = None }

    let log (jj: Jj) (dir: string) (revset: string) (max: int) =
        task {
            match! jj.Log(dir, revset, max) with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok changes -> return Ok(changes |> List.map commitFromChange)
        }

    let logPaths (jj: Jj) (dir: string) (revset: string) (max: int) (paths: string list) =
        task {
            let filesets = paths |> List.map JjFileset.Path

            match! jj.LogPaths(dir, revset, max, filesets) with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok changes -> return Ok(changes |> List.map commitFromChange)
        }

    let fetch (jj: Jj) (dir: string) =
        task {
            let! r = jj.GitFetch dir
            return ofVcs r
        }

    let fetchFrom (jj: Jj) (dir: string) (remote: string) =
        task {
            let! r = jj.GitFetchFrom(dir, remote)
            return ofVcs r
        }

    let fetchBranch (jj: Jj) (dir: string) (branch: string) =
        task {
            let! r = jj.GitFetchBranch(dir, branch)
            return ofVcs r
        }

    let push (jj: Jj) (dir: string) (branch: string) =
        task {
            // jj pushes *bookmark state* (`git push -b <name>`); no `-u` analogue.
            let! r = jj.GitPush(dir, Some branch)
            return ofVcs r
        }

    let checkout (jj: Jj) (dir: string) (reference: string) =
        task {
            // jj has no "switch branch"; moving `@` to the bookmark/revision is the
            // equivalent of a git checkout.
            let! r = jj.Edit(dir, reference)
            return ofVcs r
        }

    /// Start new work on top of `reference` without modifying it (`jj new <reference>`),
    /// creating an undescribed child change. Unlike `checkout` (`jj edit`), `reference`'s
    /// commit is left untouched — the new change is stacked on top of it.
    let newChild (jj: Jj) (dir: string) (reference: string) =
        task {
            let! r = jj.NewChild(dir, reference)
            return ofVcs r
        }

    let rebase (jj: Jj) (dir: string) (onto: string) =
        task {
            let! r = jj.Rebase(dir, onto)
            return ofVcs r
        }

    let inProgressState (jj: Jj) (dir: string) =
        task {
            // jj operations are atomic — there is no paused merge/rebase. A conflict is
            // recorded on the working-copy change instead.
            match! jj.HasWorkingCopyConflict dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok true -> return Ok OperationState.Conflict
            | Ok false -> return Ok OperationState.Clear
        }

    /// Collapse a `RollbackTo` outcome into the facade's unit result. For `tryMerge` the
    /// rollback is not best-effort: the probe **must** leave no trace, so a refused rollback
    /// (the op-log diverged — a concurrent operation advanced past the captured op) means the
    /// probe change is still in the working copy. That is a failed rollback, surfaced as an
    /// error rather than a `MergeProbe` result that would misdescribe the on-disk state.
    let private rollbackToUnit (r: Result<RollbackOutcome, ProcessError>) : Result<unit, RepoError> =
        match r with
        | Error e -> Error(RepoError.Vcs e)
        | Ok RollbackOutcome.RolledBack -> Ok()
        | Ok(RollbackOutcome.SkippedDiverged(captured, current)) ->
            Error(
                RepoError.Vcs(
                    ProcessError.Spawn(
                        "jj",
                        sprintf
                            "try_merge rollback refused: the operation log diverged from the captured op %s (now at %s) — a concurrent operation advanced past it, so the probe change was left in place rather than clobbering that work"
                            captured
                            current
                    )
                )
            )

    let tryMerge (jj: Jj) (dir: string) (source: string) =
        task {
            // Capture the rollback point BEFORE any mutation.
            match! jj.OpHead dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok preOp ->
                // Materialise the merge as a new working-copy change; jj records
                // conflicts on the commit instead of failing, so a 0 exit does NOT mean
                // "clean".
                let! merged = jj.NewMerge(dir, "vcs-core try_merge probe (rolled back)", [ "@"; source ])

                // Probe the outcome before restoring (the probe target disappears after).
                let! probe =
                    task {
                        match! jj.IsConflicted(dir, "@") with
                        | Error e -> return Error e
                        | Ok true ->
                            match! jj.ResolveList(dir, "@") with
                            | Error e -> return Error e
                            | Ok files -> return Ok(Some files)
                        | Ok false -> return Ok None
                    }

                // Always roll back — also when the merge or the probe errored. The shared
                // protocol runs the cleanup on a fresh cancellation budget and refuses to
                // restore if a concurrent operation advanced the op-head past `preOp` (which,
                // for a probe that must leave no trace, `rollbackToUnit` treats as an error).
                let! rolledBack = jj.RollbackTo(dir, preOp)
                let restored = rollbackToUnit rolledBack

                match merged, probe with
                | Ok(), Ok conflicts ->
                    // The probe is only trustworthy if the rollback actually happened.
                    match restored with
                    | Error e -> return Error e
                    | Ok() ->
                        match conflicts with
                        | Some files -> return Ok(MergeProbe.Conflicts files)
                        | None -> return Ok MergeProbe.Clean
                | Ok(), Error err ->
                    // The merge succeeded but the probe errored. Surface a *failed*
                    // rollback first (the probe change is still in the working copy),
                    // otherwise surface the probe error.
                    match restored with
                    | Error e -> return Error e
                    | Ok() -> return Error(RepoError.Vcs err)
                | Error err, _ ->
                    // The merge itself failed. If the rollback failed too, preserve both:
                    // the probe change may still be present and the root cause matters.
                    match restored with
                    | Error rollbackError ->
                        return
                            Error(
                                RepoError.Io(
                                    sprintf
                                        "try_merge failed: NewMerge failed (%s); rollback cleanup also failed: RollbackTo failed (%s)"
                                        err.Message
                                        rollbackError.Message
                                )
                            )
                    | Ok() -> return Error(RepoError.Vcs err)
        }

    // jj has no paused operations: abort/continue only *report* the current state (roll
    // back explicitly via the jj client's `Transaction`/`OpRestore`).
    let abortInProgress (jj: Jj) (dir: string) = inProgressState jj dir
    let continueInProgress (jj: Jj) (dir: string) = inProgressState jj dir

    let listWorktrees (jj: Jj) (dir: string) =
        task {
            // jj's `Workspace` carries no path, so resolve each via `workspace root` —
            // batched in one bounded fan-out. `WorkspaceRoots` returns one result per
            // name, so the `zip` is 1:1.
            match! jj.WorkspaceList dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok workspaces ->
                let names = workspaces |> List.map (fun ws -> ws.Name)
                let! roots = jj.WorkspaceRoots(dir, names)

                let out =
                    zipTruncating workspaces roots
                    |> List.choose (fun (ws, root) ->
                        match root with
                        | Error _ -> None // No useful entry without a path.
                        | Ok rootPath ->
                            Some
                                { Path = rootPath
                                  Branch = ws.Bookmarks |> List.tryHead
                                  Commit = (if ws.Commit <> "" then Some ws.Commit else None)
                                  IsBare = false })

                return Ok out
        }

    let createWorktree (jj: Jj) (dir: string) (path: string) (branch: string) (baseRef: string) =
        task {
            let wsName = workspaceNameFor branch
            // `jj workspace add` resolves a relative `path` against `dir`; resolve it the
            // same way so a `Repo` bound to a dir != the process cwd probes/deletes the
            // location jj actually used. `Path.Combine` returns `path` unchanged when it
            // is already absolute.
            let absPath = Path.Combine(dir, path)
            // Whether the destination existed *before* we touched it — a pre-existing
            // directory the caller already had is not ours to delete on rollback.
            let preexisting = Directory.Exists absPath || File.Exists absPath

            match! jj.WorkspaceAdd(dir, WorkspaceAdd.Create(wsName, baseRef, path)) with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok() ->
                // `workspace add -r <base>` puts a fresh empty change on the new
                // workspace's `@`; `<ws_name>@` resolves to it. Anchor the bookmark there.
                match! jj.BookmarkCreate(dir, branch, wsName + "@") with
                | Ok() -> return Ok CreateOutcome.Plain
                | Error e ->
                    // The two steps aren't atomic. Roll back so a failed call doesn't leak
                    // a half-made worktree: remove the dir only if we created it, forget
                    // the workspace, then surface the original error — composed with any
                    // rollback-step failure rather than swallowing it, so a broken rollback
                    // isn't silently mistaken for a clean one.
                    let dirCleanupFailure =
                        if not preexisting && Directory.Exists absPath then
                            try
                                Directory.Delete(absPath, true)
                                None
                            with ex ->
                                Some(
                                    sprintf
                                        "failed to remove partially-created worktree directory %s: %s"
                                        absPath
                                        ex.Message
                                )
                        else
                            None

                    let! forgetResult = jj.WorkspaceForget(dir, wsName)

                    let forgetFailure =
                        match forgetResult with
                        | Ok() -> None
                        | Error fe -> Some(sprintf "failed to forget workspace %s: %s" wsName fe.Message)

                    match [ dirCleanupFailure; forgetFailure ] |> List.choose id with
                    | [] -> return Error(RepoError.Vcs e)
                    | cleanupFailures ->
                        return
                            Error(
                                RepoError.Io(
                                    sprintf
                                        "failed to create worktree: BookmarkCreate failed (%s); rollback cleanup also failed: %s"
                                        e.Message
                                        (String.concat "; " cleanupFailures)
                                )
                            )
        }

    /// jj's initial workspace — its directory is the repository's main working copy, so it
    /// must never be deleted by a worktree-removal call.
    [<Literal>]
    let private DEFAULT_WORKSPACE = "default"

    let removeWorktree (jj: Jj) (dir: string) (path: string) (force: bool) =
        task {
            // Resolve `path` against `dir` (jj's cwd) so the lookup and the dir removal
            // target the location jj used, even when the process cwd differs.
            let absPath = Path.Combine(dir, path)

            match! workspaceNameForPath jj dir absPath with
            | Error e -> return Error e
            | Ok name ->
                // Never remove the repository's **main** workspace: its directory *is* the main
                // working copy, so deleting it wipes the whole checkout (`.jj`/`.git` and every
                // file). git refuses to remove its main worktree; jj has no such guard and we
                // delete the directory ourselves, so guard it here. Two signals (either alone is
                // bypassable): the name is `default` (the initial workspace), which `jj workspace
                // rename` can move off — OR the workspace owns the object store: a main
                // workspace's `.jj/repo` is a **directory** (the store), a secondary's is a
                // **file** (a pointer), stable across a rename.
                let ownsStore = Directory.Exists(Path.Combine(absPath, ".jj", "repo"))

                if name = DEFAULT_WORKSPACE || ownsStore then
                    return
                        Error(
                            RepoError.InvalidInput
                                "refusing to remove the repository's main workspace (its directory is the main working copy and owns the object store)"
                        )
                else
                    // Honor `force` like git's `worktree remove`: unless forced, refuse a
                    // workspace that still has uncommitted changes. Querying `current_change`
                    // there snapshots the working copy first, so a refusal leaves the edits in
                    // jj's op log rather than only on disk. (Skip when the dir is already gone.)
                    let! dirtyCheck =
                        task {
                            if not force && Directory.Exists absPath then
                                match! jj.CurrentChange absPath with
                                | Error e -> return Error(RepoError.Vcs e)
                                | Ok change when not change.Empty ->
                                    return
                                        Error(
                                            RepoError.InvalidInput
                                                "worktree has uncommitted changes; pass force = true to remove it (the changes are snapshotted in jj's op log and recoverable)"
                                        )
                                | Ok _ -> return Ok()
                            else
                                return Ok()
                        }

                    match dirtyCheck with
                    | Error e -> return Error e
                    | Ok() ->
                        // Delete the on-disk dir first: an orphan dir jj has forgotten is worse
                        // than a still-attached workspace.
                        let deleteResult =
                            if Directory.Exists absPath then
                                try
                                    Directory.Delete(absPath, true)
                                    Ok()
                                with ex ->
                                    Error(RepoError.Io ex.Message)
                            else
                                Ok()

                        match deleteResult with
                        | Error e -> return Error e
                        | Ok() ->
                            // Then forget the workspace. jj happily forgets an already-deleted
                            // workspace dir; surface a failure rather than swallow it.
                            let! r = jj.WorkspaceForget(dir, name)
                            return ofVcs r
        }

    /// The content of `path` as it exists at `revset` (`file show -r <revset> <path>`),
    /// untrimmed and UTF-8-decoded (non-UTF-8 bytes become U+FFFD — see `showFileBytes` for a
    /// verbatim read).
    let showFile (jj: Jj) (dir: string) (revset: string) (path: string) =
        task {
            let! r = jj.FileShow(dir, revset, path)
            return ofVcs r
        }

    /// The content of `path` at `revset` (`file show -r <revset> <path>`) as raw, verbatim bytes.
    let showFileBytes (jj: Jj) (dir: string) (revset: string) (path: string) =
        task {
            let! r = jj.FileShowBytes(dir, revset, path)
            return ofVcs r
        }
