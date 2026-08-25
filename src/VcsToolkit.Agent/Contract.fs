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

/// One operation advertised by `probe`.
type AgentCapability =
    { Operation: AgentOperation
      Supported: bool
      Mutating: bool }

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
    { PullRequestCreate: bool
      PullRequestComment: bool
      PullRequestEdit: bool
      PullRequestChecks: bool
      PullRequestMerge: bool
      IssueCreate: bool
      IssueReopen: bool
      ReleaseDelete: bool }

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

/// Compact aggregate selected by `changes summary`.
type AgentChangeSummary =
    { FilesChanged: uint64
      Insertions: uint64
      Deletions: uint64
      Paths: AgentChangedPath list }

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

/// Selected read-only change representation.
type ChangesData =
    { Mode: ChangesMode
      Summary: AgentChangeSummary option
      Files: AgentFileDiff list }

/// Operation-specific data carried by a v1 envelope.
[<RequireQualifiedAccess>]
type AgentPayload =
    | Probe of ProbeData
    | Inspect of InspectData
    | Changes of ChangesData

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
