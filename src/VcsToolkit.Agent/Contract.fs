namespace VcsToolkit.Agent

/// Stable v1 operation taxonomy. Availability is reported by `probe`; declaring an operation
/// here does not imply that the current tool version implements it.
[<RequireQualifiedAccess>]
type AgentOperation =
    | Probe
    | Inspect
    | Changes
    | Commit
    | Publish
    | CiStatus
    | CiWait

/// Stable machine-readable error taxonomy for contract v1.
[<RequireQualifiedAccess>]
type AgentErrorCode =
    | Unsupported
    | Denied
    | InvalidInput
    | Backend
    | Forge
    | Authentication
    | Timeout
    | Cancellation
    | OutputLimit
    | ExternalCommand
    | RevisionMismatch

/// Terminal outcome classification carried by every envelope.
[<RequireQualifiedAccess>]
type AgentStatus =
    | Success
    | Error

/// Why a caller may need a visible fallback outside the preferred agent interface.
[<RequireQualifiedAccess>]
type AgentFallbackReason =
    | OperationNotImplemented
    | MissingExecutable
    | UnsupportedBackend
    | UnsupportedForge
    | RawDiagnosticRequired

module internal AgentContractFacts =
    let successExitCode = 0

    let nonTerminalExitCode = 10

    let errorCodes =
        [ AgentErrorCode.Unsupported
          AgentErrorCode.Denied
          AgentErrorCode.InvalidInput
          AgentErrorCode.Backend
          AgentErrorCode.Forge
          AgentErrorCode.Authentication
          AgentErrorCode.Timeout
          AgentErrorCode.Cancellation
          AgentErrorCode.OutputLimit
          AgentErrorCode.ExternalCommand
          AgentErrorCode.RevisionMismatch ]

    let errorExit code =
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

    let fallbackReasons =
        [ AgentFallbackReason.OperationNotImplemented
          AgentFallbackReason.MissingExecutable
          AgentFallbackReason.UnsupportedBackend
          AgentFallbackReason.UnsupportedForge
          AgentFallbackReason.RawDiagnosticRequired ]

    let cliOptions operation =
        match operation with
        | AgentOperation.Probe -> [ "--output-budget" ]
        | AgentOperation.Inspect -> [ "--repo"; "--output-budget" ]
        | AgentOperation.Changes -> [ "--repo"; "--view"; "--output-budget" ]
        | AgentOperation.Commit -> [ "--repo"; "--path"; "--message"; "--output-budget" ]
        | AgentOperation.Publish ->
            [ "--repo"
              "--branch"
              "--remote"
              "--revision"
              "--forge"
              "--account"
              "--target"
              "--title"
              "--body"
              "--output-budget" ]
        | AgentOperation.CiStatus ->
            [ "--repo"
              "--branch"
              "--remote"
              "--revision"
              "--forge"
              "--account"
              "--output-budget" ]
        | AgentOperation.CiWait ->
            [ "--repo"
              "--branch"
              "--remote"
              "--revision"
              "--forge"
              "--account"
              "--poll-seconds"
              "--deadline-seconds"
              "--inactivity-seconds"
              "--output-budget" ]

/// One operation advertised by `probe`.
type AgentCapability =
    {
        Operation: AgentOperation
        Supported: bool
        Mutating: bool
        /// Repository backends on which this operation can run.
        Backends: string list
        /// Forge kinds on which this operation can run; empty when it is forge-independent.
        Forges: string list
    }

/// Compatibility declaration for optional ProcessKit-CLI supervision.
type SupervisorCompatibility =
    { Mode: string
      LifecycleProtocol: string
      Required: bool }

/// Deterministic data returned by the read-only `probe` operation.
type ProbeData =
    { ToolName: string
      ToolVersion: string
      Operations: AgentCapability list
      Backends: string list
      Forges: string list
      Supervisor: SupervisorCompatibility }

/// Request for the bounded repository-inspection outcome.
type InspectRequest =
    { RepositoryPath: string
      OutputLimitBytes: int }

    static member Create(repositoryPath: string) =
        { RepositoryPath = repositoryPath
          OutputLimitBytes = 65_536 }

    member this.WithOutputLimit(outputLimitBytes: int) =
        { this with
            OutputLimitBytes = outputLimitBytes }

/// Which read-only change representation a caller selected.
[<RequireQualifiedAccess>]
type ChangesMode =
    | Summary
    | StructuredDiff

