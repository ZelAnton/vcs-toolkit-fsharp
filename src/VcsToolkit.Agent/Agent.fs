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
            Supported = true
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
