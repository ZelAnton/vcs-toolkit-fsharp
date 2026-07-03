namespace VcsToolkit.Mcp

open System
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

/// An MCP server over a single repository (and, optionally, its forge). Call its tool
/// methods — each returns the tool's JSON result string, or an `McpError`. Read tools are
/// always available; mutating tools are gated by the `writes` policy (and repo mutations
/// serialize on a per-repo lock).
[<Sealed>]
type VcsMcpServer(repo: Repo, forge: Forge option, writes: WriteGate) =

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

    // --- forge: read (always available; error when no forge) ---------------

    /// Whether the forge CLI reports an authenticated session.
    member this.ForgeAuthStatus() = this.ReadForge(fun f -> f.AuthStatus())

    /// The repository/project on the configured forge (Unsupported on Gitea).
    member this.ForgeRepoView() = this.ReadForge(fun f -> f.RepoView())

    /// Open pull/merge requests on the configured forge.
    member this.ForgePrList() = this.ReadForge(fun f -> f.PrList())

    /// A single pull/merge request by number.
    member this.ForgePrView(number: uint64) =
        this.ReadForge(fun f -> f.PrView number)

    /// The PR/MR's coarse CI status (Unsupported on Gitea).
    member this.ForgePrChecks(number: uint64) =
        this.ReadForge(fun f -> f.PrChecks number)

    /// Open issues on the configured forge.
    member this.ForgeIssueList() = this.ReadForge(fun f -> f.IssueList())

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
                match! f.IssueCreate(title, body) with
                | Error e -> return Error(forgeErr e)
                | Ok out -> return Ok(Json.ok {| output = out |})
            })

    /// Open a pull/merge request, returning the CLI's output (the URL on success).
    member this.ForgePrCreate(title: string, body: string, source: string option, target: string option) =
        this.WithForgeWrite "forge_pr_create" (fun f ->
            task {
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

    /// Merge a pull/merge request with a strategy (`merge`/`squash`/`rebase`).
    member this.ForgePrMerge(number: uint64, strategy: string) =
        this.WithForgeWrite "forge_pr_merge" (fun f ->
            task {
                match parseStrategy strategy with
                | Error e -> return Error e
                | Ok ms ->
                    match! f.PrMerge(number, ms) with
                    | Error e -> return Error(forgeErr e)
                    | Ok() -> return Ok(Json.ok {| merged = number |})
            })

    /// Close a pull/merge request without merging.
    member this.ForgePrClose(number: uint64, deleteBranch: bool) =
        this.WithForgeWrite "forge_pr_close" (fun f ->
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

    /// Edit a pull/merge request's title and/or body (at least one required).
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

    interface IDisposable with
        member _.Dispose() = writeLock.Dispose()