/// Request for a bounded change outcome.
type ChangesRequest =
    { RepositoryPath: string
      Mode: ChangesMode
      OutputLimitBytes: int }

    static member Summary(repositoryPath: string) =
        { RepositoryPath = repositoryPath
          Mode = ChangesMode.Summary
          OutputLimitBytes = 65_536 }

    static member StructuredDiff(repositoryPath: string) =
        { RepositoryPath = repositoryPath
          Mode = ChangesMode.StructuredDiff
          OutputLimitBytes = 65_536 }

    member this.WithOutputLimit(outputLimitBytes: int) =
        { this with
            OutputLimitBytes = outputLimitBytes }

/// Request for an exact-path repository commit. Paths use the repository-root-relative,
/// forward-slash form returned by `changes`; an empty path set is always refused.
type CommitRequest =
    { RepositoryPath: string
      Paths: string list
      Message: string
      OutputLimitBytes: int }

    static member Create(repositoryPath: string, paths: string list, message: string) =
        { RepositoryPath = repositoryPath
          Paths = paths
          Message = message
          OutputLimitBytes = 65_536 }

    member this.WithOutputLimit(outputLimitBytes: int) =
        { this with
            OutputLimitBytes = outputLimitBytes }

/// Current revision identity reported by `inspect`.
type AgentRevisionIdentity =
    { Revision: string option
      Branch: string option }

/// Upstream tracking facts for the current branch.
type AgentTracking =
    { Branch: string
      Ahead: uint64 option
      Behind: uint64 option }

/// Current working-copy state reported by `inspect`.
type AgentWorkingState =
    { Dirty: bool
      ChangeCount: uint64
      Conflicted: bool
      Operation: string
      Tracking: AgentTracking option }

/// One redacted configured remote.
type AgentRemote = { Name: string; Url: string }

/// Forge discovery state for the repository.
[<RequireQualifiedAccess>]
type AgentForgeStatus =
    | Absent
    | Unsupported
    | Available
    | Unauthenticated

/// Stable capability subset needed by outcome workflows.
type AgentForgeCapabilities =
    {
        /// Whether the forge can prove auth and an explicitly selected repository identity.
        RepositoryIdentity: bool
        PullRequestCreate: bool
        PullRequestComment: bool
        PullRequestEdit: bool
        PullRequestChecks: bool
        ExactRevisionCi: bool
        PullRequestMerge: bool
        IssueCreate: bool
        IssueReopen: bool
        ReleaseDelete: bool
    }

/// Detected forge, authentication and capabilities.
type AgentForgeInfo =
    { Status: AgentForgeStatus
      Kind: string option
      Authenticated: bool
      Version: string option
      Capabilities: AgentForgeCapabilities }

/// Repository facts returned by `inspect` in one result.
type InspectData =
    { Root: string
      Backend: string
      Identity: AgentRevisionIdentity
      WorkingState: AgentWorkingState
      Remotes: AgentRemote list
      Forge: AgentForgeInfo
      Operations: AgentCapability list }

/// One changed path in a compact summary.
type AgentChangedPath =
    { Path: string
      OldPath: string option
      Change: string }

/// Aggregate for the backend diff scope. On Git this covers tracked changes only; changed
/// paths are reported separately because untracked paths are not part of `git diff`.
type AgentDiffStat =
    { FilesChanged: uint64
      Insertions: uint64
      Deletions: uint64 }

/// Compact path list and explicitly scoped diff aggregate selected by `changes summary`.
type AgentChangeSummary =
    { Paths: AgentChangedPath list
      DiffStat: AgentDiffStat }

/// One typed line in a structured diff hunk.
type AgentDiffLine = { Kind: string; Text: string }

/// One structured diff hunk.
type AgentDiffHunk =
    { OldStart: uint64
      OldLines: uint64
      NewStart: uint64
      NewLines: uint64
      Section: string
      Lines: AgentDiffLine list }

/// One structured file diff. Raw duplicate text is intentionally omitted.
type AgentFileDiff =
    { Path: string
      OldPath: string option
      Change: string
      Hunks: AgentDiffHunk list }

/// Selected read-only change representation. The union makes summary and structured-diff
/// payloads mutually exclusive for every public producer.
[<RequireQualifiedAccess>]
type ChangesData =
    | Summary of AgentChangeSummary
    | StructuredDiff of AgentFileDiff list

/// Whether the backend mutation and its postflight evidence establish one exact revision.
[<RequireQualifiedAccess>]
type CommitCompletion =
    /// The created revision and its changed-path set were independently observed.
    | Verified
    /// A backend call could have mutated the repository, but postflight could not prove its outcome.
    | Ambiguous

