namespace VcsToolkit.Agent

open System

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
            Supported = false
            Mutating = false }
          { Operation = AgentOperation.Changes
            Supported = false
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
