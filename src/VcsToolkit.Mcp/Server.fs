namespace VcsToolkit.Mcp

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open VcsToolkit.Core
open VcsToolkit.Forge

/// Server-internal helpers.
[<AutoOpen>]
module internal ServerHelpers =

    /// Parse the `forge_pr_merge` strategy argument (`merge`/`squash`/`rebase`).
    let parseStrategy (s: string) : Result<MergeStrategy, McpError> =
        match s.ToLowerInvariant() with
        | "merge" -> Ok MergeStrategy.Merge
        | "squash" -> Ok MergeStrategy.Squash
        | "rebase" -> Ok MergeStrategy.Rebase
        | other ->
            Error(McpError.InvalidParams(sprintf "unknown merge strategy %A (expected merge, squash, or rebase)" other))

    /// Parse the `forge_pr_list` `state` argument (`open`/`closed`/`merged`/`all`).
    let parsePrListState (s: string) : Result<PrListState, McpError> =
        match s.ToLowerInvariant() with
        | "open" -> Ok PrListState.Open
        | "closed" -> Ok PrListState.Closed
        | "merged" -> Ok PrListState.Merged
        | "all" -> Ok PrListState.All
        | other ->
            Error(McpError.InvalidParams(sprintf "unknown state %A (expected open, closed, merged, or all)" other))

    /// Parse the `forge_issue_list` `state` argument (`open`/`closed`/`all` — issues have no
    /// "merged" state, unlike `forge_pr_list`'s).
    let parseIssueListState (s: string) : Result<IssueListState, McpError> =
        match s.ToLowerInvariant() with
        | "open" -> Ok IssueListState.Open
        | "closed" -> Ok IssueListState.Closed
        | "all" -> Ok IssueListState.All
        | other -> Error(McpError.InvalidParams(sprintf "unknown state %A (expected open, closed, or all)" other))

    /// Validate an optional `forge_pr_list`/`forge_issue_list` `limit` argument: `None` keeps
    /// the caller's default; `Some n` must be a positive count (a zero/negative cap has no
    /// sane CLI meaning and would otherwise reach `gh`/`glab`/`tea` as a confusing raw value).
    let parseListLimit (limit: int option) : Result<int option, McpError> =
        match limit with
        | Some n when n <= 0 -> Error(McpError.InvalidParams(sprintf "limit must be positive, got %d" n))
        | _ -> Ok limit

    /// Build the `forge_pr_review` action from its `kind`/`body` arguments, enforcing
    /// `ReviewAction`'s body invariant up front (before the client is called): `request_changes`
    /// and `comment` require a non-empty body; `approve`'s body is optional. An unknown kind or a
    /// missing/empty required body is refused as `InvalidParams`.
    let parseReviewAction (kind: string) (body: string option) : Result<ReviewAction, McpError> =
        let nonEmptyBody () =
            match body with
            | Some b when b.Trim() <> "" -> Some b
            | _ -> Option.None

        match kind.ToLowerInvariant() with
        | "approve" ->
            match body with
            | Some b -> Ok(ReviewAction.Approve.WithBody b)
            | Option.None -> Ok ReviewAction.Approve
        | "request_changes" ->
            match nonEmptyBody () with
            | Some b -> Ok(ReviewAction.RequestChanges b)
            | Option.None ->
                Error(McpError.InvalidParams "forge_pr_review: a request_changes review requires a non-empty body")
        | "comment" ->
            match nonEmptyBody () with
            | Some b -> Ok(ReviewAction.Comment b)
            | Option.None -> Error(McpError.InvalidParams "forge_pr_review: a comment review requires a non-empty body")
        | other ->
            Error(
                McpError.InvalidParams(
                    sprintf "unknown review kind %A (expected approve, request_changes, or comment)" other
                )
            )

    /// Truncate `content` to at most `budgetBytes` UTF-8 bytes, snapped to a full
    /// character boundary, appending an explicit `[truncated: showing N of M bytes]`
    /// marker when truncation occurs. `None`, or `Some b` with `b <= 0`, disables
    /// truncation entirely — content passes through byte-for-byte unchanged.
    let applyOutputBudget (budgetBytes: int option) (content: string) : string =
        match budgetBytes with
        | None -> content
        | Some b when b <= 0 -> content
        | Some b ->
            let totalBytes = Encoding.UTF8.GetByteCount content

            if totalBytes <= b then
                content
            else
                // Decode only the first `b` bytes back to chars. With `flush = false` the
                // decoder silently holds back a trailing incomplete multi-byte sequence
                // instead of throwing, so this always snaps to a full UTF-8 character
                // boundary rather than splitting one mid-codepoint.
                let fullBytes = Encoding.UTF8.GetBytes content
                let decoder = Encoding.UTF8.GetDecoder()
                let charBuf = Array.zeroCreate<char> b
                let charCount = decoder.GetChars(fullBytes, 0, b, charBuf, 0, false)
                let kept = String(charBuf, 0, charCount)
                let keptBytes = Encoding.UTF8.GetByteCount kept
                kept + sprintf "\n[truncated: showing %d of %d bytes]" keptBytes totalBytes

