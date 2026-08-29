namespace VcsToolkit.Agent

open System
open System.Threading
open System.Threading.Tasks
open ProcessKit
open VcsToolkit.CliSupport
open VcsToolkit.Core
open VcsToolkit.Diff
open VcsToolkit.Forge

/// Constructors and stable mappings for the transport-neutral agent contract.
[<RequireQualifiedAccess>]
module Agent =
    [<Literal>]
    let ContractVersion = "1"

    [<Literal>]
    let DefaultOutputLimitBytes = 65_536

    [<Literal>]
    let MinimumOutputLimitBytes = 512

    /// Largest wait duration accepted by the reusable API and CLI. This stays one
    /// millisecond below the `CancellationTokenSource.CancelAfter` unsigned timer limit.
    let MaxWaitDuration = TimeSpan.FromMilliseconds 4_294_967_294.0

    let internal operationName operation = ContractNames.operation operation

    let internal errorCodeName code = ContractNames.errorCode code

    let internal fallbackReasonName reason = ContractNames.fallbackReason reason

    let internal exitCode code =
        match code with
        | AgentErrorCode.Unsupported -> 20
        | AgentErrorCode.Denied -> 21
        | AgentErrorCode.InvalidInput -> 22
        | AgentErrorCode.Backend -> 23
        | AgentErrorCode.Forge -> 24
        | AgentErrorCode.Authentication -> 25
        | AgentErrorCode.Timeout -> 26
        | AgentErrorCode.Cancellation -> 27
        | AgentErrorCode.OutputLimit -> 28
        | AgentErrorCode.ExternalCommand -> 29
        | AgentErrorCode.RevisionMismatch -> 30

    let private capabilities =
        [ { Operation = AgentOperation.Probe
            Supported = true
            Mutating = false
            Backends = [ "git"; "jj" ]
            Forges = [] }
          { Operation = AgentOperation.Inspect
            Supported = true
            Mutating = false
            Backends = [ "git"; "jj" ]
            Forges = [] }
          { Operation = AgentOperation.Changes
            Supported = true
            Mutating = false
            Backends = [ "git"; "jj" ]
            Forges = [] }
          { Operation = AgentOperation.Commit
            Supported = true
            Mutating = true
            Backends = [ "git"; "jj" ]
            Forges = [] }
          { Operation = AgentOperation.Publish
            Supported = true
            Mutating = true
            Backends = [ "git"; "jj" ]
            Forges = [ "github"; "gitlab" ] }
          { Operation = AgentOperation.CiStatus
            Supported = true
            Mutating = false
            Backends = [ "git"; "jj" ]
            Forges = [ "github"; "gitlab" ] }
          { Operation = AgentOperation.CiWait
            Supported = true
            Mutating = false
            Backends = [ "git"; "jj" ]
            Forges = [ "github"; "gitlab" ] } ]

    let private failure operation code message retryable truncated limitBytes requiredBytes fallbackReason =
        { ContractVersion = ContractVersion
          Operation = operation
          Status = AgentStatus.Error
          Terminal = true
          Data = None
          Error =
            Some
                { Code = code
                  Message = Redaction.redact message
                  Retryable = retryable
                  Truncated = truncated
                  LimitBytes = limitBytes
                  RequiredBytes = requiredBytes }
          Warnings = []
          FallbackReason = fallbackReason }

    /// Construct the deterministic, read-only probe outcome. No repository, VCS executable,
    /// network, environment variable, or machine-local path is inspected.
    let probe toolVersion =
        let version =
            if String.IsNullOrWhiteSpace toolVersion then
                "0.0.0-unknown"
            else
                Redaction.redact toolVersion

        { ContractVersion = ContractVersion
          Operation = operationName AgentOperation.Probe
          Status = AgentStatus.Success
          Terminal = true
          Data =
            Some(
                AgentPayload.Probe
                    { ToolName = "vcs-agent"
                      ToolVersion = version
                      Operations = capabilities
                      Backends = [ "git"; "jj" ]
                      Forges = [ "github"; "gitlab"; "gitea" ]
                      Supervisor =
                        { Mode = "processkit-cli-run"
                          LifecycleProtocol = "jsonl-v1"
                          Required = false } }
            )
          Error = None
          Warnings = []
          FallbackReason = None }

    /// Construct a v1 structured refusal for a declared but unavailable operation.
    let internal unsupported operation =
        failure
            (operationName operation)
            AgentErrorCode.Unsupported
            $"Operation '{operationName operation}' is not implemented by this tool version."
            false
            false
            None
            None
            (Some AgentFallbackReason.OperationNotImplemented)

    /// Construct a redacted v1 invalid-input failure.
    let internal invalidInput operation message =
        failure operation AgentErrorCode.InvalidInput message false false None None None

    let internal outputLimit operation limitBytes requiredBytes =
        failure
            operation
            AgentErrorCode.OutputLimit
            "The operation result exceeded the configured stdout budget."
            false
            true
            (Some limitBytes)
            (Some requiredBytes)
            None

    let private enforceBudget outputLimitBytes envelope =
        if outputLimitBytes < MinimumOutputLimitBytes then
            envelope
        else
            let requiredBytes = EnvelopeSerialization.byteCount envelope

            if requiredBytes <= outputLimitBytes then
                envelope
            else
                outputLimit envelope.Operation outputLimitBytes requiredBytes

    let private withBudget outputLimitBytes pending =
        task {
            let! envelope = pending
            return enforceBudget outputLimitBytes envelope
        }

    let private cancellation operation =
        failure operation AgentErrorCode.Cancellation "The operation was cancelled." false false None None None

    let private processFailure operation defaultCode limitBytes fallbackReason (error: ProcessError) =
        match error with
        | ProcessError.Cancelled _ -> cancellation operation
        | ProcessError.Timeout _ ->
            failure operation AgentErrorCode.Timeout error.Message true false None None fallbackReason
        | ProcessError.OutputTooLarge(_, _, _, _, totalBytes) -> outputLimit operation limitBytes totalBytes
        | ProcessError.NotFound _ ->
            failure
                operation
                AgentErrorCode.ExternalCommand
                error.Message
                false
                false
                None
                None
                (Some AgentFallbackReason.MissingExecutable)
        | _ -> failure operation defaultCode error.Message error.IsTransient false None None fallbackReason

    let private repoFailure operation limitBytes error =
        match error with
        | RepoError.InvalidInput message -> invalidInput operation message
        | RepoError.Unsupported message ->
            failure
                operation
                AgentErrorCode.Unsupported
                message
                false
                false
                None
                None
                (Some AgentFallbackReason.UnsupportedBackend)
        | RepoError.Vcs processError -> processFailure operation AgentErrorCode.Backend limitBytes None processError
        | _ -> failure operation AgentErrorCode.Backend error.Message false false None None None

    let private forgeFailure operation limitBytes error =
        match error with
        | ForgeError.InvalidInput message -> invalidInput operation message
        | ForgeError.Unsupported _
        | ForgeError.UnsupportedVersion _ ->
            failure
                operation
                AgentErrorCode.Unsupported
                error.Message
                false
                false
                None
                None
                (Some AgentFallbackReason.UnsupportedForge)
        | ForgeError.Forge processError when error.IsUnauthorized ->
            processFailure operation AgentErrorCode.Authentication limitBytes None processError
        | ForgeError.Forge processError -> processFailure operation AgentErrorCode.Forge limitBytes None processError

    let private success operation payload =
        { ContractVersion = ContractVersion
          Operation = operation
          Status = AgentStatus.Success
          Terminal = true
          Data = Some payload
          Error = None
          Warnings = []
          FallbackReason = None }

    let private successWithWarnings operation payload warnings =
        { success operation payload with
            Warnings = warnings }

    let private operationState state =
        match state with
        | OperationState.Clear -> "clear"
        | OperationState.Merge -> "merge"
        | OperationState.Rebase -> "rebase"
        | OperationState.ApplyMailbox -> "apply-mailbox"
        | OperationState.CherryPick -> "cherry-pick"
        | OperationState.Revert -> "revert"
        | OperationState.Bisect -> "bisect"
        | OperationState.Conflict -> "conflict"

    let private changeKind kind =
        match kind with
        | ChangeKind.Added -> "added"
        | ChangeKind.Modified -> "modified"
        | ChangeKind.Deleted -> "deleted"
        | ChangeKind.Renamed -> "renamed"

    let private emptyForgeCapabilities =
        { RepositoryIdentity = false
          PullRequestCreate = false
          PullRequestComment = false
          PullRequestEdit = false
          PullRequestChecks = false
          ExactRevisionCi = false
          PullRequestMerge = false
          IssueCreate = false
          IssueReopen = false
          ReleaseDelete = false }

    let private absentForge =
        { Status = AgentForgeStatus.Absent
          Kind = None
          Authenticated = false
          Version = None
          Capabilities = emptyForgeCapabilities }

    let private forgeInfo
        (operation: string)
        (outputLimitBytes: int)
        (cancellationToken: CancellationToken)
        (forge: Forge option)
        =
        task {
            match forge with
            | None -> return Ok absentForge
            | Some forge ->
                let configured = forge.WithAgentExecution(cancellationToken, Some outputLimitBytes)

                match! configured.Capabilities() with
                | Error error -> return Error(forgeFailure operation outputLimitBytes error)
                | Ok capabilities ->
                    let status =
                        match capabilities.Kind with
                        | ForgeKind.Unknown -> AgentForgeStatus.Unsupported
                        | _ when capabilities.Authed -> AgentForgeStatus.Available
                        | _ -> AgentForgeStatus.Unauthenticated

                    return
                        Ok
                            { Status = status
                              Kind =
                                match capabilities.Kind with
                                | ForgeKind.Unknown -> None
                                | kind -> Some kind.AsString
                              Authenticated = capabilities.Authed
                              Version = capabilities.Version |> Option.map string
                              Capabilities =
                                { RepositoryIdentity =
                                    capabilities.Authed
                                    && (capabilities.Kind = ForgeKind.GitHub || capabilities.Kind = ForgeKind.GitLab)
                                  PullRequestCreate = capabilities.PrCreate
                                  PullRequestComment = capabilities.PrComment
                                  PullRequestEdit = capabilities.PrEdit
                                  PullRequestChecks = capabilities.PrChecks
                                  ExactRevisionCi = capabilities.ExactRevisionCi
                                  PullRequestMerge = capabilities.PrMerge
                                  IssueCreate = capabilities.IssueCreate
                                  IssueReopen = capabilities.IssueReopen
                                  ReleaseDelete = capabilities.ReleaseDelete } }
        }

    let private forgeForRemotes (root: string) (remotes: VcsToolkit.Core.Remote list) =
        let detected =
            remotes |> List.tryPick (fun remote -> ForgeKind.OfRemoteUrl remote.Url)

        match detected with
        | Some ForgeKind.GitHub -> Some(Forge.GitHub root)
        | Some ForgeKind.GitLab -> Some(Forge.GitLab root)
        | Some ForgeKind.Gitea -> Some(Forge.Gitea root)
        | Some ForgeKind.Unknown -> Some(Forge.FromUnknown root)
        | None when List.isEmpty remotes -> None
        | None -> Some(Forge.FromUnknown root)

    let private inspectCoreUnbounded
        (repo: Repo)
        (forgeResolver: VcsToolkit.Core.Remote list -> Forge option)
        (cancellationToken: CancellationToken)
        (outputLimitBytes: int)
        =
        task {
            let operation = operationName AgentOperation.Inspect

            if outputLimitBytes < MinimumOutputLimitBytes then
                return invalidInput operation $"output budget must be at least {MinimumOutputLimitBytes} bytes"
            elif cancellationToken.IsCancellationRequested then
                return cancellation operation
            else
                let configured: Repo =
                    repo.WithAgentExecution(cancellationToken, Some outputLimitBytes)

                match! configured.Snapshot() with
                | Error error -> return repoFailure operation outputLimitBytes error
                | Ok snapshot ->
                    match! configured.Remotes() with
                    | Error error -> return repoFailure operation outputLimitBytes error
                    | Ok remotes ->
                        match! forgeInfo operation outputLimitBytes cancellationToken (forgeResolver remotes) with
                        | Error envelope -> return envelope
                        | Ok forge ->
                            let tracking: AgentTracking option =
                                snapshot.Tracking
                                |> Option.map (fun value ->
                                    { Branch = Redaction.redact value.Branch
                                      Ahead = value.Ahead
                                      Behind = value.Behind })

                            let data: InspectData =
                                { Root = configured.Root
                                  Backend = configured.Kind.AsString
                                  Identity =
                                    { Revision = snapshot.Head |> Option.map Redaction.redact
                                      Branch = snapshot.Branch |> Option.map Redaction.redact }
                                  WorkingState =
                                    { Dirty = snapshot.Dirty
                                      ChangeCount = snapshot.ChangeCount
                                      Conflicted = snapshot.Conflicted
                                      Operation = operationState snapshot.Operation
                                      Tracking = tracking }
                                  Remotes =
                                    remotes
                                    |> List.map (fun (remote: VcsToolkit.Core.Remote) ->
                                        { Name = Redaction.redact remote.Name
                                          Url = Redaction.redact remote.Url }
                                        : AgentRemote)
                                  Forge = forge
                                  Operations = capabilities }

                            return success operation (AgentPayload.Inspect data)
        }

    let private inspectCore repo forgeResolver cancellationToken outputLimitBytes =
        inspectCoreUnbounded repo forgeResolver cancellationToken outputLimitBytes
        |> withBudget outputLimitBytes

    let private inspectUnbounded (request: InspectRequest) (cancellationToken: CancellationToken) =
        task {
            let operation = operationName AgentOperation.Inspect

            if request.OutputLimitBytes < MinimumOutputLimitBytes then
                return invalidInput operation $"output budget must be at least {MinimumOutputLimitBytes} bytes"
            elif cancellationToken.IsCancellationRequested then
                return cancellation operation
            else
                match Repo.Open request.RepositoryPath with
                | Error error -> return repoFailure operation request.OutputLimitBytes error
                | Ok repo ->
                    return!
                        inspectCoreUnbounded repo (forgeForRemotes repo.Root) cancellationToken request.OutputLimitBytes
        }

    /// Inspect a repository through the typed Core and Forge facades. The returned envelope is
    /// already replaced by a typed output-limit outcome when its complete wire form is oversized.
    let inspect (request: InspectRequest) (cancellationToken: CancellationToken) =
        inspectUnbounded request cancellationToken
        |> withBudget request.OutputLimitBytes

    let internal inspectWith
        (repo: Repo)
        (forge: Forge option)
        (cancellationToken: CancellationToken)
        outputLimitBytes
        =
        inspectCore repo (fun _ -> forge) cancellationToken outputLimitBytes

    let private changedPath (change: FileChange) =
        { Path = Redaction.redact change.Path
          OldPath = change.OldPath |> Option.map Redaction.redact
          Change = changeKind change.Kind }

    let private diffLine (line: DiffLine) : AgentDiffLine =
        match line with
        | DiffLine.Context text ->
            { Kind = "context"
              Text = Redaction.redact text }
        | DiffLine.Added text ->
            { Kind = "added"
              Text = Redaction.redact text }
        | DiffLine.Removed text ->
            { Kind = "removed"
              Text = Redaction.redact text }

    let private diffHunk (hunk: Hunk) : AgentDiffHunk =
        { OldStart = hunk.OldStart
          OldLines = hunk.OldLines
          NewStart = hunk.NewStart
          NewLines = hunk.NewLines
          Section = Redaction.redact hunk.Section
          Lines = hunk.Lines |> List.map diffLine }

    let private fileDiff (file: FileDiff) =
        { Path = Redaction.redact file.Path
          OldPath = file.OldPath |> Option.map Redaction.redact
          Change = changeKind file.Change
          Hunks = file.Hunks |> List.map diffHunk }

    let private changesCoreUnbounded
        (repo: Repo)
        (mode: ChangesMode)
        (cancellationToken: CancellationToken)
        (outputLimitBytes: int)
        =
        task {
            let operation = operationName AgentOperation.Changes

            if outputLimitBytes < MinimumOutputLimitBytes then
                return invalidInput operation $"output budget must be at least {MinimumOutputLimitBytes} bytes"
            elif cancellationToken.IsCancellationRequested then
                return cancellation operation
            else
                let configured: Repo =
                    repo.WithAgentExecution(cancellationToken, Some outputLimitBytes)

                match mode with
                | ChangesMode.Summary ->
                    match! configured.ChangedFiles() with
                    | Error error -> return repoFailure operation outputLimitBytes error
                    | Ok paths ->
                        match! configured.DiffStat() with
                        | Error error -> return repoFailure operation outputLimitBytes error
                        | Ok stat ->
                            let summary: AgentChangeSummary =
                                { Paths = paths |> List.map changedPath
                                  DiffStat =
                                    { FilesChanged = stat.FilesChanged
                                      Insertions = stat.Insertions
                                      Deletions = stat.Deletions } }

                            return success operation (AgentPayload.Changes(ChangesData.Summary summary))
                | ChangesMode.StructuredDiff ->
                    match! configured.Diff() with
                    | Error error -> return repoFailure operation outputLimitBytes error
                    | Ok files ->
                        return
                            success
                                operation
                                (AgentPayload.Changes(ChangesData.StructuredDiff(files |> List.map fileDiff)))
        }

    let private changesCore repo mode cancellationToken outputLimitBytes =
        changesCoreUnbounded repo mode cancellationToken outputLimitBytes
        |> withBudget outputLimitBytes

    let private changesUnbounded (request: ChangesRequest) (cancellationToken: CancellationToken) =
        task {
            let operation = operationName AgentOperation.Changes

            if request.OutputLimitBytes < MinimumOutputLimitBytes then
                return invalidInput operation $"output budget must be at least {MinimumOutputLimitBytes} bytes"
            elif cancellationToken.IsCancellationRequested then
                return cancellation operation
            else
                match Repo.Open request.RepositoryPath with
                | Error error -> return repoFailure operation request.OutputLimitBytes error
                | Ok repo -> return! changesCoreUnbounded repo request.Mode cancellationToken request.OutputLimitBytes
        }

    /// Return either a compact summary or a bounded structured working-copy diff. The reusable
    /// API and process renderer expose the same typed output-limit outcome.
    let changes (request: ChangesRequest) (cancellationToken: CancellationToken) =
        changesUnbounded request cancellationToken
        |> withBudget request.OutputLimitBytes

    let internal changesWith (repo: Repo) mode (cancellationToken: CancellationToken) outputLimitBytes =
        changesCore repo mode cancellationToken outputLimitBytes

    let private invalidCommitPath path =
        String.IsNullOrWhiteSpace path
        || path.IndexOf('\000') >= 0
        || path.IndexOf('\r') >= 0
        || path.IndexOf('\n') >= 0
        || path.Contains('\\')
        || path.StartsWith('/')
        || (path.Length >= 2 && Char.IsLetter path.[0] && path.[1] = ':')
        || (path.Split('/')
            |> Array.exists (fun segment -> segment.Length = 0 || segment = "." || segment = ".."))

    let private validateCommitPaths paths =

        if obj.ReferenceEquals(paths, null) || List.isEmpty paths then
            Error "commit requires at least one repo-relative path"
        else
            match paths |> List.tryFind invalidCommitPath with
            | Some path ->
                Error $"commit path '{path}' must be a non-empty repo-relative forward-slash path without traversal"
            | None when (paths |> Set.ofList |> Set.count) <> List.length paths ->
                Error "commit paths must not contain duplicates"
            | None -> Ok paths

    let private validateCommitMessage message =
        if String.IsNullOrWhiteSpace message then
            Error "commit message must not be empty"
        elif message.IndexOf('\000') >= 0 then
            Error "commit message must not contain NUL"
        else
            Ok message

    let private changePaths (change: FileChange) =
        match change.OldPath with
        | Some oldPath -> [ oldPath; change.Path ]
        | None -> [ change.Path ]

    let private changedPathSet (changes: FileChange list) =
        changes |> List.collect changePaths |> Set.ofList

    let private prepareCommitPaths validatedPaths (changes: FileChange list) =
        let trySelect path =
            match changes |> List.filter (fun change -> change.Path = path) with
            | [] -> Error $"commit path is not changed in the working copy: {path}"
            | [ change ] ->
                match change.OldPath with
                | Some oldPath when invalidCommitPath oldPath || oldPath = change.Path ->
                    Error $"commit cannot represent renamed path '{path}' atomically"
                | _ -> Ok change
            | _ -> Error $"commit path is ambiguous in the working copy: {path}"

        let selected = validatedPaths |> List.map trySelect

        match
            selected
            |> List.tryPick (function
                | Error error -> Some error
                | Ok _ -> None)
        with
        | Some error -> Error error
        | None ->
            let changes =
                selected
                |> List.choose (function
                    | Ok change -> Some change
                    | Error _ -> None)

            let backendPaths = changes |> List.collect changePaths |> List.distinct
            Ok(backendPaths, Set.ofList backendPaths)

    let private diffPathSet (diffs: FileDiff list) =
        diffs
        |> List.collect (fun diff ->
            match diff.OldPath with
            | Some oldPath -> [ oldPath; diff.Path ]
            | None -> [ diff.Path ])
        |> Set.ofList

    let private commitWarnings unrelatedPaths =
        if Set.isEmpty unrelatedPaths then
            []
        else
            [ { Code = "unrelated-changes-preserved"
                Message = $"{Set.count unrelatedPaths} unrelated changed path(s) remain in the working copy." } ]

    let private commitData
        (repo: Repo)
        before
        requestedPaths
        backendPaths
        observedSnapshot
        observedCreatedRevision
        createdRevision
        paths
        selectedPathsRemaining
        unrelatedPathsPreserved
        completion
        =
        { Root = repo.Root
          Backend = repo.Kind.AsString
          SourceRevision = before.Head
          SourceBranch = before.Branch
          RequestedPaths = requestedPaths
          BackendPaths = backendPaths
          ObservedRevision = observedSnapshot |> Option.bind _.Head
          ObservedBranch = observedSnapshot |> Option.bind _.Branch
          ObservedCreatedRevision = observedCreatedRevision
          CreatedRevision = createdRevision
          Paths = paths
          SelectedPathsRemaining = selectedPathsRemaining
          UnrelatedPathsPreserved = unrelatedPathsPreserved
          Completion = completion }

    let private attachCommitData outputLimitBytes data warnings envelope =
        let attached =
            { envelope with
                Data = Some(AgentPayload.Commit data)
                Warnings = warnings }

        if EnvelopeSerialization.byteCount attached <= outputLimitBytes then
            attached
        else
            let compact =
                { attached with
                    Error =
                        attached.Error
                        |> Option.map (fun error ->
                            { error with
                                Message = "Commit completion is ambiguous; inspect the bounded commit evidence." }) }

            if EnvelopeSerialization.byteCount compact <= outputLimitBytes then
                compact
            else
                let preflightOnly =
                    { data with
                        ObservedRevision = None
                        ObservedBranch = None
                        ObservedCreatedRevision = None
                        CreatedRevision = None
                        Paths = []
                        SelectedPathsRemaining = None
                        UnrelatedPathsPreserved = None
                        Completion = CommitCompletion.Ambiguous }

                let minimal =
                    { compact with
                        Data = Some(AgentPayload.Commit preflightOnly) }

                if EnvelopeSerialization.byteCount minimal <= outputLimitBytes then
                    minimal
                else
                    outputLimit attached.Operation outputLimitBytes (EnvelopeSerialization.byteCount attached)

    let private isDirectGitCommit (repo: Repo) (git: VcsToolkit.Git.Git) beforeRevision candidateRevision =
        task {
            match beforeRevision with
            | Some sourceRevision ->
                match! git.RevParse(repo.Root, $"{candidateRevision}^") with
                | Error _ -> return false
                | Ok parentRevision when parentRevision <> sourceRevision -> return false
                | Ok _ ->
                    match! repo.Log($"{sourceRevision}..{candidateRevision}", 2) with
                    | Ok [ candidate ] -> return candidate.Id = candidateRevision
                    | _ -> return false
            | None ->
                match! repo.Log(candidateRevision, 2) with
                | Ok [ candidate ] -> return candidate.Id = candidateRevision
                | _ -> return false
        }

    let private observedCommitPaths (repo: Repo) beforeRevision createdRevision =
        task {
            match repo.Kind with
            | BackendKind.Git ->
                match repo.Git with
                | None -> return None
                | Some git ->
                    match! isDirectGitCommit repo git beforeRevision createdRevision with
                    | false -> return None
                    | true ->
                        let! fromRevision =
                            match beforeRevision with
                            | Some revision -> task { return Some revision }
                            | None ->
                                task {
                                    match! git.EmptyTreeOid repo.Root with
                                    | Ok revision -> return Some revision
                                    | Error _ -> return None
                                }

                        match fromRevision with
                        | None -> return None
                        | Some revision ->
                            match! git.DiffBetween(repo.Root, revision, createdRevision) with
                            | Ok diffs -> return Some(diffPathSet diffs)
                            | Error _ -> return None
            | BackendKind.Jj ->
                match repo.Jj with
                | None -> return None
                | Some jj ->
                    match! jj.DiffBetween(repo.Cwd, $"{createdRevision}-", createdRevision) with
                    | Ok diffs -> return Some(diffPathSet diffs)
                    | Error _ -> return None
        }

    let private resolveCurrentRevision (repo: Repo) revset observedSnapshot =
        task {
            match observedSnapshot |> Option.bind _.Head with
            | Some revision -> return Some revision
            | None ->
                match! repo.Log(revset, 1) with
                | Ok(commit :: _) -> return Some commit.Id
                | _ -> return None
        }

    let private resolveObservedCreatedRevision (repo: Repo) before observedSnapshot =
        task {
            let currentRevset =
                match repo.Kind with
                | BackendKind.Git -> "HEAD"
                | BackendKind.Jj -> "@"

            match! resolveCurrentRevision repo currentRevset observedSnapshot with
            | None -> return None
            | Some currentRevision when Some currentRevision = before.Head -> return None
            | Some currentRevision ->
                match repo.Kind with
                | BackendKind.Git -> return Some currentRevision
                | BackendKind.Jj ->
                    match! repo.Log("@-", 1) with
                    | Ok(commit :: _) when Some commit.Id <> before.Head -> return Some commit.Id
                    | _ -> return None
        }

    let private commitCoreUnbounded
        (repo: Repo)
        (paths: string list)
        (message: string)
        (cancellationToken: CancellationToken)
        (outputLimitBytes: int)
        =
        task {
            let operation = operationName AgentOperation.Commit

            if outputLimitBytes < MinimumOutputLimitBytes then
                return invalidInput operation $"output budget must be at least {MinimumOutputLimitBytes} bytes"
            elif cancellationToken.IsCancellationRequested then
                return cancellation operation
            else
                match validateCommitPaths paths, validateCommitMessage message with
                | Error validationError, _
                | _, Error validationError -> return invalidInput operation validationError
                | Ok validatedPaths, Ok _ ->
                    let configured: Repo =
                        repo.WithAgentExecution(cancellationToken, Some outputLimitBytes)

                    match! configured.Snapshot() with
                    | Error error -> return repoFailure operation outputLimitBytes error
                    | Ok before when before.Conflicted || before.Operation <> OperationState.Clear ->
                        return
                            failure
                                operation
                                AgentErrorCode.Denied
                                "commit is denied while the repository has conflicts or another operation in progress"
                                false
                                false
                                None
                                None
                                None
                    | Ok before ->
                        match! configured.ChangedFiles() with
                        | Error error -> return repoFailure operation outputLimitBytes error
                        | Ok changesBefore ->
                            match prepareCommitPaths validatedPaths changesBefore with
                            | Error error -> return invalidInput operation error
                            | Ok(backendPaths, selected) ->
                                let changedBefore = changedPathSet changesBefore
                                let unrelatedBefore = changedBefore |> Set.filter (selected.Contains >> not)

                                let warnings = commitWarnings unrelatedBefore

                                let previewRevision = String.replicate 128 "0"

                                let previewData =
                                    commitData
                                        configured
                                        before
                                        validatedPaths
                                        backendPaths
                                        (Some before)
                                        (Some previewRevision)
                                        (Some previewRevision)
                                        backendPaths
                                        (Some false)
                                        (Some true)
                                        CommitCompletion.Verified

                                let successPreview =
                                    successWithWarnings operation (AgentPayload.Commit previewData) warnings

                                let failurePreview =
                                    { failure
                                          operation
                                          AgentErrorCode.Backend
                                          "Commit completion is ambiguous; inspect the bounded commit evidence."
                                          true
                                          false
                                          None
                                          None
                                          None with
                                        Data =
                                            Some(
                                                AgentPayload.Commit
                                                    { previewData with
                                                        Completion = CommitCompletion.Ambiguous }
                                            )
                                        Warnings = warnings }

                                let previewBytes =
                                    max
                                        (EnvelopeSerialization.byteCount successPreview)
                                        (EnvelopeSerialization.byteCount failurePreview)

                                if previewBytes > outputLimitBytes then
                                    return outputLimit operation outputLimitBytes previewBytes
                                else
                                    let! mutationResult = configured.CommitPaths(backendPaths, message)

                                    use recoveryCts = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)

                                    let recovery = repo.WithAgentExecution(recoveryCts.Token, Some outputLimitBytes)

                                    let! observedSnapshotResult = recovery.Snapshot()

                                    let observedSnapshot =
                                        match observedSnapshotResult with
                                        | Ok snapshot -> Some snapshot
                                        | Error _ -> None

                                    let! observedChangesResult = recovery.ChangedFiles()

                                    let observedChanges =
                                        match observedChangesResult with
                                        | Ok changes -> Some(changedPathSet changes)
                                        | Error _ -> None

                                    let selectedPathsRemaining =
                                        observedChanges
                                        |> Option.map (fun changedAfter -> selected |> Set.exists changedAfter.Contains)

                                    let unrelatedPathsPreserved =
                                        observedChanges
                                        |> Option.map (fun changedAfter ->
                                            changedAfter |> Set.filter (selected.Contains >> not) = unrelatedBefore)

                                    let! observedCreatedRevision =
                                        resolveObservedCreatedRevision recovery before observedSnapshot

                                    let! observedPaths =
                                        match observedCreatedRevision with
                                        | Some revision -> observedCommitPaths recovery before.Head revision
                                        | _ -> task { return None }

                                    let createdRevision =
                                        match observedCreatedRevision, observedPaths with
                                        | Some revision, Some paths when paths = selected -> Some revision
                                        | _ -> None

                                    let committedPaths =
                                        match createdRevision, observedPaths with
                                        | Some _, Some paths -> paths |> Set.toList
                                        | _ -> []

                                    let branchPreserved =
                                        observedSnapshot |> Option.exists (fun after -> after.Branch = before.Branch)

                                    let postflightVerified =
                                        branchPreserved
                                        && selectedPathsRemaining = Some false
                                        && unrelatedPathsPreserved = Some true
                                        && createdRevision.IsSome

                                    let completion =
                                        match mutationResult with
                                        | Ok() when postflightVerified -> CommitCompletion.Verified
                                        | _ -> CommitCompletion.Ambiguous

                                    let data =
                                        commitData
                                            recovery
                                            before
                                            validatedPaths
                                            backendPaths
                                            observedSnapshot
                                            observedCreatedRevision
                                            createdRevision
                                            committedPaths
                                            selectedPathsRemaining
                                            unrelatedPathsPreserved
                                            completion

                                    match mutationResult with
                                    | Error error ->
                                        return
                                            repoFailure operation outputLimitBytes error
                                            |> attachCommitData outputLimitBytes data warnings
                                    | Ok() when not branchPreserved ->
                                        return
                                            failure
                                                operation
                                                AgentErrorCode.Backend
                                                "commit changed the current branch or bookmark unexpectedly"
                                                false
                                                false
                                                None
                                                None
                                                None
                                            |> attachCommitData outputLimitBytes data warnings
                                    | Ok() when selectedPathsRemaining <> Some false ->
                                        return
                                            failure
                                                operation
                                                AgentErrorCode.Backend
                                                "commit postflight could not prove that every selected path left the working copy"
                                                false
                                                false
                                                None
                                                None
                                                None
                                            |> attachCommitData outputLimitBytes data warnings
                                    | Ok() when unrelatedPathsPreserved <> Some true ->
                                        return
                                            failure
                                                operation
                                                AgentErrorCode.Backend
                                                "commit postflight could not prove that unrelated changed paths were preserved"
                                                false
                                                false
                                                None
                                                None
                                                None
                                            |> attachCommitData outputLimitBytes data warnings
                                    | Ok() when createdRevision.IsNone ->
                                        return
                                            failure
                                                operation
                                                AgentErrorCode.Backend
                                                "commit postflight could not verify the created revision's exact changed-path set"
                                                false
                                                false
                                                None
                                                None
                                                None
                                            |> attachCommitData outputLimitBytes data warnings
                                    | Ok() -> return successWithWarnings operation (AgentPayload.Commit data) warnings
        }

    let private commitCore repo paths message cancellationToken outputLimitBytes =
        commitCoreUnbounded repo paths message cancellationToken outputLimitBytes
        |> withBudget outputLimitBytes

    /// Commit exactly the requested changed repo-relative paths. Validation and repository
    /// preflight complete before `Repo.CommitPaths` is invoked, and the returned envelope has
    /// already passed the complete serialized-output budget.
    let commit (request: CommitRequest) (cancellationToken: CancellationToken) =
        task {
            let operation = operationName AgentOperation.Commit

            if request.OutputLimitBytes < MinimumOutputLimitBytes then
                return invalidInput operation $"output budget must be at least {MinimumOutputLimitBytes} bytes"
            elif cancellationToken.IsCancellationRequested then
                return cancellation operation
            elif String.IsNullOrWhiteSpace request.RepositoryPath then
                return invalidInput operation "repository path must not be empty"
            else
                match validateCommitPaths request.Paths, validateCommitMessage request.Message with
                | Error validationError, _
                | _, Error validationError -> return invalidInput operation validationError
                | Ok _, Ok _ ->
                    match Repo.Open request.RepositoryPath with
                    | Error error -> return repoFailure operation request.OutputLimitBytes error
                    | Ok repo ->
                        return!
                            commitCoreUnbounded
                                repo
                                request.Paths
                                request.Message
                                cancellationToken
                                request.OutputLimitBytes
        }
        |> withBudget request.OutputLimitBytes

    let internal commitWith (repo: Repo) paths message (cancellationToken: CancellationToken) outputLimitBytes =
        commitCore repo paths message cancellationToken outputLimitBytes

    let private forgeKindName forge =
        match forge with
        | AgentForgeKind.GitHub -> "github"
        | AgentForgeKind.GitLab -> "gitlab"
        | AgentForgeKind.Gitea -> "gitea"

    let private forgeKind forge =
        match forge with
        | AgentForgeKind.GitHub -> ForgeKind.GitHub
        | AgentForgeKind.GitLab -> ForgeKind.GitLab
        | AgentForgeKind.Gitea -> ForgeKind.Gitea

    let private forgeForKind root forge =
        match forge with
        | AgentForgeKind.GitHub -> Forge.GitHub root
        | AgentForgeKind.GitLab -> Forge.GitLab root
        | AgentForgeKind.Gitea -> Forge.Gitea root

    let private validIdentity (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= 256
        && not (value.StartsWith("-", StringComparison.Ordinal))
        && not (value.EndsWith("/", StringComparison.Ordinal))
        && not (value.EndsWith(".", StringComparison.Ordinal))
        && not (value.Contains("..", StringComparison.Ordinal))
        && not (value.Contains("//", StringComparison.Ordinal))
        && (value
            |> Seq.forall (fun character ->
                Char.IsAsciiLetterOrDigit character
                || character = '-'
                || character = '_'
                || character = '.'
                || character = '/'))

    let private validRevision (value: string) =
        (value.Length = 40 || value.Length = 64) && value |> Seq.forall Uri.IsHexDigit

    let private validatePublicationIdentity repositoryPath branch remote revision account targetBranch =
        if String.IsNullOrWhiteSpace repositoryPath then
            Error "repository path must not be empty"
        elif not (validIdentity branch) then
            Error "branch/bookmark must be one explicit, well-formed ref name"
        elif not (validIdentity remote) || remote.Contains("/", StringComparison.Ordinal) then
            Error "remote must be one explicit configured remote name"
        elif not (validRevision revision) then
            Error "revision must be one full 40- or 64-character hexadecimal commit id"
        elif not (validIdentity account) || account.Contains("/", StringComparison.Ordinal) then
            Error "account must be one explicit forge login/username"
        elif not (validIdentity targetBranch) then
            Error "target branch must be one explicit, well-formed ref name"
        else
            Ok()

    type private SelectedForgeRepository =
        { Host: string
          Selector: string
          ProjectPath: string }

    let private selectedForgeRepository expectedForge (remoteUrl: string) =
        let finish (host: string) (rawPath: string) =
            try
                let projectPath =
                    Uri.UnescapeDataString rawPath
                    |> fun value -> value.Trim('/')
                    |> fun value ->
                        if value.EndsWith(".git", StringComparison.OrdinalIgnoreCase) then
                            value.Substring(0, value.Length - 4)
                        else
                            value

                if
                    String.IsNullOrWhiteSpace host
                    || String.IsNullOrWhiteSpace projectPath
                    || projectPath.StartsWith("-", StringComparison.Ordinal)
                    || projectPath.Contains('\\')
                    || projectPath.Contains("..", StringComparison.Ordinal)
                    || projectPath.Contains("//", StringComparison.Ordinal)
                    || projectPath |> Seq.exists Char.IsControl
                then
                    None
                else
                    let canonicalHost = host.ToLowerInvariant()

                    match expectedForge with
                    | ForgeKind.GitHub ->
                        Some
                            { Host = canonicalHost
                              Selector = $"{canonicalHost}/{projectPath}"
                              ProjectPath = projectPath }
                    | ForgeKind.GitLab ->
                        Some
                            { Host = canonicalHost
                              Selector = $"https://{canonicalHost}/{projectPath}"
                              ProjectPath = projectPath }
                    | _ -> None
            with :? UriFormatException ->
                None

        let parsedUri =
            try
                Some(Uri(remoteUrl, UriKind.Absolute))
            with :? UriFormatException ->
                None

        match parsedUri with
        | Some uri when not (String.IsNullOrWhiteSpace uri.Host) -> finish uri.Host uri.AbsolutePath
        | _ ->
            let separator = remoteUrl.IndexOfAny([| ':'; '/' |])

            if separator < 0 || separator = remoteUrl.Length - 1 then
                None
            else
                let host = RemoteUrl.scpAuthority remoteUrl |> RemoteUrl.stripPort
                let path = remoteUrl.Substring(separator + 1).Split([| '?'; '#' |]).[0]
                finish host path

    let private configuredRemote operation outputLimitBytes (repo: Repo) remote expectedForge =
        task {
            match! repo.Remotes() with
            | Error error -> return Error(repoFailure operation outputLimitBytes error)
            | Ok remotes ->
                match remotes |> List.filter (fun configured -> configured.Name = remote) with
                | [ configured ] ->
                    match
                        ForgeKind.OfRemoteUrl configured.Url, selectedForgeRepository expectedForge configured.Url
                    with
                    | Some kind, Some repository when kind = expectedForge -> return Ok repository
                    | Some kind, _ when kind <> expectedForge ->
                        return
                            Error(
                                invalidInput
                                    operation
                                    $"remote '{remote}' identifies {kind.AsString}, not {expectedForge.AsString}"
                            )
                    | Some _, None ->
                        return
                            Error(
                                failure
                                    operation
                                    AgentErrorCode.Unsupported
                                    $"remote '{remote}' does not expose a verifiable repository identity"
                                    false
                                    false
                                    None
                                    None
                                    (Some AgentFallbackReason.UnsupportedForge)
                            )
                    | _ ->
                        return
                            Error(
                                failure
                                    operation
                                    AgentErrorCode.Unsupported
                                    $"remote '{remote}' does not expose a verifiable forge identity"
                                    false
                                    false
                                    None
                                    None
                                    (Some AgentFallbackReason.UnsupportedForge)
                            )
                | [] -> return Error(invalidInput operation $"remote '{remote}' is not configured")
                | _ -> return Error(invalidInput operation $"remote '{remote}' is ambiguous")
        }

    let private resolvedRevision (repo: Repo) reference =
        task {
            match! repo.Log(reference, 1) with
            | Error error -> return Error error
            | Ok(commit :: _) -> return Ok(Some commit.Id)
            | Ok [] -> return Ok None
        }

    let private remoteReference (repo: Repo) remote branch =
        match repo.Kind with
        | BackendKind.Git -> $"refs/remotes/{remote}/{branch}"
        | BackendKind.Jj -> $"{branch}@{remote}"

    let private observeRemoteRevision (repo: Repo) remote branch =
        task {
            match! repo.FetchFrom remote with
            | Error error -> return Error error
            | Ok() ->
                match! resolvedRevision repo (remoteReference repo remote branch) with
                | Ok revision -> return Ok revision
                | Error _ ->
                    // A successful fetch with no matching remote ref is the expected preflight
                    // state for first publication; postflight still requires an exact value.
                    return Ok None
        }

    let private publicationEvidence forge account branch remote revision (repo: Repo) remoteRevision =
        { Root = repo.Root
          Backend = repo.Kind.AsString
          Forge = forgeKindName forge
          Account = account
          Branch = branch
          Remote = remote
          LocalRevision = revision
          RemoteRevision = remoteRevision }

    let private attachPayload payload envelope = { envelope with Data = Some payload }

    let private ambiguousPublishFailure outputLimitBytes data envelope =
        envelope
        |> attachPayload (
            AgentPayload.Publish
                { data with
                    Completion = PublishCompletion.Ambiguous }
        )
        |> enforceBudget outputLimitBytes

    type private ChangeRequestRecoveryMatch =
        | NoRelevantCandidate
        | ExactCandidates of ForgePr list
        | CandidateMismatch of AgentErrorCode * string

    let private matchingOpenChangeRequests
        forge
        selectedRepository
        branch
        target
        revision
        (candidates: AgentChangeRequestCandidate list)
        =
        let sameBranches candidate =
            candidate.ChangeRequest.State = ForgePrState.Open
            && candidate.ChangeRequest.SourceBranch = branch
            && candidate.ChangeRequest.TargetBranch = target

        let relevant = candidates |> List.filter sameBranches

        let missingProof candidate =
            String.IsNullOrWhiteSpace candidate.SourceRepository
            || String.IsNullOrWhiteSpace candidate.HeadRevision
            || (forge = AgentForgeKind.GitLab
                && (candidate.TargetRepository |> Option.forall String.IsNullOrWhiteSpace))

        let sameRepository candidate =
            match forge with
            | AgentForgeKind.GitHub ->
                String.Equals(candidate.SourceRepository, selectedRepository, StringComparison.OrdinalIgnoreCase)
            | AgentForgeKind.GitLab ->
                candidate.TargetRepository
                |> Option.exists (fun targetRepository ->
                    String.Equals(candidate.SourceRepository, targetRepository, StringComparison.Ordinal))
            | AgentForgeKind.Gitea -> false

        if List.isEmpty relevant then
            NoRelevantCandidate
        elif relevant |> List.exists missingProof then
            CandidateMismatch(
                AgentErrorCode.Forge,
                "open PR/MR recovery candidate is missing source repository or exact head revision evidence"
            )
        elif
            relevant
            |> List.exists (fun candidate ->
                sameRepository candidate
                && not (String.Equals(candidate.HeadRevision, revision, StringComparison.OrdinalIgnoreCase)))
        then
            CandidateMismatch(
                AgentErrorCode.RevisionMismatch,
                "an open PR/MR from the selected repository matches the selected branches but its head revision does not match the requested publication revision"
            )
        elif relevant |> List.exists (sameRepository >> not) then
            CandidateMismatch(
                AgentErrorCode.Forge,
                "an open PR/MR matches the selected branches but its source repository does not match the selected remote repository"
            )
        else
            relevant |> List.map _.ChangeRequest |> ExactCandidates

    let private publishCoreUnbounded
        (repo: Repo)
        (forge: Forge)
        (request: PublishRequest)
        (cancellationToken: CancellationToken)
        =
        task {
            let operation = operationName AgentOperation.Publish
            let outputLimitBytes = request.OutputLimitBytes

            if outputLimitBytes < MinimumOutputLimitBytes then
                return invalidInput operation $"output budget must be at least {MinimumOutputLimitBytes} bytes"
            elif cancellationToken.IsCancellationRequested then
                return cancellation operation
            elif String.IsNullOrWhiteSpace request.Title then
                return invalidInput operation "pull/merge request title must not be empty"
            else
                match
                    validatePublicationIdentity
                        request.RepositoryPath
                        request.Branch
                        request.Remote
                        request.Revision
                        request.Account
                        request.TargetBranch
                with
                | Error message -> return invalidInput operation message
                | Ok() ->
                    let configuredRepo =
                        repo.WithAgentExecution(cancellationToken, Some outputLimitBytes)

                    let configuredForge =
                        forge.WithAgentExecution(cancellationToken, Some outputLimitBytes)

                    if configuredForge.Kind <> forgeKind request.Forge then
                        return invalidInput operation "selected forge handle does not match the request"
                    else
                        match!
                            configuredRemote
                                operation
                                outputLimitBytes
                                configuredRepo
                                request.Remote
                                configuredForge.Kind
                        with
                        | Error envelope -> return envelope
                        | Ok repository ->
                            let selectedForge =
                                configuredForge.WithAgentRepository(
                                    repository.Host,
                                    repository.Selector,
                                    repository.ProjectPath
                                )

                            match! selectedForge.AuthIdentity() with
                            | Error error -> return forgeFailure operation outputLimitBytes error
                            | Ok observedAccount when
                                not (
                                    String.Equals(observedAccount, request.Account, StringComparison.OrdinalIgnoreCase)
                                )
                                ->
                                return
                                    failure
                                        operation
                                        AgentErrorCode.Authentication
                                        $"authenticated forge account '{observedAccount}' does not match requested account '{request.Account}'"
                                        false
                                        false
                                        None
                                        None
                                        None
                            | Ok _ ->
                                match! resolvedRevision configuredRepo request.Revision with
                                | Error error -> return repoFailure operation outputLimitBytes error
                                | Ok(Some localRevision) when localRevision <> request.Revision ->
                                    return
                                        failure
                                            operation
                                            AgentErrorCode.RevisionMismatch
                                            "the requested local revision did not resolve to itself"
                                            false
                                            false
                                            None
                                            None
                                            None
                                | Ok None -> return invalidInput operation "the requested local revision does not exist"
                                | Ok(Some _) ->
                                    match! resolvedRevision configuredRepo request.Branch with
                                    | Error error -> return repoFailure operation outputLimitBytes error
                                    | Ok branchRevision when branchRevision <> Some request.Revision ->
                                        return
                                            failure
                                                operation
                                                AgentErrorCode.RevisionMismatch
                                                "the selected branch/bookmark does not resolve to the requested local revision"
                                                false
                                                false
                                                None
                                                None
                                                None
                                    | Ok _ ->
                                        match! observeRemoteRevision configuredRepo request.Remote request.Branch with
                                        | Error error -> return repoFailure operation outputLimitBytes error
                                        | Ok beforeRemote ->
                                            let preflight =
                                                publicationEvidence
                                                    request.Forge
                                                    request.Account
                                                    request.Branch
                                                    request.Remote
                                                    request.Revision
                                                    configuredRepo
                                                    beforeRemote

                                            let prospective: PublishData =
                                                { Preflight = preflight
                                                  Postflight = Some preflight
                                                  ChangeRequest =
                                                    Some
                                                        { Number = UInt64.MaxValue
                                                          Url = String('x', 2048)
                                                          SourceBranch = request.Branch
                                                          TargetBranch = request.TargetBranch
                                                          Disposition = PublicationChangeRequestDisposition.Created }
                                                  Completion = PublishCompletion.Verified }

                                            let prospectiveEnvelope =
                                                success operation (AgentPayload.Publish prospective)

                                            let requiredBytes = EnvelopeSerialization.byteCount prospectiveEnvelope

                                            if requiredBytes > outputLimitBytes then
                                                return outputLimit operation outputLimitBytes requiredBytes
                                            else
                                                let! pushed =
                                                    if beforeRemote = Some request.Revision then
                                                        task { return Ok() }
                                                    else
                                                        match configuredRepo.Git, configuredRepo.Jj with
                                                        | Some git, None ->
                                                            git.Push(
                                                                configuredRepo.Cwd,
                                                                VcsToolkit.Git.GitPush
                                                                    .ForRefspec(request.Revision, request.Branch)
                                                                    .WithRemote(request.Remote)
                                                            )
                                                        | None, Some jj when request.Remote = "origin" ->
                                                            jj.GitPush(configuredRepo.Cwd, Some request.Branch)
                                                        | None, Some _ ->
                                                            task {
                                                                return
                                                                    Error(
                                                                        ProcessError.Spawn(
                                                                            "jj",
                                                                            "checked publication currently supports only the explicit origin remote on Jujutsu"
                                                                        )
                                                                    )
                                                            }
                                                        | _ ->
                                                            task {
                                                                return
                                                                    Error(
                                                                        ProcessError.Spawn(
                                                                            "vcs-agent",
                                                                            "repository backend is unavailable"
                                                                        )
                                                                    )
                                                            }

                                                let! afterRemoteResult =
                                                    observeRemoteRevision configuredRepo request.Remote request.Branch

                                                let afterRemote =
                                                    match afterRemoteResult with
                                                    | Ok revision -> revision
                                                    | Error _ -> None

                                                let postflight =
                                                    publicationEvidence
                                                        request.Forge
                                                        request.Account
                                                        request.Branch
                                                        request.Remote
                                                        request.Revision
                                                        configuredRepo
                                                        afterRemote

                                                let ambiguousData: PublishData =
                                                    { Preflight = preflight
                                                      Postflight = Some postflight
                                                      ChangeRequest = None
                                                      Completion = PublishCompletion.Ambiguous }

                                                match pushed, afterRemoteResult with
                                                | Error pushError, _ when afterRemote <> Some request.Revision ->
                                                    return
                                                        processFailure
                                                            operation
                                                            AgentErrorCode.Backend
                                                            outputLimitBytes
                                                            None
                                                            pushError
                                                        |> ambiguousPublishFailure outputLimitBytes ambiguousData
                                                | _, Error error ->
                                                    return
                                                        repoFailure operation outputLimitBytes error
                                                        |> ambiguousPublishFailure outputLimitBytes ambiguousData
                                                | _, Ok observed when observed <> Some request.Revision ->
                                                    return
                                                        failure
                                                            operation
                                                            AgentErrorCode.RevisionMismatch
                                                            "the observed remote revision does not match the requested publication revision"
                                                            false
                                                            false
                                                            None
                                                            None
                                                            None
                                                        |> ambiguousPublishFailure outputLimitBytes ambiguousData
                                                | _ ->
                                                    match!
                                                        selectedForge.PrForBranchesComplete(
                                                            request.Branch,
                                                            request.TargetBranch
                                                        )
                                                    with
                                                    | Error error ->
                                                        return
                                                            forgeFailure operation outputLimitBytes error
                                                            |> ambiguousPublishFailure outputLimitBytes ambiguousData
                                                    | Ok existing ->
                                                        match
                                                            matchingOpenChangeRequests
                                                                request.Forge
                                                                repository.ProjectPath
                                                                request.Branch
                                                                request.TargetBranch
                                                                request.Revision
                                                                existing
                                                        with
                                                        | CandidateMismatch(code, message) ->
                                                            return
                                                                failure
                                                                    operation
                                                                    code
                                                                    message
                                                                    false
                                                                    false
                                                                    None
                                                                    None
                                                                    None
                                                                |> ambiguousPublishFailure
                                                                    outputLimitBytes
                                                                    ambiguousData
                                                        | ExactCandidates [ changeRequest ] ->
                                                            let data =
                                                                { ambiguousData with
                                                                    ChangeRequest =
                                                                        Some
                                                                            { Number = changeRequest.Number
                                                                              Url = changeRequest.Url
                                                                              SourceBranch = changeRequest.SourceBranch
                                                                              TargetBranch = changeRequest.TargetBranch
                                                                              Disposition =
                                                                                PublicationChangeRequestDisposition.Existing }
                                                                    Completion = PublishCompletion.Verified }

                                                            return success operation (AgentPayload.Publish data)
                                                        | ExactCandidates(_ :: _ :: _) ->
                                                            return
                                                                failure
                                                                    operation
                                                                    AgentErrorCode.Forge
                                                                    "more than one open PR/MR matches the selected source and target branches"
                                                                    false
                                                                    false
                                                                    None
                                                                    None
                                                                    None
                                                                |> ambiguousPublishFailure
                                                                    outputLimitBytes
                                                                    ambiguousData
                                                        | NoRelevantCandidate
                                                        | ExactCandidates [] ->
                                                            let spec =
                                                                PrCreate
                                                                    .Create(request.Title, request.Body)
                                                                    .WithSource(request.Branch)
                                                                    .WithTarget(request.TargetBranch)

                                                            let! created = selectedForge.PrCreate spec

                                                            let! recovered =
                                                                selectedForge.PrForBranchesComplete(
                                                                    request.Branch,
                                                                    request.TargetBranch
                                                                )

                                                            match recovered with
                                                            | Ok requests ->
                                                                match
                                                                    matchingOpenChangeRequests
                                                                        request.Forge
                                                                        repository.ProjectPath
                                                                        request.Branch
                                                                        request.TargetBranch
                                                                        request.Revision
                                                                        requests
                                                                with
                                                                | ExactCandidates [ changeRequest ] ->
                                                                    let data =
                                                                        { ambiguousData with
                                                                            ChangeRequest =
                                                                                Some
                                                                                    { Number = changeRequest.Number
                                                                                      Url = changeRequest.Url
                                                                                      SourceBranch =
                                                                                        changeRequest.SourceBranch
                                                                                      TargetBranch =
                                                                                        changeRequest.TargetBranch
                                                                                      Disposition =
                                                                                        match created with
                                                                                        | Ok _ ->
                                                                                            PublicationChangeRequestDisposition.Created
                                                                                        | Error _ ->
                                                                                            PublicationChangeRequestDisposition.Existing }
                                                                            Completion = PublishCompletion.Verified }

                                                                    return success operation (AgentPayload.Publish data)
                                                                | CandidateMismatch(code, message) ->
                                                                    return
                                                                        failure
                                                                            operation
                                                                            code
                                                                            message
                                                                            false
                                                                            false
                                                                            None
                                                                            None
                                                                            None
                                                                        |> ambiguousPublishFailure
                                                                            outputLimitBytes
                                                                            ambiguousData
                                                                | ExactCandidates []
                                                                | ExactCandidates(_ :: _ :: _)
                                                                | NoRelevantCandidate ->
                                                                    let envelope =
                                                                        match created with
                                                                        | Error error ->
                                                                            forgeFailure
                                                                                operation
                                                                                outputLimitBytes
                                                                                error
                                                                        | Ok _ ->
                                                                            failure
                                                                                operation
                                                                                AgentErrorCode.Forge
                                                                                "PR/MR creation completed but exact recovery was not unique"
                                                                                false
                                                                                false
                                                                                None
                                                                                None
                                                                                None

                                                                    return
                                                                        envelope
                                                                        |> ambiguousPublishFailure
                                                                            outputLimitBytes
                                                                            ambiguousData
                                                            | Error error ->
                                                                let envelope =
                                                                    match created with
                                                                    | Error createError ->
                                                                        forgeFailure
                                                                            operation
                                                                            outputLimitBytes
                                                                            createError
                                                                    | Ok _ ->
                                                                        forgeFailure operation outputLimitBytes error

                                                                return
                                                                    envelope
                                                                    |> ambiguousPublishFailure
                                                                        outputLimitBytes
                                                                        ambiguousData
        }

    /// Publish one explicit revision and recover one matching PR/MR without duplicate creation.
    let publish (request: PublishRequest) (cancellationToken: CancellationToken) =
        task {
            let operation = operationName AgentOperation.Publish

            if request.OutputLimitBytes < MinimumOutputLimitBytes then
                return invalidInput operation $"output budget must be at least {MinimumOutputLimitBytes} bytes"
            elif cancellationToken.IsCancellationRequested then
                return cancellation operation
            elif String.IsNullOrWhiteSpace request.Title then
                return invalidInput operation "pull/merge request title must not be empty"
            else
                match
                    validatePublicationIdentity
                        request.RepositoryPath
                        request.Branch
                        request.Remote
                        request.Revision
                        request.Account
                        request.TargetBranch
                with
                | Error message -> return invalidInput operation message
                | Ok() ->
                    match Repo.Open request.RepositoryPath with
                    | Error error -> return repoFailure operation request.OutputLimitBytes error
                    | Ok repo ->
                        return!
                            publishCoreUnbounded repo (forgeForKind repo.Root request.Forge) request cancellationToken
        }
        |> withBudget request.OutputLimitBytes

    let internal publishWith
        (repo: Repo)
        (forge: Forge)
        (request: PublishRequest)
        (cancellationToken: CancellationToken)
        =
        publishCoreUnbounded repo forge request cancellationToken
        |> withBudget request.OutputLimitBytes

    let internal ciState (runs: ForgeCiRun list) =
        let lower (value: string) = value.ToLowerInvariant()

        let statuses =
            runs
            |> List.map (fun run -> lower run.Status, run.Conclusion |> Option.map lower)

        if List.isEmpty statuses then
            AgentCiState.NoRuns
        elif
            statuses
            |> List.exists (fun (status, _) ->
                status <> "completed"
                && status <> "success"
                && status <> "failed"
                && status <> "canceled"
                && status <> "cancelled"
                && status <> "skipped")
        then
            AgentCiState.Pending
        elif
            statuses
            |> List.exists (fun (status, conclusion) ->
                status = "canceled"
                || status = "cancelled"
                || conclusion = Some "cancelled"
                || conclusion = Some "canceled")
        then
            AgentCiState.Cancelled
        elif
            statuses
            |> List.exists (fun (status, conclusion) ->
                status = "failed"
                || conclusion = Some "failure"
                || conclusion = Some "failed"
                || conclusion = Some "timed_out"
                || conclusion = Some "action_required")
        then
            AgentCiState.Failure
        elif
            statuses
            |> List.exists (fun (status, conclusion) ->
                status = "skipped"
                || conclusion = Some "skipped"
                || conclusion = Some "neutral"
                || conclusion = Some "stale")
        then
            AgentCiState.Skipped
        elif
            statuses
            |> List.forall (fun (status, conclusion) ->
                status = "success" || (status = "completed" && conclusion = Some "success"))
        then
            AgentCiState.Success
        else
            AgentCiState.Failure

    let private ciTerminal state =
        match state with
        | AgentCiState.Success
        | AgentCiState.Failure
        | AgentCiState.Cancelled
        | AgentCiState.Skipped
        | AgentCiState.RevisionMismatch -> true
        | AgentCiState.NoRuns
        | AgentCiState.Pending -> false

    let private ciEnvelope operation (data: CiData) =
        { ContractVersion = ContractVersion
          Operation = operation
          Status = AgentStatus.Success
          Terminal = ciTerminal data.State
          Data = Some(AgentPayload.Ci data)
          Error = None
          Warnings = []
          FallbackReason = None }

    let private ciFailureWithData data envelope =
        attachPayload (AgentPayload.Ci data) envelope

    let private ciObservation
        operation
        outputLimitBytes
        (repo: Repo)
        (forge: Forge)
        forgeSelection
        account
        branch
        remote
        revision
        pollCount
        =
        task {
            match! forge.ExactRevisionCi revision with
            | Error error -> return Error(forgeFailure operation outputLimitBytes error)
            | Ok observed ->
                let exact =
                    observed
                    |> List.filter (fun run -> run.Revision = revision && run.Branch = branch)

                let mapped =
                    exact
                    |> List.map (fun run ->
                        { Id = run.Id
                          Name = run.Name
                          Status = run.Status
                          Conclusion = run.Conclusion
                          Revision = run.Revision
                          Url = run.Url }
                        : AgentCiRun)

                let mismatch =
                    observed |> List.exists (fun run -> run.Revision <> revision)
                    || (not (List.isEmpty observed) && List.isEmpty exact)

                let data =
                    { Root = repo.Root
                      Forge = forgeKindName forgeSelection
                      Account = account
                      Branch = branch
                      Remote = remote
                      Revision = revision
                      State =
                        if mismatch then
                            AgentCiState.RevisionMismatch
                        else
                            ciState exact
                      Runs = mapped
                      PollCount = pollCount }

                if mismatch then
                    return
                        Error(
                            failure
                                operation
                                AgentErrorCode.RevisionMismatch
                                "forge CI results did not belong to the requested branch and exact revision"
                                false
                                false
                                None
                                None
                                None
                            |> ciFailureWithData data
                        )
                else
                    return Ok data
        }

    let private ciPreflight
        operation
        outputLimitBytes
        (repo: Repo)
        (forge: Forge)
        forgeSelection
        account
        branch
        remote
        revision
        cancellationToken
        =
        task {
            let configuredRepo =
                repo.WithAgentExecution(cancellationToken, Some outputLimitBytes)

            match! configuredRemote operation outputLimitBytes configuredRepo remote (forgeKind forgeSelection) with
            | Error envelope -> return Error envelope
            | Ok repository ->
                let selectedForge =
                    forge.WithAgentRepository(repository.Host, repository.Selector, repository.ProjectPath)

                match! selectedForge.AuthIdentity() with
                | Error error -> return Error(forgeFailure operation outputLimitBytes error)
                | Ok observedAccount when
                    not (String.Equals(observedAccount, account, StringComparison.OrdinalIgnoreCase))
                    ->
                    return
                        Error(
                            failure
                                operation
                                AgentErrorCode.Authentication
                                $"authenticated forge account '{observedAccount}' does not match requested account '{account}'"
                                false
                                false
                                None
                                None
                                None
                        )
                | Ok _ ->
                    match! observeRemoteRevision configuredRepo remote branch with
                    | Error error -> return Error(repoFailure operation outputLimitBytes error)
                    | Ok observed when observed <> Some revision ->
                        return
                            Error(
                                failure
                                    operation
                                    AgentErrorCode.RevisionMismatch
                                    "the selected remote branch/bookmark is not at the requested published revision"
                                    false
                                    false
                                    None
                                    None
                                    None
                            )
                    | Ok _ -> return Ok(configuredRepo, selectedForge)
        }

    let private ciStatusCore
        (repo: Repo)
        (forge: Forge)
        (request: CiStatusRequest)
        (cancellationToken: CancellationToken)
        =
        task {
            let operation = operationName AgentOperation.CiStatus

            if request.OutputLimitBytes < MinimumOutputLimitBytes then
                return invalidInput operation $"output budget must be at least {MinimumOutputLimitBytes} bytes"
            elif cancellationToken.IsCancellationRequested then
                return cancellation operation
            else
                match
                    validatePublicationIdentity
                        request.RepositoryPath
                        request.Branch
                        request.Remote
                        request.Revision
                        request.Account
                        request.Branch
                with
                | Error message -> return invalidInput operation message
                | Ok() ->
                    let configuredForge =
                        forge.WithAgentExecution(cancellationToken, Some request.OutputLimitBytes)

                    if configuredForge.Kind <> forgeKind request.Forge then
                        return invalidInput operation "selected forge handle does not match the request"
                    else
                        match!
                            ciPreflight
                                operation
                                request.OutputLimitBytes
                                repo
                                configuredForge
                                request.Forge
                                request.Account
                                request.Branch
                                request.Remote
                                request.Revision
                                cancellationToken
                        with
                        | Error envelope -> return envelope
                        | Ok(configuredRepo, selectedForge) ->
                            match!
                                ciObservation
                                    operation
                                    request.OutputLimitBytes
                                    configuredRepo
                                    selectedForge
                                    request.Forge
                                    request.Account
                                    request.Branch
                                    request.Remote
                                    request.Revision
                                    1UL
                            with
                            | Error envelope -> return envelope
                            | Ok data -> return ciEnvelope operation data
        }

    /// Observe CI runs/pipelines for one proven published revision.
    let ciStatus (request: CiStatusRequest) (cancellationToken: CancellationToken) =
        task {
            let operation = operationName AgentOperation.CiStatus

            if request.OutputLimitBytes < MinimumOutputLimitBytes then
                return invalidInput operation $"output budget must be at least {MinimumOutputLimitBytes} bytes"
            elif cancellationToken.IsCancellationRequested then
                return cancellation operation
            else
                match
                    validatePublicationIdentity
                        request.RepositoryPath
                        request.Branch
                        request.Remote
                        request.Revision
                        request.Account
                        request.Branch
                with
                | Error message -> return invalidInput operation message
                | Ok() ->
                    match Repo.Open request.RepositoryPath with
                    | Error error -> return repoFailure operation request.OutputLimitBytes error
                    | Ok repo ->
                        return! ciStatusCore repo (forgeForKind repo.Root request.Forge) request cancellationToken
        }
        |> withBudget request.OutputLimitBytes

    let internal ciStatusWith repo forge request cancellationToken =
        ciStatusCore repo forge request cancellationToken
        |> withBudget request.OutputLimitBytes

    let rec private ciPoll
        operation
        (request: CiWaitRequest)
        (configuredRepo: Repo)
        (configuredForge: Forge)
        (cancellationToken: CancellationToken)
        (executionToken: CancellationToken)
        (clock: System.Diagnostics.Stopwatch)
        pollCount
        (previousSignature: (AgentCiState * (string * string * string option) list) option)
        (lastActivity: TimeSpan)
        (lastData: CiData option)
        =
        task {
            if cancellationToken.IsCancellationRequested then
                return
                    cancellation operation
                    |> (fun envelope ->
                        match lastData with
                        | Some data -> ciFailureWithData data envelope
                        | None -> envelope)
            elif executionToken.IsCancellationRequested || clock.Elapsed >= request.Deadline then
                return
                    failure
                        operation
                        AgentErrorCode.Timeout
                        "CI wait reached its overall deadline"
                        true
                        false
                        None
                        None
                        None
                    |> (fun envelope ->
                        match lastData with
                        | Some data -> ciFailureWithData data envelope
                        | None -> envelope)
            else
                match!
                    ciObservation
                        operation
                        request.OutputLimitBytes
                        configuredRepo
                        configuredForge
                        request.Forge
                        request.Account
                        request.Branch
                        request.Remote
                        request.Revision
                        pollCount
                with
                | Error _ when cancellationToken.IsCancellationRequested ->
                    return
                        cancellation operation
                        |> (fun envelope ->
                            match lastData with
                            | Some data -> ciFailureWithData data envelope
                            | None -> envelope)
                | Error _ when executionToken.IsCancellationRequested || clock.Elapsed >= request.Deadline ->
                    return
                        failure
                            operation
                            AgentErrorCode.Timeout
                            "CI wait reached its overall deadline"
                            true
                            false
                            None
                            None
                            None
                        |> (fun envelope ->
                            match lastData with
                            | Some data -> ciFailureWithData data envelope
                            | None -> envelope)
                | Error envelope -> return envelope
                | Ok data when ciTerminal data.State -> return ciEnvelope operation data
                | Ok data ->
                    let signature =
                        data.State, (data.Runs |> List.map (fun run -> run.Id, run.Status, run.Conclusion))

                    let activity =
                        if previousSignature = Some signature then
                            lastActivity
                        else
                            clock.Elapsed

                    if clock.Elapsed - activity >= request.InactivityDeadline then
                        return
                            failure
                                operation
                                AgentErrorCode.Timeout
                                "CI wait reached its inactivity deadline"
                                true
                                false
                                None
                                None
                                None
                            |> ciFailureWithData data
                    else
                        let delay = Task.Delay(request.PollInterval, executionToken)
                        let! _ = Task.WhenAny [| delay |]

                        return!
                            ciPoll
                                operation
                                request
                                configuredRepo
                                configuredForge
                                cancellationToken
                                executionToken
                                clock
                                (pollCount + 1UL)
                                (Some signature)
                                activity
                                (Some data)
        }

    let private validateCiWaitDurations (request: CiWaitRequest) =
        let validate name value =
            if value <= TimeSpan.Zero then
                Error $"{name} must be positive"
            elif value > MaxWaitDuration then
                Error $"{name} must not exceed {MaxWaitDuration.TotalSeconds} seconds"
            else
                Ok()

        match validate "poll interval" request.PollInterval with
        | Error message -> Error message
        | Ok() ->
            match validate "deadline" request.Deadline with
            | Error message -> Error message
            | Ok() -> validate "inactivity deadline" request.InactivityDeadline

    let private ciWaitCore (repo: Repo) (forge: Forge) (request: CiWaitRequest) (cancellationToken: CancellationToken) =
        task {
            let operation = operationName AgentOperation.CiWait

            if request.OutputLimitBytes < MinimumOutputLimitBytes then
                return invalidInput operation $"output budget must be at least {MinimumOutputLimitBytes} bytes"
            elif cancellationToken.IsCancellationRequested then
                return cancellation operation
            else
                match
                    validateCiWaitDurations request,
                    validatePublicationIdentity
                        request.RepositoryPath
                        request.Branch
                        request.Remote
                        request.Revision
                        request.Account
                        request.Branch
                with
                | Error message, _
                | _, Error message -> return invalidInput operation message
                | Ok(), Ok() ->
                    use executionCts = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                    executionCts.CancelAfter request.Deadline
                    let executionToken = executionCts.Token
                    let clock = System.Diagnostics.Stopwatch.StartNew()

                    let configuredForge =
                        forge.WithAgentExecution(executionToken, Some request.OutputLimitBytes)

                    if configuredForge.Kind <> forgeKind request.Forge then
                        return invalidInput operation "selected forge handle does not match the request"
                    else
                        match!
                            ciPreflight
                                operation
                                request.OutputLimitBytes
                                repo
                                configuredForge
                                request.Forge
                                request.Account
                                request.Branch
                                request.Remote
                                request.Revision
                                executionToken
                        with
                        | Error _ when cancellationToken.IsCancellationRequested -> return cancellation operation
                        | Error _ when executionToken.IsCancellationRequested || clock.Elapsed >= request.Deadline ->
                            return
                                failure
                                    operation
                                    AgentErrorCode.Timeout
                                    "CI wait reached its overall deadline"
                                    true
                                    false
                                    None
                                    None
                                    None
                        | Error envelope -> return envelope
                        | Ok(configuredRepo, selectedForge) ->
                            return!
                                ciPoll
                                    operation
                                    request
                                    configuredRepo
                                    selectedForge
                                    cancellationToken
                                    executionToken
                                    clock
                                    1UL
                                    None
                                    TimeSpan.Zero
                                    None
        }

    /// Poll one exact-revision CI source until a terminal conclusion or a bounded stop.
    let ciWait (request: CiWaitRequest) (cancellationToken: CancellationToken) =
        task {
            let operation = operationName AgentOperation.CiWait

            if request.OutputLimitBytes < MinimumOutputLimitBytes then
                return invalidInput operation $"output budget must be at least {MinimumOutputLimitBytes} bytes"
            elif cancellationToken.IsCancellationRequested then
                return cancellation operation
            else
                match validateCiWaitDurations request with
                | Error message -> return invalidInput operation message
                | Ok() ->
                    match
                        validatePublicationIdentity
                            request.RepositoryPath
                            request.Branch
                            request.Remote
                            request.Revision
                            request.Account
                            request.Branch
                    with
                    | Error message -> return invalidInput operation message
                    | Ok() ->
                        match Repo.Open request.RepositoryPath with
                        | Error error -> return repoFailure operation request.OutputLimitBytes error
                        | Ok repo ->
                            return! ciWaitCore repo (forgeForKind repo.Root request.Forge) request cancellationToken
        }
        |> withBudget request.OutputLimitBytes

    let internal ciWaitWith repo forge request cancellationToken =
        ciWaitCore repo forge request cancellationToken
        |> withBudget request.OutputLimitBytes
