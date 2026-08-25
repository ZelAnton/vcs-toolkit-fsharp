namespace VcsToolkit.Agent

open System.Text

/// Deterministic JSON rendering and stdout-budget enforcement for contract v1.
[<RequireQualifiedAccess>]
module AgentWire =
    /// Serialize one envelope with stable property ordering and LF termination. Error and
    /// warning messages are redacted at this boundary, including caller-constructed envelopes.
    let serialize envelope =
        EnvelopeSerialization.serialize envelope

    let private execution envelope stdout =
        match envelope.Error with
        | None ->
            { ExitCode = 0
              Stdout = stdout
              Stderr = "" }
        | Some error ->
            { ExitCode = Agent.exitCode error.Code
              Stdout = stdout
              Stderr = $"vcs-agent: {Agent.errorCodeName error.Code}\n" }

    /// Render an envelope within `outputLimitBytes`. Oversized data is replaced by an explicit
    /// `output-limit` envelope whose `requiredBytes` reports the unbounded result size.
    let render outputLimitBytes envelope =
        let envelope =
            if outputLimitBytes < Agent.MinimumOutputLimitBytes then
                Agent.invalidInput
                    envelope.Operation
                    $"output budget must be at least {Agent.MinimumOutputLimitBytes} bytes"
            else
                envelope

        let rendered = serialize envelope
        let requiredBytes = Encoding.UTF8.GetByteCount rendered

        if outputLimitBytes < Agent.MinimumOutputLimitBytes then
            execution envelope rendered
        elif requiredBytes <= outputLimitBytes then
            execution envelope rendered
        else
            let limited = Agent.outputLimit envelope.Operation outputLimitBytes requiredBytes
            let limitedOutput = serialize limited

            if Encoding.UTF8.GetByteCount limitedOutput > outputLimitBytes then
                invalidOp "The minimum output budget cannot contain the v1 output-limit envelope."

            execution limited limitedOutput
