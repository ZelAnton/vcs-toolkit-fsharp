namespace VcsToolkit.Mcp

open System.Text.Json
open System.Text.Json.Serialization

/// JSON serialization for tool results — F#-aware (clean options/unions/records) so a DTO
/// renders as idiomatic JSON for the agent, not `{ "Case": … }` noise.
[<RequireQualifiedAccess>]
module Json =

    /// The shared serializer options: F# support (options → value/null, fieldless unions →
    /// their bare tag string, e.g. `OperationState.Clear` → `"Clear"`, `CiStatus.Passing` →
    /// `"Passing"`), camelCase property names, and indented output (a readable result).
    let options =
        let fsharp =
            JsonFSharpOptions.Default().WithUnionUnwrapFieldlessTags().WithUnwrapOption().ToJsonSerializerOptions()

        fsharp.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        fsharp.WriteIndented <- true
        fsharp.DefaultIgnoreCondition <- JsonIgnoreCondition.Never
        fsharp

    /// Serialize `value` to a pretty JSON string — the text body of a tool result.
    let ok (value: 'T) : string =
        JsonSerializer.Serialize(value, options)
