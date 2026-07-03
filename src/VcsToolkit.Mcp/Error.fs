namespace VcsToolkit.Mcp

open System
open VcsToolkit.Core
open VcsToolkit.Forge

/// An error surfaced from a tool call — mapped onto MCP's JSON-RPC error codes by the
/// server binary: `InvalidParams` is the client's call to fix (a bad argument, a disabled
/// write, an unsupported forge op), `Internal` is a backend/network failure.
[<RequireQualifiedAccess>]
type McpError =
    /// The caller's input/request was refused (invalid-params on the wire).
    | InvalidParams of string
    /// A backend (git/jj/forge) or internal failure (internal-error on the wire).
    | Internal of string

    /// The human-readable message.
    member this.Message =
        match this with
        | McpError.InvalidParams m -> m
        | McpError.Internal m -> m

// (Whether this is an invalid-params error is the compiler-generated `IsInvalidParams`
// case tester — no custom member needed.)

/// Error mapping and argv guards shared by the tools.
[<AutoOpen>]
module internal ErrorMapping =

    /// Map a `VcsToolkit.Core` error into an MCP error. The facade reports a refused *input*
    /// (e.g. `CommitPaths` with an empty path set) as a `RepoError.Io` with a descriptive
    /// message — that's the client's call to fix, so surface it as invalid-params rather than
    /// internal. (The F# `RepoError.Io` has no `io.kind()` to inspect, so all `Io` map to
    /// invalid-params; genuine filesystem failures during detection are rare here.)
    let coreErr (e: RepoError) : McpError =
        match e with
        | RepoError.Io _ -> McpError.InvalidParams e.Message
        | _ -> McpError.Internal e.Message

    /// Map a `VcsToolkit.Forge` error into an MCP error — an `Unsupported` op or an
    /// `InvalidInput` (the facade's pre-spawn refusal path) is a client-facing invalid
    /// request; a forge/network failure is internal.
    let forgeErr (e: ForgeError) : McpError =
        match e with
        | ForgeError.InvalidInput _ -> McpError.InvalidParams e.Message
        | _ when e.IsUnsupported -> McpError.InvalidParams e.Message
        | _ -> McpError.Internal e.Message

    /// Belt-and-braces argv guard for a mutating tool's `body`/`title` field: a value whose
    /// first character is `-` would be parsed by a CLI as a flag. A uniform second line of
    /// defence at the MCP seam (the wrappers already guard per backend). **An empty string is
    /// a real value** (clears the field) and passes.
    let guardArgvField (what: string) (value: string) : Result<unit, McpError> =
        if value.StartsWith("-", StringComparison.Ordinal) then
            Error(McpError.InvalidParams(sprintf "%s %A would be parsed as a flag — refusing to pass it" what value))
        else
            Ok()