/// Bounded evidence returned for both a verified commit and an ambiguous late failure.
/// `ObservedCreatedRevision` is populated only after the backend's relevant revision identity
/// changed. `CreatedRevision` is stronger: it identifies the direct Git child of `SourceRevision`
/// (or the sole root in an unborn repository), or the created Jujutsu revision, after `Paths` were
/// read from that revision and matched `BackendPaths`.
type CommitData =
    { Root: string
      Backend: string
      SourceRevision: string option
      SourceBranch: string option
      RequestedPaths: string list
      BackendPaths: string list
      ObservedRevision: string option
      ObservedBranch: string option
      ObservedCreatedRevision: string option
      CreatedRevision: string option
      Paths: string list
      SelectedPathsRemaining: bool option
      UnrelatedPathsPreserved: bool option
      Completion: CommitCompletion }

/// Explicit forge selection for publication and CI. The value is never inferred from another
/// remote when a mutating operation is requested.
[<RequireQualifiedAccess>]
type AgentForgeKind =
    | GitHub
    | GitLab
    | Gitea

/// Request for verified publication of one local revision to one named remote branch/bookmark.
/// The authenticated forge account and PR/MR target are explicit identity inputs.
type PublishRequest =
    { RepositoryPath: string
      Branch: string
      Remote: string
      Revision: string
      Forge: AgentForgeKind
      Account: string
      TargetBranch: string
      Title: string
      Body: string
      OutputLimitBytes: int }

    static member Create
        (
            repositoryPath: string,
            branch: string,
            remote: string,
            revision: string,
            forge: AgentForgeKind,
            account: string,
            targetBranch: string,
            title: string,
            body: string
        ) =
        { RepositoryPath = repositoryPath
          Branch = branch
          Remote = remote
          Revision = revision
          Forge = forge
          Account = account
          TargetBranch = targetBranch
          Title = title
          Body = body
          OutputLimitBytes = 65_536 }

    member this.WithOutputLimit(outputLimitBytes: int) =
        { this with
            OutputLimitBytes = outputLimitBytes }

/// Evidence captured before the first remote mutation and after recovery/verification.
type PublicationEvidence =
    { Root: string
      Backend: string
      Forge: string
      Account: string
      Branch: string
      Remote: string
      LocalRevision: string
      RemoteRevision: string option }

/// Whether the matching PR/MR was opened by this call or recovered from existing forge state.
[<RequireQualifiedAccess>]
type PublicationChangeRequestDisposition =
    | Created
    | Existing

/// One verified PR/MR bound to the published source branch and requested target branch.
type PublicationChangeRequest =
    { Number: uint64
      Url: string
      SourceBranch: string
      TargetBranch: string
      Disposition: PublicationChangeRequestDisposition }

/// Whether all publication postconditions were proven or a late step may have completed.
[<RequireQualifiedAccess>]
type PublishCompletion =
    | Verified
    | Ambiguous

/// Bounded publication evidence. Error envelopes may carry `Ambiguous` data after a remote
/// mutation so a caller can safely retry and recover the already-pushed/already-open result.
type PublishData =
    { Preflight: PublicationEvidence
      Postflight: PublicationEvidence option
      ChangeRequest: PublicationChangeRequest option
      Completion: PublishCompletion }

/// Request for one exact-revision CI observation.
type CiStatusRequest =
    { RepositoryPath: string
      Forge: AgentForgeKind
      Account: string
      Branch: string
      Remote: string
      Revision: string
      OutputLimitBytes: int }

    static member Create
        (
            repositoryPath: string,
            forge: AgentForgeKind,
            account: string,
            branch: string,
            remote: string,
            revision: string
        ) =
        { RepositoryPath = repositoryPath
          Forge = forge
          Account = account
          Branch = branch
          Remote = remote
          Revision = revision
          OutputLimitBytes = 65_536 }

    member this.WithOutputLimit(outputLimitBytes: int) =
        { this with
            OutputLimitBytes = outputLimitBytes }

