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

/// Operation-specific data carried by a v1 envelope.
[<RequireQualifiedAccess>]
type AgentPayload = Probe of ProbeData

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
