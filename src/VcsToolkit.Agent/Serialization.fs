namespace VcsToolkit.Agent

open System.IO
open System.Text
open System.Text.Json

module internal EnvelopeSerialization =
    let private statusName status =
        match status with
        | AgentStatus.Success -> "success"
        | AgentStatus.Error -> "error"

    let private writeOptionalInt (writer: Utf8JsonWriter) (name: string) (value: int option) =
        match value with
        | Some number -> writer.WriteNumber(name, number)
        | None -> writer.WriteNull name

    let private writeOptionalUInt64 (writer: Utf8JsonWriter) (name: string) (value: uint64 option) =
        match value with
        | Some number -> writer.WriteNumber(name, number)
        | None -> writer.WriteNull name

    let private writeOptionalString (writer: Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some text -> writer.WriteString(name, Redaction.redact text)
        | None -> writer.WriteNull name

    let private writeCapabilities (writer: Utf8JsonWriter) (capabilities: AgentCapability list) =
        writer.WriteStartArray("operations")

        for capability in capabilities do
            writer.WriteStartObject()
            writer.WriteString("name", ContractNames.operation capability.Operation)
            writer.WriteString("availability", if capability.Supported then "supported" else "planned")
            writer.WriteBoolean("mutating", capability.Mutating)
            writer.WriteEndObject()

        writer.WriteEndArray()

    let private writeProbe (writer: Utf8JsonWriter) (probe: ProbeData) =
        writer.WriteStartObject()
        writer.WriteString("kind", "probe")
        writer.WriteString("toolName", probe.ToolName)
        writer.WriteString("toolVersion", probe.ToolVersion)
        writeCapabilities writer probe.Operations
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

    let private forgeStatusName status =
        match status with
        | AgentForgeStatus.Absent -> "absent"
        | AgentForgeStatus.Unsupported -> "unsupported"
        | AgentForgeStatus.Available -> "available"
        | AgentForgeStatus.Unauthenticated -> "unauthenticated"

    let private writeInspect (writer: Utf8JsonWriter) (inspect: InspectData) =
        writer.WriteStartObject()
        writer.WriteString("kind", "inspect")
        writer.WriteString("root", Redaction.redact inspect.Root)
        writer.WriteString("backend", Redaction.redact inspect.Backend)
        writer.WriteStartObject("identity")
        writeOptionalString writer "revision" inspect.Identity.Revision
        writeOptionalString writer "branch" inspect.Identity.Branch
        writer.WriteEndObject()
        writer.WriteStartObject("workingState")
        writer.WriteBoolean("dirty", inspect.WorkingState.Dirty)
        writer.WriteNumber("changeCount", inspect.WorkingState.ChangeCount)
        writer.WriteBoolean("conflicted", inspect.WorkingState.Conflicted)
        writer.WriteString("operation", Redaction.redact inspect.WorkingState.Operation)

        match inspect.WorkingState.Tracking with
        | None -> writer.WriteNull "tracking"
        | Some tracking ->
            writer.WriteStartObject("tracking")
            writer.WriteString("branch", Redaction.redact tracking.Branch)
            writeOptionalUInt64 writer "ahead" tracking.Ahead
            writeOptionalUInt64 writer "behind" tracking.Behind
            writer.WriteEndObject()

        writer.WriteEndObject()
        writer.WriteStartArray("remotes")

        for remote in inspect.Remotes do
            writer.WriteStartObject()
            writer.WriteString("name", Redaction.redact remote.Name)
            writer.WriteString("url", Redaction.redact remote.Url)
            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteStartObject("forge")
        writer.WriteString("status", forgeStatusName inspect.Forge.Status)
        writeOptionalString writer "kind" inspect.Forge.Kind
        writer.WriteBoolean("authenticated", inspect.Forge.Authenticated)
        writeOptionalString writer "version" inspect.Forge.Version
        writer.WriteStartObject("capabilities")
        writer.WriteBoolean("pullRequestCreate", inspect.Forge.Capabilities.PullRequestCreate)
        writer.WriteBoolean("pullRequestComment", inspect.Forge.Capabilities.PullRequestComment)
        writer.WriteBoolean("pullRequestEdit", inspect.Forge.Capabilities.PullRequestEdit)
        writer.WriteBoolean("pullRequestChecks", inspect.Forge.Capabilities.PullRequestChecks)
        writer.WriteBoolean("pullRequestMerge", inspect.Forge.Capabilities.PullRequestMerge)
        writer.WriteBoolean("issueCreate", inspect.Forge.Capabilities.IssueCreate)
        writer.WriteBoolean("issueReopen", inspect.Forge.Capabilities.IssueReopen)
        writer.WriteBoolean("releaseDelete", inspect.Forge.Capabilities.ReleaseDelete)
        writer.WriteEndObject()
        writer.WriteEndObject()
        writeCapabilities writer inspect.Operations
        writer.WriteEndObject()

    let private writeChangedPath (writer: Utf8JsonWriter) (path: AgentChangedPath) =
        writer.WriteStartObject()
        writer.WriteString("path", Redaction.redact path.Path)
        writeOptionalString writer "oldPath" path.OldPath
        writer.WriteString("change", Redaction.redact path.Change)
        writer.WriteEndObject()

    let private writeSummary (writer: Utf8JsonWriter) (summary: AgentChangeSummary) =
        writer.WriteStartObject("summary")
        writer.WriteStartArray("paths")

        for path in summary.Paths do
            writeChangedPath writer path

        writer.WriteEndArray()
        writer.WriteStartObject("diffStat")
        writer.WriteNumber("filesChanged", summary.DiffStat.FilesChanged)
        writer.WriteNumber("insertions", summary.DiffStat.Insertions)
        writer.WriteNumber("deletions", summary.DiffStat.Deletions)
        writer.WriteEndObject()
        writer.WriteEndObject()

    let private writeFiles (writer: Utf8JsonWriter) (files: AgentFileDiff list) =
        writer.WriteStartArray("files")

        for file in files do
            writer.WriteStartObject()
            writer.WriteString("path", Redaction.redact file.Path)
            writeOptionalString writer "oldPath" file.OldPath
            writer.WriteString("change", Redaction.redact file.Change)
            writer.WriteStartArray("hunks")

            for hunk in file.Hunks do
                writer.WriteStartObject()
                writer.WriteNumber("oldStart", hunk.OldStart)
                writer.WriteNumber("oldLines", hunk.OldLines)
                writer.WriteNumber("newStart", hunk.NewStart)
                writer.WriteNumber("newLines", hunk.NewLines)
                writer.WriteString("section", Redaction.redact hunk.Section)
                writer.WriteStartArray("lines")

                for line in hunk.Lines do
                    writer.WriteStartObject()
                    writer.WriteString("kind", Redaction.redact line.Kind)
                    writer.WriteString("text", Redaction.redact line.Text)
                    writer.WriteEndObject()

                writer.WriteEndArray()
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteEndObject()

        writer.WriteEndArray()

    let private writeChanges (writer: Utf8JsonWriter) (changes: ChangesData) =
        writer.WriteStartObject()
        writer.WriteString("kind", "changes")

        match changes with
        | ChangesData.Summary summary ->
            writer.WriteString("mode", "summary")
            writeSummary writer summary
            writeFiles writer []
        | ChangesData.StructuredDiff files ->
            writer.WriteString("mode", "structured-diff")
            writer.WriteNull "summary"
            writeFiles writer files

        writer.WriteEndObject()

    let private writePayload (writer: Utf8JsonWriter) payload =
        match payload with
        | AgentPayload.Probe probe -> writeProbe writer probe
        | AgentPayload.Inspect inspect -> writeInspect writer inspect
        | AgentPayload.Changes changes -> writeChanges writer changes

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
            writer.WriteString("code", ContractNames.errorCode error.Code)
            writer.WriteString("message", Redaction.redact error.Message)
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
        | Some reason -> writer.WriteString("fallbackReason", ContractNames.fallbackReason reason)
        | None -> writer.WriteNull "fallbackReason"

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray()) + "\n"

    let byteCount envelope =
        envelope |> serialize |> Encoding.UTF8.GetByteCount