/// Request for polling the exact-revision CI source to a terminal conclusion.
type CiWaitRequest =
    { RepositoryPath: string
      Forge: AgentForgeKind
      Account: string
      Branch: string
      Remote: string
      Revision: string
      PollInterval: System.TimeSpan
      Deadline: System.TimeSpan
      InactivityDeadline: System.TimeSpan
      OutputLimitBytes: int }

    static member Create
        (
            repositoryPath: string,
            forge: AgentForgeKind,
            account: string,
            branch: string,
            remote: string,
            revision: string
        ) =
        { RepositoryPath = repositoryPath
          Forge = forge
          Account = account
          Branch = branch
          Remote = remote
          Revision = revision
          PollInterval = System.TimeSpan.FromSeconds 5.0
          Deadline = System.TimeSpan.FromMinutes 30.0
          InactivityDeadline = System.TimeSpan.FromMinutes 10.0
          OutputLimitBytes = 65_536 }

    member this.WithPolling(pollInterval: System.TimeSpan) =
        { this with
            PollInterval = pollInterval }

    member this.WithDeadline(deadline: System.TimeSpan) = { this with Deadline = deadline }

    member this.WithInactivityDeadline(inactivityDeadline: System.TimeSpan) =
        { this with
            InactivityDeadline = inactivityDeadline }

    member this.WithOutputLimit(outputLimitBytes: int) =
        { this with
            OutputLimitBytes = outputLimitBytes }

/// Exact-revision CI state. Only `Success`, `Failure`, `Cancelled`, and `Skipped` are terminal.
[<RequireQualifiedAccess>]
type AgentCiState =
    | NoRuns
    | Pending
    | Success
    | Failure
    | Cancelled
    | Skipped
    | RevisionMismatch

/// One bounded forge CI run/pipeline observation.
type AgentCiRun =
    { Id: string
      Name: string
      Status: string
      Conclusion: string option
      Revision: string
      Url: string }

/// Exact-revision CI evidence returned by both status and wait.
type CiData =
    { Root: string
      Forge: string
      Account: string
      Branch: string
      Remote: string
      Revision: string
      State: AgentCiState
      Runs: AgentCiRun list
      PollCount: uint64 }

/// Operation-specific data carried by a v1 envelope.
[<RequireQualifiedAccess>]
type AgentPayload =
    | Probe of ProbeData
    | Inspect of InspectData
    | Changes of ChangesData
    | Commit of CommitData
    | Publish of PublishData
    | Ci of CiData

/// Structured failure details. `LimitBytes` and `RequiredBytes` are populated only for
/// `OutputLimit`; `Truncated` makes refusal of oversized content explicit.
type AgentError =
    { Code: AgentErrorCode
      Message: string
      Retryable: bool
      Truncated: bool
      LimitBytes: int option
      RequiredBytes: int option }

/// A bounded diagnostic that does not change the operation's status.
type AgentWarning = { Code: string; Message: string }

/// The transport-neutral v1 outcome envelope.
type AgentEnvelope =
    { ContractVersion: string
      Operation: string
      Status: AgentStatus
      Terminal: bool
      Data: AgentPayload option
      Error: AgentError option
      Warnings: AgentWarning list
      FallbackReason: AgentFallbackReason option }

/// Fully rendered process boundary result. Machine output is always on `Stdout`; `Stderr`
/// contains only a bounded diagnostic label.
type AgentExecution =
    { ExitCode: int
      Stdout: string
      Stderr: string }

module internal ContractNames =
    let operation operation =
        match operation with
        | AgentOperation.Probe -> "probe"
        | AgentOperation.Inspect -> "inspect"
        | AgentOperation.Changes -> "changes"
        | AgentOperation.Commit -> "commit"
        | AgentOperation.Publish -> "publish"
        | AgentOperation.CiStatus -> "ci.status"
        | AgentOperation.CiWait -> "ci.wait"

    let errorCode code =
        match code with
        | AgentErrorCode.Unsupported -> "unsupported"
        | AgentErrorCode.Denied -> "denied"
        | AgentErrorCode.InvalidInput -> "invalid-input"
        | AgentErrorCode.Backend -> "backend"
        | AgentErrorCode.Forge -> "forge"
        | AgentErrorCode.Authentication -> "authentication"
        | AgentErrorCode.Timeout -> "timeout"
        | AgentErrorCode.Cancellation -> "cancellation"
        | AgentErrorCode.RevisionMismatch -> "revision-mismatch"
        | AgentErrorCode.OutputLimit -> "output-limit"
        | AgentErrorCode.ExternalCommand -> "external-command"

    let fallbackReason reason =
        match reason with
        | AgentFallbackReason.OperationNotImplemented -> "operation-not-implemented"
        | AgentFallbackReason.MissingExecutable -> "missing-executable"
        | AgentFallbackReason.UnsupportedBackend -> "unsupported-backend"
        | AgentFallbackReason.UnsupportedForge -> "unsupported-forge"
        | AgentFallbackReason.RawDiagnosticRequired -> "raw-diagnostic-required"
