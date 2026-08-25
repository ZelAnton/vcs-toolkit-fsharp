namespace VcsToolkit.Agent

open System
open System.IO
open System.Text
open System.Text.Json

/// Deterministic JSON rendering and stdout-budget enforcement for contract v1.
[<RequireQualifiedAccess>]
module AgentWire =
    let private statusName status =
        match status with
        | AgentStatus.Success -> "success"
        | AgentStatus.Error -> "error"

    let private writeOptionalInt (writer: Utf8JsonWriter) (name: string) (value: int option) =
        match value with
        | Some number -> writer.WriteNumber(name, number)
        | None -> writer.WriteNull name

    let private writeProbe (writer: Utf8JsonWriter) (probe: ProbeData) =
        writer.WriteStartObject()
        writer.WriteString("kind", "probe")
        writer.WriteString("toolName", probe.ToolName)
        writer.WriteString("toolVersion", probe.ToolVersion)
        writer.WriteStartArray("operations")

        for capability in probe.Operations do
            writer.WriteStartObject()
            writer.WriteString("name", Agent.operationName capability.Operation)
            writer.WriteString("availability", if capability.Supported then "supported" else "planned")
            writer.WriteBoolean("mutating", capability.Mutating)
            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteStartArray("backends")

        for backend in probe.Backends do
            writer.WriteStringValue backend

        writer.WriteEndArray()
        writer.WriteStartArray("forges")

        for forge in probe.Forges do
            writer.WriteStringValue forge

        writer.WriteEndArray()
        writer.WriteStartObject("supervisor")
        writer.WriteString("mode", probe.Supervisor.Mode)
        writer.WriteString("lifecycleProtocol", probe.Supervisor.LifecycleProtocol)
        writer.WriteBoolean("required", probe.Supervisor.Required)
        writer.WriteEndObject()
        writer.WriteEndObject()

    let private writePayload (writer: Utf8JsonWriter) payload =
        match payload with
        | AgentPayload.Probe probe -> writeProbe writer probe

    /// Serialize one envelope with stable property ordering and LF termination.
    let serialize envelope =
        use stream = new MemoryStream()

        use writer =
            new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false, SkipValidation = false))

        writer.WriteStartObject()
        writer.WriteString("contractVersion", envelope.ContractVersion)
        writer.WriteString("operation", envelope.Operation)
        writer.WriteString("status", statusName envelope.Status)
        writer.WriteBoolean("terminal", envelope.Terminal)

        match envelope.Data with
        | Some payload ->
            writer.WritePropertyName "data"
            writePayload writer payload
        | None -> writer.WriteNull "data"

        match envelope.Error with
        | Some error ->
            writer.WriteStartObject("error")
            writer.WriteString("code", Agent.errorCodeName error.Code)
            writer.WriteString("message", error.Message)
            writer.WriteBoolean("retryable", error.Retryable)
            writer.WriteBoolean("truncated", error.Truncated)
            writeOptionalInt writer "limitBytes" error.LimitBytes
            writeOptionalInt writer "requiredBytes" error.RequiredBytes
            writer.WriteEndObject()
        | None -> writer.WriteNull "error"

        writer.WriteStartArray("warnings")

        for warning in envelope.Warnings do
            writer.WriteStartObject()
            writer.WriteString("code", warning.Code)
            writer.WriteString("message", Redaction.redact warning.Message)
            writer.WriteEndObject()

        writer.WriteEndArray()

        match envelope.FallbackReason with
        | Some reason -> writer.WriteString("fallbackReason", Agent.fallbackReasonName reason)
        | None -> writer.WriteNull "fallbackReason"

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray()) + "\n"

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
