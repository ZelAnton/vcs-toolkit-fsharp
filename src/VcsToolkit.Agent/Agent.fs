namespace VcsToolkit.Agent

open System
open System.Threading
open ProcessKit
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

    let internal operationName operation =
        match operation with
        | AgentOperation.Probe -> "probe"
        | AgentOperation.Inspect -> "inspect"
        | AgentOperation.Changes -> "changes"
        | AgentOperation.Commit -> "commit"
        | AgentOperation.Publish -> "publish"
        | AgentOperation.CiStatus -> "ci.status"
        | AgentOperation.CiWait -> "ci.wait"

    let internal errorCodeName code =
        match code with
        | AgentErrorCode.Unsupported -> "unsupported"
        | AgentErrorCode.Denied -> "denied"
        | AgentErrorCode.InvalidInput -> "invalid-input"
        | AgentErrorCode.Backend -> "backend"
        | AgentErrorCode.Forge -> "forge"
        | AgentErrorCode.Authentication -> "authentication"
        | AgentErrorCode.Timeout -> "timeout"
        | AgentErrorCode.Cancellation -> "cancellation"
        | AgentErrorCode.OutputLimit -> "output-limit"
        | AgentErrorCode.ExternalCommand -> "external-command"

    let internal fallbackReasonName reason =
        match reason with
        | AgentFallbackReason.OperationNotImplemented -> "operation-not-implemented"
        | AgentFallbackReason.MissingExecutable -> "missing-executable"
        | AgentFallbackReason.UnsupportedBackend -> "unsupported-backend"
        | AgentFallbackReason.UnsupportedForge -> "unsupported-forge"
        | AgentFallbackReason.RawDiagnosticRequired -> "raw-diagnostic-required"

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

    let private capabilities =
        [ { Operation = AgentOperation.Probe
            Supported = true
            Mutating = false }
          { Operation = AgentOperation.Inspect
            Supported = true
            Mutating = false }
          { Operation = AgentOperation.Changes
            Supported = true
            Mutating = false }
          { Operation = AgentOperation.Commit
            Supported = false
            Mutating = true }
          { Operation = AgentOperation.Publish
            Supported = false
            Mutating = true }
          { Operation = AgentOperation.CiStatus
            Supported = false
            Mutating = false }
          { Operation = AgentOperation.CiWait
            Supported = false
            Mutating = false } ]

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
        { PullRequestCreate = false
          PullRequestComment = false
          PullRequestEdit = false
          PullRequestChecks = false
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
                                { PullRequestCreate = capabilities.PrCreate
                                  PullRequestComment = capabilities.PrComment
                                  PullRequestEdit = capabilities.PrEdit
                                  PullRequestChecks = capabilities.PrChecks
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

    let private inspectCore
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

    /// Inspect a repository through the typed Core and Forge facades.
    let inspect (request: InspectRequest) (cancellationToken: CancellationToken) =
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
                    return! inspectCore repo (forgeForRemotes repo.Root) cancellationToken request.OutputLimitBytes
        }

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

    let private changesCore
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
                                { FilesChanged = stat.FilesChanged
                                  Insertions = stat.Insertions
                                  Deletions = stat.Deletions
                                  Paths = paths |> List.map changedPath }

                            return
                                success
                                    operation
                                    (AgentPayload.Changes
                                        { Mode = ChangesMode.Summary
                                          Summary = Some summary
                                          Files = [] })
                | ChangesMode.StructuredDiff ->
                    match! configured.Diff() with
                    | Error error -> return repoFailure operation outputLimitBytes error
                    | Ok files ->
                        return
                            success
                                operation
                                (AgentPayload.Changes
                                    { Mode = ChangesMode.StructuredDiff
                                      Summary = None
                                      Files = files |> List.map fileDiff })
        }

    /// Return either a compact summary or a bounded structured working-copy diff.
    let changes (request: ChangesRequest) (cancellationToken: CancellationToken) =
        task {
            let operation = operationName AgentOperation.Changes

            if request.OutputLimitBytes < MinimumOutputLimitBytes then
                return invalidInput operation $"output budget must be at least {MinimumOutputLimitBytes} bytes"
            elif cancellationToken.IsCancellationRequested then
                return cancellation operation
            else
                match Repo.Open request.RepositoryPath with
                | Error error -> return repoFailure operation request.OutputLimitBytes error
                | Ok repo -> return! changesCore repo request.Mode cancellationToken request.OutputLimitBytes
        }

    let internal changesWith (repo: Repo) mode (cancellationToken: CancellationToken) outputLimitBytes =
        changesCore repo mode cancellationToken outputLimitBytes
