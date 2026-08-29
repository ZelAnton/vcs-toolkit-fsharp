namespace VcsToolkit.Mcp

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open VcsToolkit.Agent
open VcsToolkit.Core
open VcsToolkit.Forge

/// Server-internal helpers.
[<AutoOpen>]
module internal ServerHelpers =

    type internal MaterializedPath = { Relative: string; Full: string }

    let private invalidPath (message: string) = Error(McpError.InvalidParams message)

    let private isWindows = OperatingSystem.IsWindows()

    let private isReservedDeviceName (part: string) =
        let stem = part.TrimEnd(' ', '.').Split('.').[0].ToUpperInvariant()

        [ "CON"; "PRN"; "AUX"; "NUL" ] |> List.exists (fun name -> name = stem)
        || (stem.Length = 4
            && (stem.StartsWith("COM", StringComparison.Ordinal)
                || stem.StartsWith("LPT", StringComparison.Ordinal))
            && Char.IsDigit stem.[3]
            && stem.[3] <> '0')

    let private hasReparsePoint (path: string) =
        try
            if File.Exists path || Directory.Exists path then
                (File.GetAttributes path).HasFlag FileAttributes.ReparsePoint
            else
                false
        with ex ->
            raise (IOException(sprintf "could not inspect path %A: %s" path ex.Message))

    /// Resolve a caller path to an existing repo-relative materialized file without accepting
    /// traversal, rooted paths, Windows device names, or symlink/reparse-point components.
    let materializedPath (root: string) (path: string) : Result<MaterializedPath, McpError> =
        if String.IsNullOrWhiteSpace path then
            invalidPath "path must be a non-empty repository-relative file path"
        elif path.IndexOf('\u0000') >= 0 then
            invalidPath "path must not contain NUL"
        elif Path.IsPathRooted path || path.Contains(':') then
            invalidPath (sprintf "path %A must be repository-relative" path)
        else
            let components = path.Split([| '/'; '\\' |], StringSplitOptions.None)

            if components |> Array.exists (fun c -> c.Length = 0 || c = "." || c = "..") then
                invalidPath (sprintf "path %A must use normal repository-relative components" path)
            elif components |> Array.exists isReservedDeviceName then
                invalidPath (sprintf "path %A contains a reserved device name" path)
            else
                try
                    let rootFull = Path.GetFullPath root
                    let relative = String.Join(Path.DirectorySeparatorChar, components)
                    let full = Path.GetFullPath(Path.Combine(rootFull, relative))

                    let comparison =
                        if isWindows then
                            StringComparison.OrdinalIgnoreCase
                        else
                            StringComparison.Ordinal

                    let prefix =
                        rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + string Path.DirectorySeparatorChar

                    if not (full.StartsWith(prefix, comparison)) then
                        invalidPath (sprintf "path %A escapes the repository root" path)
                    elif hasReparsePoint rootFull then
                        invalidPath "repository root must not be a symlink or reparse point"
                    else
                        let mutable current = rootFull
                        let mutable unsafeComponent: string option = None

                        for part in components do
                            if unsafeComponent.IsNone then
                                current <- Path.Combine(current, part)

                                if hasReparsePoint current then
                                    unsafeComponent <- Some part

                        match unsafeComponent with
                        | Some part -> invalidPath (sprintf "path component %A is a symlink or reparse point" part)
                        | None ->
                            Ok
                                { Relative = String.Join('/', components)
                                  Full = full }
                with
                | :? IOException as ex -> Error(McpError.Internal ex.Message)
                | :? UnauthorizedAccessException as ex -> Error(McpError.Internal ex.Message)
                | :? ArgumentException as ex -> invalidPath ex.Message

    let readMaterialized (outputBudget: int option) (file: MaterializedPath) : Result<string, McpError> =
        try
            let bytes = File.ReadAllBytes file.Full

            match outputBudget with
            | Some budget when budget > 0 && bytes.LongLength > int64 budget ->
                Error(
                    McpError.InvalidParams(
                        sprintf
                            "materialized conflict file %A is %d bytes, above the %d-byte output budget"
                            file.Relative
                            bytes.LongLength
                            budget
                    )
                )
            | _ -> Ok((UTF8Encoding(false, true)).GetString bytes)
        with ex ->
            // Reading the materialized file is a local filesystem operation; expose its precise
            // failure instead of silently returning an incomplete conflict document.
            Error(
                McpError.Internal(sprintf "could not read materialized conflict file %A: %s" file.Relative ex.Message)
            )

    let writeMaterialized (file: MaterializedPath) (content: string) : Result<unit, McpError> =
        try
            File.WriteAllBytes(file.Full, (UTF8Encoding(false)).GetBytes content)
            Ok()
        with ex ->
            // The write is deliberately performed after all backend and resolution validation;
            // report an I/O failure rather than claiming that the conflict was resolved.
            Error(
                McpError.Internal(sprintf "could not write materialized conflict file %A: %s" file.Relative ex.Message)
            )

    let private normalizedRepoPath (path: string) = path.Replace('\\', '/')

    let isConflictedPath (relative: string) (conflicted: string list) =
        let expected = normalizedRepoPath relative
        conflicted |> List.exists (fun path -> normalizedRepoPath path = expected)

    let gitRegions content =
        match VcsToolkit.Git.Conflict.parseConflicts content with
        | Error e -> Error(McpError.Internal(sprintf "could not parse Git conflict markers: %s" e.Message))
        | Ok segments ->
            let regions =
                segments
                |> List.choose (function
                    | VcsToolkit.Git.ConflictSegment.Conflict region ->
                        Some(
                            {| oursLabel = region.OursLabel
                               baseLabel = region.BaseLabel
                               theirsLabel = region.TheirsLabel
                               ours = region.Ours
                               baseLines = region.Base
                               theirs = region.Theirs
                               markerLen = region.MarkerLen |}
                        )
                    | VcsToolkit.Git.ConflictSegment.Text _ -> None)

            Ok(segments, regions)

    let jjRegions content =
        match VcsToolkit.Jj.Conflict.parseConflicts content with
        | Error e -> Error(McpError.Internal(sprintf "could not parse Jujutsu conflict markers: %s" e.Message))
        | Ok segments ->
            let regions =
                segments
                |> List.choose (function
                    | VcsToolkit.Jj.JjConflictSegment.Conflict region ->
                        let sections =
                            region.Sections
                            |> List.map (function
                                | VcsToolkit.Jj.JjConflictSection.Diff(fromLabel, toLabel, lines) ->
                                    {| kind = "diff"
                                       fromLabel = Some fromLabel
                                       toLabel = Some toLabel
                                       label = Option.None
                                       lines = lines |}
                                | VcsToolkit.Jj.JjConflictSection.Snapshot(label, lines) ->
                                    {| kind = "snapshot"
                                       fromLabel = Option.None
                                       toLabel = Option.None
                                       label = Some label
                                       lines = lines |}
                                | VcsToolkit.Jj.JjConflictSection.Base(label, lines) ->
                                    {| kind = "base"
                                       fromLabel = Option.None
                                       toLabel = Option.None
                                       label = Some label
                                       lines = lines |})

                        Some(
                            {| number = region.Number
                               total = region.Total
                               sections = sections
                               sides = region.Sides()
                               baseLines = region.Base() |}
                        )
                    | VcsToolkit.Jj.JjConflictSegment.Text _ -> None)

            Ok(segments, regions)

    let gitResolution (side: string) (index: int option) : Result<VcsToolkit.Git.ResolutionSide, McpError> =
        match index with
        | Some _ -> invalidPath "index is only valid with side=side on Jujutsu"
        | None ->
            match side.ToLowerInvariant() with
            | "ours" -> Ok VcsToolkit.Git.ResolutionSide.Ours
            | "theirs" -> Ok VcsToolkit.Git.ResolutionSide.Theirs
            | "base" -> Ok VcsToolkit.Git.ResolutionSide.Base
            | "side" -> invalidPath "side=side is only supported by Jujutsu"
            | other -> invalidPath (sprintf "unknown conflict side %A (expected ours, theirs, or base)" other)

    let jjResolution
        (segments: VcsToolkit.Jj.JjConflictSegment list)
        (side: string)
        (index: int option)
        : Result<VcsToolkit.Jj.JjResolution, McpError> =
        let regions =
            segments
            |> List.choose (function
                | VcsToolkit.Jj.JjConflictSegment.Conflict region -> Some region
                | VcsToolkit.Jj.JjConflictSegment.Text _ -> None)

        let rejectIndex () =
            match index with
            | Some _ -> invalidPath "index is only valid with side=side on Jujutsu"
            | None -> Ok()

        match side.ToLowerInvariant() with
        | "ours" ->
            match rejectIndex () with
            | Error e -> Error e
            | Ok() -> Ok(VcsToolkit.Jj.JjResolution.Side 0)
        | "theirs" ->
            match rejectIndex () with
            | Error e -> Error e
            | Ok() when regions |> List.forall (fun region -> region.Sides().Length = 2) ->
                Ok(VcsToolkit.Jj.JjResolution.Side 1)
            | Ok() -> invalidPath "side=theirs requires every Jujutsu conflict to have exactly two sides"
        | "base" ->
            match rejectIndex () with
            | Error e -> Error e
            | Ok() -> Ok VcsToolkit.Jj.JjResolution.Base
        | "side" ->
            match index with
            | Some i when i >= 0 && regions |> List.forall (fun region -> i < region.Sides().Length) ->
                Ok(VcsToolkit.Jj.JjResolution.Side i)
            | Some i -> invalidPath (sprintf "conflict side index %d is not present in every region" i)
            | None -> invalidPath "side=side requires a non-negative integer index"
        | other -> invalidPath (sprintf "unknown conflict side %A (expected ours, theirs, base, or side)" other)

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

    /// Serialize a list unchanged when it fits the output budget. When it does not, keep the
    /// largest prefix of whole items whose structured truncation envelope fits. The minimum
    /// empty envelope is returned intact even when the budget is too small to contain it, since
    /// valid JSON and explicit truncation metadata take precedence over an impossible byte cap.
    let applyJsonArrayOutputBudget (budgetBytes: int option) (items: 'T list) : string =
        let full = Json.ok items

        match budgetBytes with
        | None -> full
        | Some b when b <= 0 -> full
        | Some b when Encoding.UTF8.GetByteCount full <= b -> full
        | Some b ->
            let total = List.length items

            let envelope shown =
                Json.ok
                    {| Items = List.truncate shown items
                       Truncated = true
                       Shown = shown
                       Total = total |}

            let rec largestFitting low high best =
                if low > high then
                    best
                else
                    let middle = low + (high - low) / 2
                    let candidate = envelope middle

                    if Encoding.UTF8.GetByteCount candidate <= b then
                        largestFitting (middle + 1) high candidate
                    else
                        largestFitting low (middle - 1) best

            let minimum = envelope 0

            if Encoding.UTF8.GetByteCount minimum > b then
                minimum
            else
                largestFitting 1 (total - 1) minimum

/// An MCP server over a single repository (and, optionally, its forge). Call its tool
/// methods — each returns the tool's JSON result string, or an `McpError`. Read tools are
/// always available; mutating tools are gated by the `writes` policy (and repo mutations
/// serialize on a per-repo lock).
[<Sealed>]
type VcsMcpServer
    private
    (
        repo: Repo,
        forge: Forge option,
        writes: WriteGate,
        outputBudget: int option,
        writeLock: SemaphoreSlim,
        ownsWriteLock: bool,
        requestCancellation: CancellationToken
    ) =

    // Keep the ordinary facade unbounded: its typed parsers cannot generally consume a partial
    // result. Only the read tools with an explicit response envelope use these bounded views.
    let boundedRepo =
        match repo.Git, repo.Jj with
        | Some git, Option.None -> Repo.FromGit(repo.Root, repo.Cwd, git.WithOutputBudget outputBudget)
        | Option.None, Some jj -> Repo.FromJj(repo.Root, repo.Cwd, jj.WithOutputBudget outputBudget)
        | _ -> invalidOp "repository backend handle is inconsistent"

    let boundedForge =
        forge
        |> Option.map (fun f ->
            match f.GitHubClient, f.GitLabClient, f.GiteaClient with
            | Some client, Option.None, Option.None -> Forge.FromGitHub(f.Cwd, client.WithOutputBudget outputBudget)
            | Option.None, Some client, Option.None -> Forge.FromGitLab(f.Cwd, client.WithOutputBudget outputBudget)
            | Option.None, Option.None, Some client -> Forge.FromGitea(f.Cwd, client.WithOutputBudget outputBudget)
            | Option.None, Option.None, Option.None -> Forge.FromUnknown f.Cwd
            | _ -> invalidOp "forge backend handles are inconsistent")

    let agentOutputLimit = outputBudget |> Option.defaultValue Int32.MaxValue

    let agentForge =
        forge
        |> Option.bind (fun configured ->
            match configured.Kind with
            | ForgeKind.GitHub -> Some(AgentForgeKind.GitHub, configured)
            | ForgeKind.GitLab -> Some(AgentForgeKind.GitLab, configured)
            | ForgeKind.Gitea -> Some(AgentForgeKind.Gitea, configured)
            | ForgeKind.Unknown -> None)

    let agentForgeSupportsPublicationAndCi =
        match agentForge with
        | Some(AgentForgeKind.GitHub, _)
        | Some(AgentForgeKind.GitLab, _) -> true
        | Some(AgentForgeKind.Gitea, _)
        | None -> false

    let agentResult envelope : Result<string, McpError> = Ok(AgentWire.serialize envelope)

    // Serializes repo-mutating tools, including request-scoped views, so concurrent calls cannot
    // interleave operations on the same working copy.
    new(repo: Repo, forge: Forge option, writes: WriteGate, outputBudget: int option) =
        new VcsMcpServer(repo, forge, writes, outputBudget, new SemaphoreSlim(1, 1), true, CancellationToken.None)

    /// The repository this server serves.
    member _.Repo = repo

    /// The configured forge, if any.
    member _.ForgeOpt = forge

    /// The write gate.
    member _.Writes = writes

    /// The output-size budget (bytes) applied to large-content read tools; `None` means
    /// no limit.
    member _.OutputBudget = outputBudget

    /// Intent-oriented Agent tools that are meaningful under this server's configured forge and
    /// write policy. Low-level repo/forge tools remain independently discoverable.
    member _.AvailableAgentTools =
        [ "agent_inspect"
          "agent_changes"

          if writes.Allows "agent_commit" then
              "agent_commit"

          if agentForgeSupportsPublicationAndCi then
              if writes.Allows "agent_publish" then
                  "agent_publish"

              "agent_ci_status"
              "agent_ci_wait" ]

    /// Build request-scoped client handles while retaining this server's shared repo lock. The
    /// command clients already compose their configured timeout and cancellation independently,
    /// so binding the request token here preserves the server timeout as a separate deadline.
    member internal this.WithCancellation(token: CancellationToken) : VcsMcpServer =
        if not token.CanBeCanceled then
            this
        else
            let requestRepo =
                match repo.Git, repo.Jj with
                | Some git, Option.None -> Repo.FromGit(repo.Root, repo.Cwd, git.DefaultCancelOn token)
                | Option.None, Some jj -> Repo.FromJj(repo.Root, repo.Cwd, jj.DefaultCancelOn token)
                | _ -> invalidOp "repository backend handle is inconsistent"

            let requestForge =
                forge
                |> Option.map (fun f ->
                    match f.GitHubClient, f.GitLabClient, f.GiteaClient with
                    | Some client, Option.None, Option.None -> Forge.FromGitHub(f.Cwd, client.DefaultCancelOn token)
                    | Option.None, Some client, Option.None -> Forge.FromGitLab(f.Cwd, client.DefaultCancelOn token)
                    | Option.None, Option.None, Some client -> Forge.FromGitea(f.Cwd, client.DefaultCancelOn token)
                    | Option.None, Option.None, Option.None -> Forge.FromUnknown f.Cwd
                    | _ -> invalidOp "forge backend handles are inconsistent")

            new VcsMcpServer(requestRepo, requestForge, writes, outputBudget, writeLock, false, token)

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
                do! writeLock.WaitAsync(requestCancellation)

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
                    do! writeLock.WaitAsync(requestCancellation)

                    try
                        return! action f
                    finally
                        writeLock.Release() |> ignore
        }

    /// Gate and serialize an Agent mutation without translating its typed outcome into an MCP
    /// protocol error. The same per-repository lock covers low-level and intent writes.
    member private _.WithAgentRepoWrite
        (tool: string)
        (operation: AgentOperation)
        (action: unit -> Task<Result<string, McpError>>)
        =
        task {
            if not (writes.Allows tool) then
                return
                    Agent.denied
                        operation
                        $"write tool '{tool}' is disabled; restart the server with --allow-write or --allow-tools naming it"
                    |> agentResult
            elif requestCancellation.IsCancellationRequested then
                return Agent.cancelled operation |> agentResult
            else
                try
                    do! writeLock.WaitAsync requestCancellation

                    try
                        return! action ()
                    finally
                        writeLock.Release() |> ignore
                with :? OperationCanceledException ->
                    return Agent.cancelled operation |> agentResult
        }

    // --- intent outcomes --------------------------------------------------

    /// One transport-neutral inspection outcome for the configured repository.
    member _.AgentInspect() : Task<Result<string, McpError>> =
        task {
            let! envelope = Agent.inspectWith repo forge requestCancellation agentOutputLimit
            return agentResult envelope
        }

    /// One transport-neutral changes outcome for the configured repository.
    member _.AgentChanges(mode: ChangesMode) : Task<Result<string, McpError>> =
        task {
            let! envelope = Agent.changesWith repo mode requestCancellation agentOutputLimit
            return agentResult envelope
        }

    /// One write-gated, repository-locked commit outcome.
    member this.AgentCommit(paths: string list, message: string) : Task<Result<string, McpError>> =
        this.WithAgentRepoWrite "agent_commit" AgentOperation.Commit (fun () ->
            task {
                let! envelope = Agent.commitWith repo paths message requestCancellation agentOutputLimit
                return agentResult envelope
            })

    /// One write-gated, repository-locked publication outcome. Gitea is intentionally omitted
    /// until Agent can prove repository identity and exact-revision CI through that forge.
    member this.AgentPublish
        (
            branch: string,
            remote: string,
            revision: string,
            account: string,
            targetBranch: string,
            title: string,
            body: string
        ) : Task<Result<string, McpError>> =
        match agentForge with
        | None -> Task.FromResult(Agent.unsupported AgentOperation.Publish |> agentResult)
        | Some(kind, configuredForge) ->
            this.WithAgentRepoWrite "agent_publish" AgentOperation.Publish (fun () ->
                task {
                    let request =
                        PublishRequest.Create(
                            repo.Root,
                            branch,
                            remote,
                            revision,
                            kind,
                            account,
                            targetBranch,
                            title,
                            body
                        )
                        |> fun value -> value.WithOutputLimit agentOutputLimit

                    let! envelope = Agent.publishWith repo configuredForge request requestCancellation
                    return agentResult envelope
                })

    /// One exact-revision CI observation outcome.
    member _.AgentCiStatus(branch: string, remote: string, revision: string, account: string) =
        task {
            match agentForge with
            | None -> return Agent.unsupported AgentOperation.CiStatus |> agentResult
            | Some(kind, configuredForge) ->
                let request =
                    CiStatusRequest.Create(repo.Root, kind, account, branch, remote, revision)
                    |> fun value -> value.WithOutputLimit agentOutputLimit

                let! envelope = Agent.ciStatusWith repo configuredForge request requestCancellation
                return agentResult envelope
        }

    /// One exact-revision CI wait outcome.
    member _.AgentCiWait
        (
            branch: string,
            remote: string,
            revision: string,
            account: string,
            pollInterval: TimeSpan,
            deadline: TimeSpan,
            inactivityDeadline: TimeSpan
        ) =
        task {
            match agentForge with
            | None -> return Agent.unsupported AgentOperation.CiWait |> agentResult
            | Some(kind, configuredForge) ->
                let request =
                    CiWaitRequest.Create(repo.Root, kind, account, branch, remote, revision)
                    |> fun value -> value.WithPolling pollInterval
                    |> fun value -> value.WithDeadline deadline
                    |> fun value -> value.WithInactivityDeadline inactivityDeadline
                    |> fun value -> value.WithOutputLimit agentOutputLimit

                let! envelope = Agent.ciWaitWith repo configuredForge request requestCancellation
                return agentResult envelope
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

    /// The working copy's unified diff, per file. Normally serialized as the original JSON
    /// array. When the server's output budget truncates it, whole trailing entries are dropped
    /// and a valid JSON envelope reports `items`, `truncated`, `shown`, and `total`.
    member _.RepoDiff() : Task<Result<string, McpError>> =
        task {
            match! boundedRepo.Diff() with
            | Error e -> return Error(coreErr e)
            | Ok files -> return Ok(applyJsonArrayOutputBudget outputBudget files)
        }

    /// Local branch (git) / bookmark (jj) names.
    member this.RepoBranches() =
        this.ReadRepo(fun () -> repo.LocalBranches())

    /// The current branch/bookmark (null when detached/unset).
    member this.RepoCurrentBranch() =
        this.ReadRepo(fun () -> repo.CurrentBranch())

    /// Git tag names, sorted by git's default ordering. Unsupported on jj, where the Core
    /// facade refuses the operation before spawning a command.
    member this.RepoTags() = this.ReadRepo(fun () -> repo.Tags())

    /// Paths with unresolved merge conflicts.
    member this.RepoConflicts() =
        this.ReadRepo(fun () -> repo.ConflictedFiles())

    /// Parse structured conflict regions from the materialized working-copy file. The file is
    /// read directly because Git's markers are not present in `show` output; the same path also
    /// gives Jujutsu's native materialization and keeps the response bounded before parsing.
    member _.RepoConflictRegions(path: string) : Task<Result<string, McpError>> =
        task {
            match materializedPath repo.Root path with
            | Error e -> return Error e
            | Ok file ->
                match readMaterialized outputBudget file with
                | Error e -> return Error e
                | Ok content ->
                    match repo.Kind with
                    | BackendKind.Git ->
                        match gitRegions content with
                        | Error e -> return Error e
                        | Ok(_, regions) ->
                            return
                                Ok(
                                    Json.ok
                                        {| path = file.Relative
                                           backend = "git"
                                           regions = regions |}
                                )
                    | BackendKind.Jj ->
                        match jjRegions content with
                        | Error e -> return Error e
                        | Ok(_, regions) ->
                            return
                                Ok(
                                    Json.ok
                                        {| path = file.Relative
                                           backend = "jj"
                                           regions = regions |}
                                )
        }

    /// Resolve every region in a materialized conflict file and, for Git, stage the resolved
    /// path. The conflict list is checked first so a clean or unrelated path cannot be written.
    member this.RepoResolveConflict(path: string, side: string, index: int option) : Task<Result<string, McpError>> =
        this.WithRepoWrite "repo_resolve_conflict" (fun () ->
            task {
                match materializedPath repo.Root path with
                | Error e -> return Error e
                | Ok file ->
                    match! repo.ConflictedFiles() with
                    | Error e -> return Error(coreErr e)
                    | Ok conflicted when not (isConflictedPath file.Relative conflicted) ->
                        return
                            Error(McpError.InvalidParams(sprintf "path %A is not currently conflicted" file.Relative))
                    | Ok _ ->
                        match readMaterialized outputBudget file with
                        | Error e -> return Error e
                        | Ok content ->
                            match repo.Kind with
                            | BackendKind.Git ->
                                match gitRegions content, gitResolution side index with
                                | Ok(segments, _), Ok resolution ->
                                    match VcsToolkit.Git.Conflict.resolve segments resolution with
                                    | Error e -> return Error(McpError.InvalidParams e.Message)
                                    | Ok resolved ->
                                        match writeMaterialized file resolved with
                                        | Error e -> return Error e
                                        | Ok() ->
                                            match repo.Git with
                                            | None ->
                                                return Error(McpError.Internal "Git backend handle is unavailable")
                                            | Some git ->
                                                match! git.Run(repo.Root, [ "add"; "--"; file.Relative ]) with
                                                | Error e -> return Error(McpError.Internal e.Message)
                                                | Ok _ ->
                                                    return
                                                        Ok(
                                                            Json.ok
                                                                {| path = file.Relative
                                                                   backend = "git"
                                                                   side = side |}
                                                        )
                                | Error e, _
                                | _, Error e -> return Error e
                            | BackendKind.Jj ->
                                match jjRegions content with
                                | Error e -> return Error e
                                | Ok(segments, _) ->
                                    match jjResolution segments side index with
                                    | Error e -> return Error e
                                    | Ok resolution ->
                                        match VcsToolkit.Jj.Conflict.resolve segments resolution with
                                        | Error e -> return Error(McpError.InvalidParams e.Message)
                                        | Ok resolved ->
                                            match writeMaterialized file resolved with
                                            | Error e -> return Error e
                                            | Ok() ->
                                                return
                                                    Ok(
                                                        Json.ok
                                                            {| path = file.Relative
                                                               backend = "jj"
                                                               side = side |}
                                                    )
            })

    /// Attached worktrees (git) / workspaces (jj).
    member this.RepoWorktrees() =
        this.ReadRepo(fun () -> repo.ListWorktrees())

    /// The configured remotes (name + URL) — git `remote -v` (one entry per remote, its fetch
    /// URL) / jj `jj git remote list`.
    member this.RepoRemotes() = this.ReadRepo(fun () -> repo.Remotes())

    /// The full commit id of a best common ancestor of `a` and `b`, or null when the histories
    /// are disconnected. Inputs are backend-specific revision expressions: git commit-ish values
    /// or jj revsets. Git uses `git merge-base`; jj excludes its all-zero virtual root.
    member this.RepoMergeBase(a: string, b: string) =
        this.ReadRepo(fun () -> repo.MergeBase(a, b))

    /// The content of `path` as it exists at `rev`, untrimmed up to the server's output
    /// budget (`--output-budget`; a byte count). Content within the budget is returned
    /// byte-for-byte unchanged; content beyond it is truncated with a trailing
    /// `[truncated: showing N of M bytes]` marker. `rev` is passed through as-is — git
    /// accepts a commit-ish, jj a revset; not cross-backend syntax-portable.
    member this.RepoShowFile(rev: string, path: string) =
        this.ReadRepo(fun () ->
            task {
                match! boundedRepo.ShowFile(rev, path) with
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

    /// Recent Jujutsu operation history, newest first. Git returns a structural Unsupported
    /// error from the Core facade without spawning a process.
    member this.RepoOpLog(limit: uint64) =
        let capped =
            if limit > uint64 Int32.MaxValue then
                Int32.MaxValue
            else
                int limit

        this.ReadRepo(fun () -> repo.OpLog capped)

    /// Per-line authorship of `path` at `rev` (git `blame --line-porcelain` / jj `file
    /// annotate`) — "who last touched this line, and when". Normally serialized as the original
    /// JSON array. When the server's output budget truncates it, whole trailing entries are
    /// dropped and a valid JSON envelope reports `items`, `truncated`, `shown`, and `total`.
    /// `rev` is passed through as-is (git commit-ish / jj revset, not cross-backend-portable);
    /// `None` annotates the working copy / `@`.
    member this.RepoAnnotate(path: string, rev: string option) : Task<Result<string, McpError>> =
        task {
            match! boundedRepo.Annotate(path, rev) with
            | Error e -> return Error(coreErr e)
            | Ok lines -> return Ok(applyJsonArrayOutputBudget outputBudget lines)
        }

    /// Repo-relative tracked paths at `rev` ('/'-separated) — git `ls-files`/`ls-tree -r
    /// --name-only` / jj `file list`. Normally serialized as the original JSON array. When the
    /// server's output budget truncates it, whole trailing entries are dropped and a valid
    /// JSON envelope reports `items`, `truncated`, `shown`, and `total`. `rev` is passed
    /// through as-is (git commit-ish / jj revset, not cross-backend-portable); `None` lists the
    /// working copy / `@`.
    member this.RepoListFiles(rev: string option) : Task<Result<string, McpError>> =
        task {
            match! boundedRepo.ListFiles rev with
            | Error e -> return Error(coreErr e)
            | Ok files -> return Ok(applyJsonArrayOutputBudget outputBudget files)
        }

    // --- repo: mutations (gated) -------------------------------------------

    /// Undo the latest Jujutsu operation. This rewrites repository state and is write-gated;
    /// Git returns a structural Unsupported error from the Core facade.
    member this.RepoUndo() =
        this.WithRepoWrite "repo_undo" (fun () ->
            task {
                match! repo.OpUndo() with
                | Error e -> return Error(coreErr e)
                | Ok() -> return Ok(Json.ok {| undone = true |})
            })

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

    /// Create a git tag named `name` at `rev` (`None` means `HEAD`). `Some message` creates an
    /// annotated tag; `None` creates a lightweight tag. Unsupported on jj before any spawn.
    member this.RepoTagCreate(name: string, message: string option, rev: string option) =
        this.WithRepoWrite "repo_tag_create" (fun () ->
            task {
                match! repo.TagCreate(name, message, rev) with
                | Error e -> return Error(coreErr e)
                | Ok() ->
                    return
                        Ok(
                            Json.ok
                                {| created = name
                                   annotated = message.IsSome
                                   revision = rev |}
                        )
            })

    /// Delete the git tag named `name`. Unsupported on jj before any spawn. This is destructive
    /// and non-idempotent because a deleted tag reference cannot be recovered by a second call.
    member this.RepoTagDelete(name: string) =
        this.WithRepoWrite "repo_tag_delete" (fun () ->
            task {
                match! repo.TagDelete name with
                | Error e -> return Error(coreErr e)
                | Ok() -> return Ok(Json.ok {| deleted = name |})
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
    /// currently match. On Gitea `tea pr list` has no head-branch filter, so the facade lists
    /// all states and matches the source branch itself, over the fetched window.
    member this.ForgePrForBranch(sourceBranch: string) =
        this.ReadForge(fun f -> f.PrForBranch sourceBranch)

    /// The PR/MR's coarse CI status (Unsupported on Gitea).
    member this.ForgePrChecks(number: uint64) =
        this.ReadForge(fun f -> f.PrChecks number)

    /// The PR/MR's unified diff, per file. Normally serialized as the original JSON array. When
    /// the server's output budget truncates it, whole trailing entries are dropped and a valid
    /// JSON envelope reports `items`, `truncated`, `shown`, and `total`. Unsupported on Gitea
    /// (`tea` has no diff command).
    member this.ForgePrDiff(number: uint64) : Task<Result<string, McpError>> =
        task {
            match boundedForge with
            | None ->
                return
                    Error(
                        McpError.InvalidParams
                            "no forge is configured for this repository (pass --forge github|gitlab|gitea)"
                    )
            | Some f ->
                match! f.PrDiff number with
                | Error e -> return Error(forgeErr e)
                | Ok files -> return Ok(applyJsonArrayOutputBudget outputBudget files)
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
    member this.ForgeIssueCreate(title: string, body: string) = this.ForgeIssueCreate(title, body, [])

    /// Open an issue with optional labels, returning the CLI's output.
    member this.ForgeIssueCreate(title: string, body: string, labels: string list) =
        this.WithForgeWrite "forge_issue_create" (fun f ->
            task {
                match guardArgvField "title" title with
                | Error e -> return Error e
                | Ok() ->
                    match guardArgvField "body" body with
                    | Error e -> return Error e
                    | Ok() ->
                        let spec = IssueCreate.Create(title, body).WithLabels labels

                        match! f.IssueCreate spec with
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

    /// Edit an issue's title and/or body (at least one required; empty strings clear fields).
    /// GitLab refuses a body equal to `-` before spawning. **Unsupported on Gitea** (`tea`
    /// 0.9.2 has no issue edit command), where a populated edit is refused before any spawn.
    member this.ForgeIssueEdit(number: uint64, title: string option, body: string option) =
        this.WithForgeWrite "forge_issue_edit" (fun f ->
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
                            IssueEdit.Create()
                            |> fun ed ->
                                match title with
                                | Some t -> ed.WithTitle t
                                | Option.None -> ed
                            |> fun ed ->
                                match body with
                                | Some b -> ed.WithBody b
                                | Option.None -> ed

                        match! f.IssueEdit(number, edit) with
                        | Error e -> return Error(forgeErr e)
                        | Ok() -> return Ok(Json.ok {| edited = number |})
            })

    /// Add labels to an existing issue. Gitea reports Unsupported; all mutations are write-gated.
    member this.ForgeIssueAddLabels(number: uint64, labels: string list) =
        this.WithForgeWrite "forge_issue_add_labels" (fun f ->
            task {
                match! f.IssueAddLabels(number, labels) with
                | Error e -> return Error(forgeErr e)
                | Ok() ->
                    return
                        Ok(
                            Json.ok
                                {| number = number
                                   labels_added = labels |}
                        )
            })

    /// Remove labels from an existing issue. Gitea reports Unsupported; all mutations are write-gated.
    member this.ForgeIssueRemoveLabels(number: uint64, labels: string list) =
        this.WithForgeWrite "forge_issue_remove_labels" (fun f ->
            task {
                match! f.IssueRemoveLabels(number, labels) with
                | Error e -> return Error(forgeErr e)
                | Ok() ->
                    return
                        Ok(
                            Json.ok
                                {| number = number
                                   labels_removed = labels |}
                        )
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
        this.ForgePrCreate(title, body, source, target, [])

    /// Open a pull/merge request with optional labels, returning the CLI's output.
    member this.ForgePrCreate
        (title: string, body: string, source: string option, target: string option, labels: string list)
        =
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
                            |> fun s -> s.WithLabels labels

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

    /// Add labels to an existing pull/merge request. Gitea reports Unsupported.
    member this.ForgePrAddLabels(number: uint64, labels: string list) =
        this.WithForgeWrite "forge_pr_add_labels" (fun f ->
            task {
                match! f.PrAddLabels(number, labels) with
                | Error e -> return Error(forgeErr e)
                | Ok() ->
                    return
                        Ok(
                            Json.ok
                                {| number = number
                                   labels_added = labels |}
                        )
            })

    /// Remove labels from an existing pull/merge request. Gitea reports Unsupported.
    member this.ForgePrRemoveLabels(number: uint64, labels: string list) =
        this.WithForgeWrite "forge_pr_remove_labels" (fun f ->
            task {
                match! f.PrRemoveLabels(number, labels) with
                | Error e -> return Error(forgeErr e)
                | Ok() ->
                    return
                        Ok(
                            Json.ok
                                {| number = number
                                   labels_removed = labels |}
                        )
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
        member _.Dispose() =
            if ownsWriteLock then
                writeLock.Dispose()
