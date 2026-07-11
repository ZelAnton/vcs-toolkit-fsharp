namespace VcsToolkit.Core

open System.IO
open ProcessKit
open VcsToolkit.CliSupport
open VcsToolkit.Diff
open VcsToolkit.Git

/// Git-backed implementations of the facade operations: thin calls to the
/// `VcsToolkit.Git` client plus pure mappers from its types into the facade DTOs.
module internal GitBackend =

    /// Map a porcelain `XY` status code to a `ChangeKind`. Rename wins over the others;
    /// an untracked (`??`) or copied (`C`) entry counts as added (a copy is a new file);
    /// unmerged states fold into their underlying kind — use `conflictedFiles` for the
    /// conflict signal.
    let changeKindFromCode (code: string) : ChangeKind =
        if code.Contains 'R' then
            ChangeKind.Renamed
        elif code.Contains 'D' then
            ChangeKind.Deleted
        elif code.Contains 'A' || code.Contains '?' || code.Contains 'C' then
            ChangeKind.Added
        else
            ChangeKind.Modified

    let private fileChangeFromStatus (entry: StatusEntry) : FileChange =
        { Kind = changeKindFromCode entry.Code
          Path = entry.Path
          OldPath = entry.OldPath }

    let currentBranch (git: Git) (dir: string) =
        task {
            let! r = git.CurrentBranch dir
            return ofVcs r
        }

    let trunk (git: Git) (dir: string) =
        task {
            let! r = git.RemoteHeadBranch dir
            return ofVcs r
        }

    let localBranches (git: Git) (dir: string) =
        task {
            match! git.Branches dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok branches -> return Ok(branches |> List.map (fun b -> b.Name))
        }

    let branchExists (git: Git) (dir: string) (name: string) =
        task {
            let! r = git.BranchExists(dir, name)
            return ofVcs r
        }

    let hasUncommittedChanges (git: Git) (dir: string) =
        task {
            match! git.Status dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok entries -> return Ok(not (List.isEmpty entries))
        }

    let hasTrackedChanges (git: Git) (dir: string) =
        task {
            match! git.StatusTracked dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok entries -> return Ok(not (List.isEmpty entries))
        }

    let conflictedFiles (git: Git) (dir: string) =
        task {
            let! r = git.ConflictedFiles dir
            return ofVcs r
        }

    let deleteBranch (git: Git) (dir: string) (name: string) (force: bool) =
        task {
            let! r = git.DeleteBranch(dir, name, force)
            return ofVcs r
        }

    let renameBranch (git: Git) (dir: string) (oldName: string) (newName: string) =
        task {
            let! r = git.RenameBranch(dir, oldName, newName)
            return ofVcs r
        }

    let changedFiles (git: Git) (dir: string) =
        task {
            match! git.Status dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok entries -> return Ok(entries |> List.map fileChangeFromStatus)
        }

    let diffStat (git: Git) (dir: string) =
        task {
            // Working tree vs the last commit. On an unborn repo `HEAD` doesn't resolve,
            // so stat against the empty tree — a fresh repo's working copy then reports
            // its files as additions instead of hard-failing.
            match! git.IsUnborn dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok false ->
                let! r = git.DiffStat(dir, "HEAD")
                return ofVcs r
            | Ok true ->
                // Resolve the repository's actual empty-tree id (stand-in for HEAD in an
                // unborn repo) — not the SHA-1-specific literal, which doesn't exist under
                // `extensions.objectFormat=sha256`.
                match! git.EmptyTreeOid dir with
                | Error e -> return Error(RepoError.Vcs e)
                | Ok emptyTree ->
                    let! r = git.DiffStat(dir, emptyTree)
                    return ofVcs r
        }

    let snapshot (git: Git) (dir: string) =
        task {
            // 1 spawn: branch + upstream + ahead/behind + change counts (porcelain v2).
            match! git.BranchStatus dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok bs ->
                // 1 spawn: resolve the git dir, then a filesystem probe for an interrupted
                // merge/rebase/am/cherry-pick/revert/bisect (porcelain v2 doesn't report it). A
                // git conflict is part of that paused state, so `operation` is one of the git
                // sequencer states here (matching `inProgressState`); the unresolved-files signal
                // is `conflicted`.
                match! git.GitDir dir with
                | Error e -> return Error(RepoError.Vcs e)
                | Ok raw ->
                    let gitDir =
                        if Path.IsPathRooted raw then
                            raw
                        else
                            Path.Combine(dir, raw)

                    let operation =
                        // Same precedence as `inProgressState`: `git am` and an apply-backend
                        // rebase share `rebase-apply/`, but am marks it `applying` — check that
                        // first so an am reads `ApplyMailbox`, not `Rebase` (M20). Cherry-pick and
                        // revert key off their own head file (a conflict there writes
                        // `CHERRY_PICK_HEAD`/`REVERT_HEAD`, never `MERGE_HEAD`); bisect keys off
                        // `BISECT_LOG`.
                        if File.Exists(Path.Combine(gitDir, "MERGE_HEAD")) then
                            OperationState.Merge
                        elif File.Exists(Path.Combine(gitDir, "rebase-apply", "applying")) then
                            OperationState.ApplyMailbox
                        elif
                            Directory.Exists(Path.Combine(gitDir, "rebase-merge"))
                            || Directory.Exists(Path.Combine(gitDir, "rebase-apply"))
                        then
                            OperationState.Rebase
                        elif File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")) then
                            OperationState.CherryPick
                        elif File.Exists(Path.Combine(gitDir, "REVERT_HEAD")) then
                            OperationState.Revert
                        elif File.Exists(Path.Combine(gitDir, "BISECT_LOG")) then
                            OperationState.Bisect
                        else
                            OperationState.Clear

                    let changeCount = bs.TrackedChanges + bs.Untracked
                    // Upstream + ahead/behind travel together: git reports the counts only when an
                    // upstream is set. A set-but-GONE upstream (deleted / not-yet-fetched remote)
                    // keeps `Branch` with `Ahead`/`Behind = None` (uncountable) instead of a
                    // fabricated `Some 0UL` that would read as a false "in sync" (M17).
                    let tracking =
                        bs.Upstream
                        |> Option.map (fun branch ->
                            { Branch = branch
                              Ahead = bs.Ahead
                              Behind = bs.Behind })

                    return
                        Ok
                            { Head = bs.Head
                              Branch = bs.Branch
                              Tracking = tracking
                              Dirty = bs.IsDirty
                              ChangeCount = changeCount
                              Conflicted = bs.Conflicts > 0UL
                              Operation = operation }
        }

    let commitPaths (git: Git) (dir: string) (paths: string list) (message: string) =
        task {
            let! r = git.CommitPaths(dir, CommitPaths.Create(paths, message))
            return ofVcs r
        }

    /// Map a git-typed `Commit` (hash/short-hash/author/date/subject) into the facade DTO. Author
    /// and date are always present on git.
    let private commitFromGit (c: VcsToolkit.Git.Commit) : VcsToolkit.Core.Commit =
        { Id = c.Hash
          Description = c.Subject
          Author = Some c.Author
          Date = Some c.Date }

    let log (git: Git) (dir: string) (revspec: string) (max: int) =
        task {
            match! git.Log(dir, revspec, max) with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok commits -> return Ok(commits |> List.map commitFromGit)
        }

    let logPaths (git: Git) (dir: string) (revspec: string) (max: int) (paths: string list) =
        task {
            match! git.LogPaths(dir, revspec, max, paths) with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok commits -> return Ok(commits |> List.map commitFromGit)
        }

    let fetch (git: Git) (dir: string) =
        task {
            let! r = git.Fetch dir
            return ofVcs r
        }

    let fetchFrom (git: Git) (dir: string) (remote: string) =
        task {
            let! r = git.FetchFrom(dir, remote)
            return ofVcs r
        }

    let fetchBranch (git: Git) (dir: string) (branch: string) =
        task {
            let! r = git.FetchBranch(dir, branch)
            return ofVcs r
        }

    let push (git: Git) (dir: string) (branch: string) =
        task {
            // `-u` so the first facade push also records the upstream; idempotent on later pushes.
            let! r = git.Push(dir, GitPush.Branch(branch).WithUpstream())
            return ofVcs r
        }

    let checkout (git: Git) (dir: string) (reference: string) =
        task {
            let! r = git.Checkout(dir, reference)
            return ofVcs r
        }

    /// Start new work on top of `reference` without modifying it. On git this is
    /// literally `checkout`: the next commit naturally appends on top of `reference`
    /// rather than rewriting it (git has no "detached child change" concept to reach for).
    let newChild (git: Git) (dir: string) (reference: string) = checkout git dir reference

    let rebase (git: Git) (dir: string) (onto: string) =
        task {
            let! r = git.Rebase(dir, onto)
            return ofVcs r
        }

    let inProgressState (git: Git) (dir: string) =
        task {
            // git surfaces an interrupted operation as on-disk state; at most one of these is
            // live, so report whichever is present. The precedence mirrors git's own
            // `wt_status_get_state` (merge → am/rebase → cherry-pick → revert, bisect
            // independent) and is safe because the markers are mutually exclusive in practice: a
            // cherry-pick/revert conflict writes `CHERRY_PICK_HEAD`/`REVERT_HEAD` (never
            // `MERGE_HEAD`), and a rebase that internally cherry-picks does NOT set
            // `CHERRY_PICK_HEAD`. `git am` is checked distinctly from rebase (both use
            // `rebase-apply/`, but am marks it `applying`) so an am isn't mis-aborted with
            // `rebase --abort` (M20). Keep this in step with the `snapshot` probe above.
            match! git.IsMergeInProgress dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok true -> return Ok OperationState.Merge
            | Ok false ->
                match! git.IsAmInProgress dir with
                | Error e -> return Error(RepoError.Vcs e)
                | Ok true -> return Ok OperationState.ApplyMailbox
                | Ok false ->
                    match! git.IsRebaseInProgress dir with
                    | Error e -> return Error(RepoError.Vcs e)
                    | Ok true -> return Ok OperationState.Rebase
                    | Ok false ->
                        match! git.IsCherryPickInProgress dir with
                        | Error e -> return Error(RepoError.Vcs e)
                        | Ok true -> return Ok OperationState.CherryPick
                        | Ok false ->
                            match! git.IsRevertInProgress dir with
                            | Error e -> return Error(RepoError.Vcs e)
                            | Ok true -> return Ok OperationState.Revert
                            | Ok false ->
                                match! git.IsBisectInProgress dir with
                                | Error e -> return Error(RepoError.Vcs e)
                                | Ok true -> return Ok OperationState.Bisect
                                | Ok false -> return Ok OperationState.Clear
        }

    let tryMerge (git: Git) (dir: string) (source: string) =
        task {
            // `--no-ff` so even a fast-forwardable merge stages a real (abortable) merge;
            // `--no-commit` so nothing is committed either way.
            let! merged = git.MergeNoCommit(dir, MergeNoCommit.ForBranch(source).WithNoFf())

            match merged with
            | Ok() ->
                // "Already up to date." exits 0 *without* MERGE_HEAD — only abort an
                // actually-started merge. Detached probe: runs on its own cancellation
                // budget rather than this call's (possibly already-fired) token, so a
                // cancelled/timed-out probe merge still gets cleaned up.
                match! git.IsMergeInProgressDetached dir with
                | Error e -> return Error(RepoError.Vcs e)
                | Ok inProgress ->
                    if inProgress then
                        match! git.MergeAbortDetached dir with
                        | Error e -> return Error(RepoError.Vcs e)
                        | Ok() -> return Ok MergeProbe.Clean
                    else
                        return Ok MergeProbe.Clean
            | Error err when isMergeConflict err ->
                // Collect the conflicted paths BEFORE aborting (`merge --abort` clears
                // the unmerged index entries). Abort first regardless, so a transient
                // read failure can't leave the probe merge staged. Detached abort: see
                // the comment on the success branch above.
                let! files = git.ConflictedFiles dir

                match! git.MergeAbortDetached dir with
                // A failed abort breaks the guaranteed-rollback contract → propagate.
                | Error e -> return Error(RepoError.Vcs e)
                | Ok() -> return ofVcs (files |> Result.map MergeProbe.Conflicts)
            | Error err ->
                // E.g. a dirty-tree refusal or an unknown ref — the merge usually never
                // started, but clean up if it did. Detached probe/abort: see the comment
                // on the success branch above.
                match! git.IsMergeInProgressDetached dir with
                | Error e -> return Error(RepoError.Vcs e)
                | Ok inProgress ->
                    if inProgress then
                        match! git.MergeAbortDetached dir with
                        | Error e -> return Error(RepoError.Vcs e)
                        | Ok() -> return Error(RepoError.Vcs err)
                    else
                        return Error(RepoError.Vcs err)
        }

    let abortInProgress (git: Git) (dir: string) =
        task {
            match! inProgressState git dir with
            | Error e -> return Error e
            | Ok state ->
                let! aborted =
                    task {
                        match state with
                        | OperationState.Merge -> return! git.MergeAbort dir
                        | OperationState.Rebase -> return! git.RebaseAbort dir
                        | OperationState.ApplyMailbox -> return! git.AmAbort dir
                        | OperationState.CherryPick -> return! git.CherryPickAbort dir
                        | OperationState.Revert -> return! git.RevertAbort dir
                        | OperationState.Bisect -> return! git.BisectReset dir
                        | _ -> return Ok()
                    }

                match ofVcs aborted with
                | Error e -> return Error e
                // Recompute rather than assume `Clear` — the return is the post-call state.
                | Ok() -> return! inProgressState git dir
        }

    let continueInProgress (git: Git) (dir: string) =
        task {
            // git refuses to continue while unmerged paths remain; report instead of
            // tripping over the hard error.
            match! git.ConflictedFiles dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok conflicts when not (List.isEmpty conflicts) -> return Ok OperationState.Conflict
            | Ok _ ->
                match! inProgressState git dir with
                | Error e -> return Error e
                | Ok state ->
                    // Run the continue step. `Ok None` = ran cleanly; `Ok (Some Conflict)`
                    // = a continued sequencer step stopped on the next commit's conflict.
                    let! stepOutcome =
                        task {
                            // A merge finishes with a plain commit. The sequencer states (rebase,
                            // cherry-pick, revert) each have a `--continue` that can stop AGAIN on
                            // the next commit's conflict (exit non-zero) — that's `Conflict`, not an
                            // error. Bisect has no continue step, so it is refused explicitly rather
                            // than pretending to succeed while still mid-bisect. `am --continue`
                            // isn't wired here (unchanged).
                            match state with
                            | OperationState.Merge ->
                                match! git.MergeContinue dir with
                                | Error e -> return Error(RepoError.Vcs e)
                                | Ok() -> return Ok None
                            | OperationState.Rebase
                            | OperationState.CherryPick
                            | OperationState.Revert ->
                                let! continued =
                                    task {
                                        match state with
                                        | OperationState.CherryPick -> return! git.CherryPickContinue dir
                                        | OperationState.Revert -> return! git.RevertContinue dir
                                        | _ -> return! git.RebaseContinue dir
                                    }

                                match continued with
                                | Ok() -> return Ok None
                                | Error err ->
                                    match! git.ConflictedFiles dir with
                                    | Error e -> return Error(RepoError.Vcs e)
                                    | Ok c when not (List.isEmpty c) -> return Ok(Some OperationState.Conflict)
                                    | Ok _ -> return Error(RepoError.Vcs err)
                            | OperationState.Bisect ->
                                return
                                    Error(
                                        RepoError.Unsupported(
                                            "a git bisect has no continue step - mark commits with `git bisect "
                                            + "good`/`bad`, or end it with AbortInProgress (`bisect reset`)"
                                        )
                                    )
                            | _ -> return Ok None
                        }

                    match stepOutcome with
                    | Error e -> return Error e
                    | Ok(Some early) -> return Ok early
                    | Ok None ->
                        // Belt and braces: report any unresolved paths the continue left behind.
                        match! git.ConflictedFiles dir with
                        | Error e -> return Error(RepoError.Vcs e)
                        | Ok c when not (List.isEmpty c) -> return Ok OperationState.Conflict
                        | Ok _ -> return! inProgressState git dir
        }

    let listWorktrees (git: Git) (dir: string) =
        task {
            match! git.WorktreeList dir with
            | Error e -> return Error(RepoError.Vcs e)
            | Ok worktrees ->
                return
                    Ok(
                        worktrees
                        |> List.map (fun w ->
                            { Path = w.Path
                              Branch = w.Branch
                              Commit = w.Head
                              IsBare = w.Bare })
                    )
        }

    let createWorktree (git: Git) (dir: string) (path: string) (branch: string) (baseRef: string) =
        task {
            let! r = git.WorktreeAdd(dir, WorktreeAdd.CreateBranch(path, branch, baseRef))
            return ofVcs (r |> Result.map (fun () -> CreateOutcome.Plain))
        }

    let removeWorktree (git: Git) (dir: string) (path: string) (force: bool) =
        task {
            let! r = git.WorktreeRemove(dir, path, force)
            return ofVcs r
        }

    /// The content of `path` as it exists at `rev` (`show <rev>:<path>`), untrimmed.
    let showFile (git: Git) (dir: string) (rev: string) (path: string) =
        task {
            let! r = git.ShowFile(dir, rev, path)
            return ofVcs r
        }