/// An MCP server over a single repository (and, optionally, its forge). Call its tool
/// methods — each returns the tool's JSON result string, or an `McpError`. Read tools are
/// always available; mutating tools are gated by the `writes` policy (and repo mutations
/// serialize on a per-repo lock).
[<Sealed>]
type VcsMcpServer(repo: Repo, forge: Forge option, writes: WriteGate, outputBudget: int option) =

    // Serializes the repo-mutating tools: an MCP host can dispatch tool calls concurrently,
    // so without this two repo mutations (e.g. `repo_try_merge`'s materialize-then-rollback
    // racing a `repo_commit`) could interleave. Forge tools are predominantly remote calls to
    // a server that serializes on its side, so they aren't gated by this local lock.
    let writeLock = new SemaphoreSlim(1, 1)

    /// The repository this server serves.
    member _.Repo = repo

    /// The configured forge, if any.
    member _.ForgeOpt = forge

    /// The write gate.
    member _.Writes = writes

    /// The output-size budget (bytes) applied to large-content read tools; `None` means
    /// no limit.
    member _.OutputBudget = outputBudget

    // --- gating helpers ----------------------------------------------------

    /// Reject the mutating tool `tool` when the write gate doesn't cover it.
    member _.RequireWrite(tool: string) : Result<unit, McpError> =
        if writes.Allows tool then
            Ok()
        else
            Error(
                McpError.InvalidParams(
                    sprintf
                        "write tool %A is disabled; restart the server with --allow-write (all mutations) or --allow-tools naming it"
                        tool
                )
            )

    /// The configured forge, or a clear invalid-params error when none was resolved.
    member _.Forge() : Result<Forge, McpError> =
        match forge with
        | Some f -> Ok f
        | Option.None ->
            Error(
                McpError.InvalidParams "no forge is configured for this repository (pass --forge github|gitlab|gitea)"
            )

    /// Gate + serialize a repo-mutating tool: check the write gate, then hold the per-repo
    /// write lock for the action's duration.
    member private this.WithRepoWrite (tool: string) (action: unit -> Task<Result<string, McpError>>) =
        task {
            match this.RequireWrite tool with
            | Error e -> return Error e
            | Ok() ->
                do! writeLock.WaitAsync()

                try
                    return! action ()
                finally
                    writeLock.Release() |> ignore
        }

    /// Gate a forge-mutating tool (no repo lock), then run `action` against the forge.
    member private this.WithForgeWrite (tool: string) (action: Forge -> Task<Result<string, McpError>>) =
        task {
            match this.RequireWrite tool with
            | Error e -> return Error e
            | Ok() ->
                match this.Forge() with
                | Error e -> return Error e
                | Ok f -> return! action f
        }

    /// Gate a forge tool that mutates the LOCAL working copy (i.e. `forge_pr_checkout`, which
    /// switches the working tree to a PR/MR branch, `forge_pr_merge`, and `forge_pr_close`,
    /// which can delete the local branch and switch the checkout via `--delete-branch`): check
    /// the write gate, resolve the forge, then hold the per-repo write lock for the action's
    /// duration. Unlike the remote-only forge writes (`forge_pr_create`,
    /// `forge_pr_mark_ready`, `forge_pr_comment`, `forge_pr_edit`, `forge_issue_create`, ...),
    /// this touches the same working tree the `repo_*` mutations do, so it must serialize on
    /// that lock the way `repo_checkout` does — otherwise a concurrent
    /// `repo_commit`/`repo_checkout` could interleave with the branch switch.
    member private this.WithForgeRepoWrite (tool: string) (action: Forge -> Task<Result<string, McpError>>) =
        task {
            match this.RequireWrite tool with
            | Error e -> return Error e
            | Ok() ->
                match this.Forge() with
                | Error e -> return Error e
                | Ok f ->
                    do! writeLock.WaitAsync()

                    try
                        return! action f
                    finally
                        writeLock.Release() |> ignore
        }

    /// A repo read tool: call the facade and serialize its DTO (mapping the error).
    member private _.ReadRepo(action: unit -> Task<Result<'T, RepoError>>) =
        task {
            match! action () with
            | Error e -> return Error(coreErr e)
            | Ok v -> return Ok(Json.ok v)
        }

    /// A forge read tool: resolve the forge, call it, and serialize its DTO.
    member private this.ReadForge(action: Forge -> Task<Result<'T, ForgeError>>) =
        task {
            match this.Forge() with
            | Error e -> return Error e
            | Ok f ->
                match! action f with
                | Error e -> return Error(forgeErr e)
                | Ok v -> return Ok(Json.ok v)
        }

    // --- repo: read (always available) -------------------------------------

    /// A batched snapshot of the repo state.
    member this.RepoSnapshot() =
        this.ReadRepo(fun () -> repo.Snapshot())

    /// The backend, root, working directory, and configured forge.
    member _.RepoInfo() : Task<Result<string, McpError>> =
        task {
            let info =
                {| backend = repo.Kind.AsString
                   root = repo.Root
                   cwd = repo.Cwd
                   forge = forge |> Option.map (fun f -> f.Kind.AsString) |}

            return Ok(Json.ok info)
        }

    /// The working-copy changes.
    member this.RepoStatus() =
        this.ReadRepo(fun () -> repo.ChangedFiles())

    /// Aggregate insertion/deletion/file counts for the working copy.
    member this.RepoDiffStat() =
        this.ReadRepo(fun () -> repo.DiffStat())

    /// Local branch (git) / bookmark (jj) names.
    member this.RepoBranches() =
        this.ReadRepo(fun () -> repo.LocalBranches())

    /// The current branch/bookmark (null when detached/unset).
    member this.RepoCurrentBranch() =
        this.ReadRepo(fun () -> repo.CurrentBranch())

    /// Paths with unresolved merge conflicts.
    member this.RepoConflicts() =
        this.ReadRepo(fun () -> repo.ConflictedFiles())

    /// Attached worktrees (git) / workspaces (jj).
    member this.RepoWorktrees() =
        this.ReadRepo(fun () -> repo.ListWorktrees())

    /// The configured remotes (name + URL) — git `remote -v` (one entry per remote, its fetch
    /// URL) / jj `jj git remote list`.
    member this.RepoRemotes() = this.ReadRepo(fun () -> repo.Remotes())

    /// The content of `path` as it exists at `rev`, untrimmed up to the server's output
    /// budget (`--output-budget`; a byte count). Content within the budget is returned
    /// byte-for-byte unchanged; content beyond it is truncated with a trailing
    /// `[truncated: showing N of M bytes]` marker. `rev` is passed through as-is — git
    /// accepts a commit-ish, jj a revset; not cross-backend syntax-portable.
    member this.RepoShowFile(rev: string, path: string) =
        this.ReadRepo(fun () ->
            task {
                match! repo.ShowFile(rev, path) with
                | Error e -> return Error e
                | Ok content -> return Ok(applyOutputBudget outputBudget content)
            })

    /// Recent history: up to `max` commits reachable from `revspecOrRevset` (git revspec / jj
    /// revset), most-recent-first. `author`/`date` are null on jj (its typed log surfaces neither).
    member this.RepoLog(revspecOrRevset: string, max: uint64) =
        // The facade's log takes an int count; clamp the wire's non-negative integer to Int32 so an
        // absurdly large value can't overflow into a negative or otherwise wrong cap.
        let capped =
            if max > uint64 Int32.MaxValue then
                Int32.MaxValue
            else
                int max

        this.ReadRepo(fun () -> repo.Log(revspecOrRevset, capped))

    /// Per-line authorship of `path` at `rev` (git `blame --line-porcelain` / jj `file
    /// annotate`) — "who last touched this line, and when". Serialized as a JSON array and
    /// truncated to the server's output budget the same way `repo_show_file` truncates file
    /// content (a trailing `[truncated: showing N of M bytes]` marker when it is) — an
    /// annotated file's rendered text easily blows past a reasonable context budget. `rev` is
    /// passed through as-is (git commit-ish / jj revset, not cross-backend-portable); `None`
    /// annotates the working copy / `@`.
    member this.RepoAnnotate(path: string, rev: string option) : Task<Result<string, McpError>> =
        task {
            match! repo.Annotate(path, rev) with
            | Error e -> return Error(coreErr e)
            | Ok lines -> return Ok(applyOutputBudget outputBudget (Json.ok lines))
        }

    // --- repo: mutations (gated) -------------------------------------------

    /// Probe whether merging `source` into the current work would conflict (rolled back).
    /// Write-gated — it spawns a real trial merge that materializes working-tree content.
    member this.RepoTryMerge(source: string) =
        this.WithRepoWrite "repo_try_merge" (fun () ->
            task {
                match! repo.TryMerge source with
                | Error e -> return Error(coreErr e)
                | Ok probe -> return Ok(Json.ok probe)
            })

    /// Commit exactly the given paths with a message.
    member this.RepoCommit(paths: string list, message: string) =
        this.WithRepoWrite "repo_commit" (fun () ->
            task {
                match! repo.CommitPaths(paths, message) with
                | Error e -> return Error(coreErr e)
                | Ok() -> return Ok(Json.ok {| committedPaths = List.length paths |})
            })

    /// Switch the working copy to a branch/bookmark/revision.
    member this.RepoCheckout(reference: string) =
        this.WithRepoWrite "repo_checkout" (fun () ->
            task {
                match! repo.Checkout reference with
                | Error e -> return Error(coreErr e)
                | Ok() -> return Ok(Json.ok {| checkedOut = reference |})
            })

    /// Fetch from the default remote.
    member this.RepoFetch() =
        this.WithRepoWrite "repo_fetch" (fun () ->
            task {
                match! repo.Fetch() with
                | Error e -> return Error(coreErr e)
                | Ok() -> return Ok(Json.ok {| fetched = true |})
            })

    /// Push an existing branch/bookmark to origin.
    member this.RepoPush(branch: string) =
        this.WithRepoWrite "repo_push" (fun () ->
            task {
                match! repo.Push branch with
                | Error e -> return Error(coreErr e)
                | Ok() -> return Ok(Json.ok {| pushed = branch |})
            })

    /// Create a worktree/workspace at `path` on a new `branch` from `baseRef`.
    member this.RepoCreateWorktree(path: string, branch: string, baseRef: string) =
        this.WithRepoWrite "repo_create_worktree" (fun () ->
            task {
                match! repo.CreateWorktree(path, branch, baseRef) with
                | Error e -> return Error(coreErr e)
                | Ok outcome -> return Ok(Json.ok outcome)
            })

    /// Remove the worktree/workspace at `path` (force to remove one with local changes).
    member this.RepoRemoveWorktree(path: string, force: bool) =
        this.WithRepoWrite "repo_remove_worktree" (fun () ->
            task {
                match! repo.RemoveWorktree(path, force) with
                | Error e -> return Error(coreErr e)
                | Ok() -> return Ok(Json.ok {| removed = path |})
            })

    /// Rebase the current work onto `onto` (git `rebase` / jj `rebase -d`). Rewrites the
    /// branch's commits onto a new base, so it holds the per-repo write lock like the other
    /// history-touching mutations.
    member this.RepoRebase(onto: string) =
        this.WithRepoWrite "repo_rebase" (fun () ->
            task {
                match! repo.Rebase onto with
                | Error e -> return Error(coreErr e)
                | Ok() -> return Ok(Json.ok {| rebasedOnto = onto |})
            })

    /// Abort the in-progress operation, if any (git: `merge`/`rebase --abort`; jj: a no-op).
    /// Reports the fresh post-call operation state (`Clear` once nothing is in progress) so the
    /// caller sees the result of the abort rather than assuming it.
    member this.RepoAbortInProgress() =
        this.WithRepoWrite "repo_abort_in_progress" (fun () ->
            task {
                match! repo.AbortInProgress() with
                | Error e -> return Error(coreErr e)
                | Ok state -> return Ok(Json.ok {| operation = state |})
            })

    /// Continue the in-progress operation after conflict resolution (git: `commit --no-edit`
    /// for a merge / `rebase --continue`; jj: a no-op). Reports the fresh post-call operation
    /// state: `Conflict` when unresolved paths still block, `Clear` when finished.
    member this.RepoContinueInProgress() =
        this.WithRepoWrite "repo_continue_in_progress" (fun () ->
            task {
                match! repo.ContinueInProgress() with
                | Error e -> return Error(coreErr e)
                | Ok state -> return Ok(Json.ok {| operation = state |})
            })

    /// Delete a local branch (git) / bookmark (jj). `force` (git only) deletes even an unmerged
    /// branch, discarding its unique commits, so this is write-gated and flagged destructive.
    member this.RepoDeleteBranch(name: string, force: bool) =
        this.WithRepoWrite "repo_delete_branch" (fun () ->
            task {
                match! repo.DeleteBranch(name, force) with
                | Error e -> return Error(coreErr e)
                | Ok() -> return Ok(Json.ok {| deleted = name |})
            })

    /// Rename a local branch (git) / bookmark (jj). Non-destructive — it preserves the commits.
    member this.RepoRenameBranch(oldName: string, newName: string) =
        this.WithRepoWrite "repo_rename_branch" (fun () ->
            task {
                match! repo.RenameBranch(oldName, newName) with
                | Error e -> return Error(coreErr e)
                | Ok() ->
                    return
                        Ok(
                            Json.ok
                                {| renamedFrom = oldName
                                   renamedTo = newName |}
                        )
            })

    /// Start new work on top of `reference` **without modifying it** (git `checkout <reference>`;
    /// jj `new <reference>`) — the backend-agnostic "start fresh on top of main" that, unlike
    /// `repo_checkout`, does not rewrite `reference` in place on jj.
    member this.RepoNewChild(reference: string) =
        this.WithRepoWrite "repo_new_child" (fun () ->
            task {
                match! repo.NewChild reference with
                | Error e -> return Error(coreErr e)
                | Ok() -> return Ok(Json.ok {| newChild = reference |})
            })

    // --- forge: read (always available; error when no forge) ---------------

    /// Whether the forge CLI reports an authenticated session.
    member this.ForgeAuthStatus() = this.ReadForge(fun f -> f.AuthStatus())

    /// The repository/project on the configured forge (Unsupported on Gitea).
    member this.ForgeRepoView() = this.ReadForge(fun f -> f.RepoView())

    /// Open pull/merge requests on the configured forge — the previous, options-less
    /// behaviour (open, up to 100). Kept as a genuine zero-argument overload for CLR binary
    /// compatibility: an already-compiled caller of the pre-state/limit `ForgePrList()`
    /// would hit `MissingMethodException` against a build that replaced it outright with
    /// the two-argument `state`/`limit` overload below.
    member this.ForgePrList() : Task<Result<string, McpError>> =
        this.ForgePrList(Option.None, Option.None)

    /// Open pull/merge requests on the configured forge, optionally filtered by `state`
    /// (`open`/`closed`/`merged`/`all`; `None` defaults to `open`) and capped at `limit`
    /// (`None` defaults to 100) — mirrors `PrListOptions`'s defaults, so omitting both
    /// arguments reproduces this tool's previous, options-less behaviour exactly.
    member this.ForgePrList(state: string option, limit: int option) : Task<Result<string, McpError>> =
        task {
            let stateResult =
                match state with
                | Some s -> parsePrListState s
                | Option.None -> Ok PrListState.Open

            match stateResult, parseListLimit limit with
            | Error e, _ -> return Error e
            | _, Error e -> return Error e
            | Ok st, Ok lim ->
                let opts =
                    PrListOptions.Default.WithState st
                    |> fun o ->
                        match lim with
                        | Some l -> o.WithLimit l
                        | Option.None -> o

                return! this.ReadForge(fun f -> f.PrList opts)
        }

    /// A single pull/merge request by number.
    member this.ForgePrView(number: uint64) =
        this.ReadForge(fun f -> f.PrView number)

    /// PR/MRs whose source branch is `sourceBranch`, in any state, regardless of target
    /// branch — the "after pushing, find my PR" query. Returns a list, not a single value
    /// (a branch can have more than one PR/MR over its lifetime); an empty list means none
    /// currently match. Unsupported on Gitea (`tea pr list --output json` does not work
    /// against the real CLI — K-049).
    member this.ForgePrForBranch(sourceBranch: string) =
        this.ReadForge(fun f -> f.PrForBranch sourceBranch)

    /// The PR/MR's coarse CI status (Unsupported on Gitea).
    member this.ForgePrChecks(number: uint64) =
        this.ReadForge(fun f -> f.PrChecks number)

    /// The PR/MR's unified diff, per-file, serialized as JSON and truncated to the server's
    /// output budget the same way `repo_show_file`/`repo_annotate` truncate their content (a
    /// trailing `[truncated: showing N of M bytes]` marker when it is) — a PR diff easily blows
    /// past a reasonable context budget. Unsupported on Gitea (`tea` has no diff command).
    member this.ForgePrDiff(number: uint64) : Task<Result<string, McpError>> =
        task {
            match this.Forge() with
            | Error e -> return Error e
            | Ok f ->
                match! f.PrDiff number with
                | Error e -> return Error(forgeErr e)
                | Ok files -> return Ok(applyOutputBudget outputBudget (Json.ok files))
        }

    /// Open issues on the configured forge — the previous, options-less behaviour (open, up
    /// to 100). Kept as a genuine zero-argument overload for CLR binary compatibility (see
    /// `ForgePrList`'s doc comment for the rationale).
    member this.ForgeIssueList() : Task<Result<string, McpError>> =
        this.ForgeIssueList(Option.None, Option.None)

    /// Open issues on the configured forge, optionally filtered by `state`
    /// (`open`/`closed`/`all`; `None` defaults to `open`) and capped at `limit` (`None`
    /// defaults to 100) — mirrors `IssueListOptions`'s defaults, so omitting both arguments
    /// reproduces this tool's previous, options-less behaviour exactly.
    member this.ForgeIssueList(state: string option, limit: int option) : Task<Result<string, McpError>> =
        task {
            let stateResult =
                match state with
                | Some s -> parseIssueListState s
                | Option.None -> Ok IssueListState.Open

            match stateResult, parseListLimit limit with
            | Error e, _ -> return Error e
            | _, Error e -> return Error e
            | Ok st, Ok lim ->
                let opts =
                    IssueListOptions.Default.WithState st
                    |> fun o ->
                        match lim with
                        | Some l -> o.WithLimit l
                        | Option.None -> o

                return! this.ReadForge(fun f -> f.IssueList opts)
        }

    /// A single issue by number.
    member this.ForgeIssueView(number: uint64) =
        this.ReadForge(fun f -> f.IssueView number)

    /// Releases on the configured forge, newest first.
    member this.ForgeReleaseList() =
        this.ReadForge(fun f -> f.ReleaseList())

    /// A single release by tag (Unsupported on Gitea).
    member this.ForgeReleaseView(tag: string) =
        this.ReadForge(fun f -> f.ReleaseView tag)

    /// The forge's identity and flat capability map.
    member this.ForgeInfo() : Task<Result<string, McpError>> =
        task {
            match this.Forge() with
            | Error e -> return Error e
            | Ok f ->
                match! f.Capabilities() with
                | Error e -> return Error(forgeErr e)
                | Ok caps ->
                    return
                        Ok(
                            Json.ok
                                {| kind = f.Kind.AsString
                                   capabilities = caps |}
                        )
        }

    // --- forge: mutations (gated) ------------------------------------------

    /// Open an issue, returning the CLI's output (the URL on success).
    member this.ForgeIssueCreate(title: string, body: string) =
        this.WithForgeWrite "forge_issue_create" (fun f ->
            task {
                match guardArgvField "title" title with
                | Error e -> return Error e
                | Ok() ->
                    match guardArgvField "body" body with
                    | Error e -> return Error e
                    | Ok() ->
                        match! f.IssueCreate(title, body) with
                        | Error e -> return Error(forgeErr e)
                        | Ok out -> return Ok(Json.ok {| output = out |})
            })

    /// Close an issue (reopenable). A remote-only status change — no local working-copy
    /// mutation — so it uses `WithForgeWrite` (write gate only), not the per-repo lock the
    /// local-mutating forge writes hold (see `WithForgeRepoWrite`).
    member this.ForgeIssueClose(number: uint64) =
        this.WithForgeWrite "forge_issue_close" (fun f ->
            task {
                match! f.IssueClose number with
                | Error e -> return Error(forgeErr e)
                | Ok() -> return Ok(Json.ok {| closed = number |})
            })

    /// Reopen a closed issue. A remote-only status change — no local working-copy mutation —
    /// so it uses `WithForgeWrite` (write gate only), like `forge_issue_close`.
    member this.ForgeIssueReopen(number: uint64) =
        this.WithForgeWrite "forge_issue_reopen" (fun f ->
            task {
                match! f.IssueReopen number with
                | Error e -> return Error(forgeErr e)
                | Ok() -> return Ok(Json.ok {| reopened = number |})
            })

    /// Post a comment to an existing issue, returning the CLI's output. Remote-only, so it
    /// uses `WithForgeWrite` (like `forge_pr_comment`/`forge_issue_create`).
    member this.ForgeIssueComment(number: uint64, body: string) =
        this.WithForgeWrite "forge_issue_comment" (fun f ->
            task {
                match guardArgvField "body" body with
                | Error e -> return Error e
                | Ok() ->
                    match! f.IssueComment(number, body) with
                    | Error e -> return Error(forgeErr e)
                    | Ok out -> return Ok(Json.ok {| output = out |})
            })

    /// Delete a release by tag. This is a remote-only destructive mutation, so it uses
    /// `WithForgeWrite` without taking the per-repo local write lock.
    member this.ForgeReleaseDelete(tag: string) =
        this.WithForgeWrite "forge_release_delete" (fun f ->
            task {
                match guardArgvField "tag" tag with
                | Error e -> return Error e
                | Ok() ->
                    match! f.ReleaseDelete tag with
                    | Error e -> return Error(forgeErr e)
                    | Ok() -> return Ok(Json.ok {| deleted = tag |})
            })

    /// Open a pull/merge request, returning the CLI's output (the URL on success).
    member this.ForgePrCreate(title: string, body: string, source: string option, target: string option) =
        this.WithForgeWrite "forge_pr_create" (fun f ->
            task {
                match guardArgvField "title" title with
                | Error e -> return Error e
                | Ok() ->
                    match guardArgvField "body" body with
                    | Error e -> return Error e
                    | Ok() ->
                        let spec =
                            PrCreate.Create(title, body)
                            |> fun s ->
                                match source with
                                | Some x -> s.WithSource x
                                | Option.None -> s
                            |> fun s ->
                                match target with
                                | Some x -> s.WithTarget x
                                | Option.None -> s

                        match! f.PrCreate spec with
                        | Error e -> return Error(forgeErr e)
                        | Ok out -> return Ok(Json.ok {| output = out |})
            })

    /// Merge a pull/merge request with a strategy (`merge`/`squash`/`rebase`), optionally with
    /// auto-merge / delete-branch (GitHub only — refused as `Unsupported` on GitLab/Gitea). With
    /// `DeleteBranch = true` this can delete the local branch and switch the checkout, so it
    /// holds the per-repo write lock unconditionally (see `WithForgeRepoWrite`) rather than only
    /// when `deleteBranch` is set — simpler, and avoids a lock decision that races the branch.
    member this.ForgePrMerge(number: uint64, strategy: string, auto: bool, deleteBranch: bool) =
        this.WithForgeRepoWrite "forge_pr_merge" (fun f ->
            task {
                match parseStrategy strategy with
                | Error e -> return Error e
                | Ok ms ->
                    let spec: PrMerge =
                        { Strategy = ms
                          Auto = auto
                          DeleteBranch = deleteBranch }

                    match! f.PrMerge(number, spec) with
                    | Error e -> return Error(forgeErr e)
                    | Ok() -> return Ok(Json.ok {| merged = number |})
            })

    /// Close a pull/merge request without merging. With `DeleteBranch = true` this can delete
    /// the local branch and switch the checkout, so it holds the per-repo write lock
    /// unconditionally (see `WithForgeRepoWrite`) rather than only when `deleteBranch` is set —
    /// simpler, and avoids a lock decision that races the branch.
    member this.ForgePrClose(number: uint64, deleteBranch: bool) =
        this.WithForgeRepoWrite "forge_pr_close" (fun f ->
            task {
                match! f.PrClose(number, deleteBranch) with
                | Error e -> return Error(forgeErr e)
                | Ok() -> return Ok(Json.ok {| closed = number |})
            })

    /// Mark a draft pull/merge request as ready for review (Unsupported on Gitea).
    member this.ForgePrMarkReady(number: uint64) =
        this.WithForgeWrite "forge_pr_mark_ready" (fun f ->
            task {
                match! f.PrMarkReady number with
                | Error e -> return Error(forgeErr e)
                | Ok() -> return Ok(Json.ok {| ready = number |})
            })

    /// Post a comment to an existing pull/merge request, returning the CLI's output.
    member this.ForgePrComment(number: uint64, body: string) =
        this.WithForgeWrite "forge_pr_comment" (fun f ->
            task {
                match guardArgvField "body" body with
                | Error e -> return Error e
                | Ok() ->
                    match! f.PrComment(number, body) with
                    | Error e -> return Error(forgeErr e)
                    | Ok out -> return Ok(Json.ok {| output = out |})
            })

    /// Edit a pull/merge request's title and/or body (at least one required). **Unsupported on
    /// Gitea** (`tea` 0.9.2 has no `pr edit` command; K-063) — refused before any spawn there.
    member this.ForgePrEdit(number: uint64, title: string option, body: string option) =
        this.WithForgeWrite "forge_pr_edit" (fun f ->
            task {
                let titleGuard =
                    match title with
                    | Some t -> guardArgvField "title" t
                    | Option.None -> Ok()

                match titleGuard with
                | Error e -> return Error e
                | Ok() ->
                    let bodyGuard =
                        match body with
                        | Some b -> guardArgvField "body" b
                        | Option.None -> Ok()

                    match bodyGuard with
                    | Error e -> return Error e
                    | Ok() ->
                        let edit =
                            PrEdit.Create()
                            |> fun ed ->
                                match title with
                                | Some t -> ed.WithTitle t
                                | Option.None -> ed
                            |> fun ed ->
                                match body with
                                | Some b -> ed.WithBody b
                                | Option.None -> ed

                        match! f.PrEdit(number, edit) with
                        | Error e -> return Error(forgeErr e)
                        | Ok() -> return Ok(Json.ok {| edited = number |})
            })

    /// Check out a pull/merge request's branch into the local working copy. A local-worktree
    /// mutation (it switches the checked-out branch), so — unlike the remote-only forge
    /// writes — it holds the per-repo write lock to serialize with the `repo_*` mutations.
    member this.ForgePrCheckout(number: uint64) =
        this.WithForgeRepoWrite "forge_pr_checkout" (fun f ->
            task {
                match! f.PrCheckout number with
                | Error e -> return Error(forgeErr e)
                | Ok() -> return Ok(Json.ok {| checkedOut = number |})
            })

    /// Submit a review on a pull/merge request (approve / request_changes / comment). A
    /// remote-only mutation — it never touches the local working copy — so it uses
    /// `WithForgeWrite` (write gate only), NOT the per-repo lock the local-mutating forge writes
    /// hold (K-003), the same class as `forge_pr_comment`/`forge_pr_edit`. The body invariant
    /// (required for request_changes/comment) is enforced before the client is called; `kind`
    /// support varies by forge and is refused as `Unsupported` before any spawn.
    member this.ForgePrReview(number: uint64, kind: string, body: string option) =
        this.WithForgeWrite "forge_pr_review" (fun f ->
            task {
                match parseReviewAction kind body with
                | Error e -> return Error e
                | Ok action ->
                    // Belt-and-braces argv guard on the body (a leading `-` would read as a flag),
                    // matching forge_pr_comment/forge_pr_edit.
                    let bodyGuard =
                        match action.Body with
                        | Some b -> guardArgvField "body" b
                        | Option.None -> Ok()

                    match bodyGuard with
                    | Error e -> return Error e
                    | Ok() ->
                        match! f.PrReview(number, action) with
                        | Error e -> return Error(forgeErr e)
                        | Ok() -> return Ok(Json.ok {| reviewed = number |})
            })

    /// Create a release on the configured forge for a Git tag, returning the CLI's output (the
    /// release URL on GitHub/GitLab). A remote-only mutation — it never touches the local working
    /// copy — so it uses `WithForgeWrite` (write gate only), NOT the per-repo lock the
    /// local-mutating forge writes hold (K-003), the same class as `forge_pr_create`. `draft`/
    /// `prerelease` are refused as `Unsupported` on GitLab before any spawn.
    member this.ForgeReleaseCreate
        (tag: string, title: string option, notes: string option, draft: bool, prerelease: bool)
        =
        this.WithForgeWrite "forge_release_create" (fun f ->
            task {
                // Belt-and-braces argv guard on the free-text fields (a leading `-` would read as a
                // flag; `tag` also lands in a bare positional on gh/glab), matching
                // forge_issue_create/forge_pr_create.
                let guard =
                    [ "tag", tag ]
                    @ (match title with
                       | Some t -> [ "title", t ]
                       | Option.None -> [])
                    @ (match notes with
                       | Some n -> [ "notes", n ]
                       | Option.None -> [])
                    |> List.tryPick (fun (what, value) ->
                        match guardArgvField what value with
                        | Error e -> Some e
                        | Ok() -> Option.None)

                match guard with
                | Some e -> return Error e
                | Option.None ->
                    let spec =
                        ReleaseCreate.Create tag
                        |> fun s ->
                            match title with
                            | Some t -> s.WithTitle t
                            | Option.None -> s
                        |> fun s ->
                            match notes with
                            | Some n -> s.WithNotes n
                            | Option.None -> s
                        |> fun s -> if draft then s.WithDraft() else s
                        |> fun s -> if prerelease then s.WithPrerelease() else s

                    match! f.ReleaseCreate spec with
                    | Error e -> return Error(forgeErr e)
                    | Ok out -> return Ok(Json.ok {| output = out |})
            })

    interface IDisposable with
        member _.Dispose() = writeLock.Dispose()
