namespace VcsToolkit.Mcp

open System
open VcsToolkit.Core
open VcsToolkit.Forge

/// An error surfaced from a tool call. The `vcs-mcp` server binary routes the two cases onto
/// the two distinct MCP error channels so a client can tell them apart programmatically:
/// `InvalidParams` — the caller's call to fix (a bad or missing argument, an unknown tool, a
/// disabled write, an unsupported forge op) — is raised as a JSON-RPC **protocol** error
/// (`McpProtocolException` with `McpErrorCode.InvalidParams`); `Internal` — a backend/network
/// execution failure — is returned inside the tool result with `IsError = true` (the MCP
/// convention: execution errors travel in the result, protocol errors as JSON-RPC). This type
/// may gain cases — add a `| _ ->` arm if you match it, so a future case doesn't break your code.
[<RequireQualifiedAccess>]
type McpError =
    /// The caller's input/request was refused — raised as a JSON-RPC invalid-params protocol error.
    | InvalidParams of string
    /// A backend (git/jj/forge) or internal execution failure — returned as an `IsError` tool result.
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

    /// Map a `VcsToolkit.Core` error into an MCP error. A facade refusal of caller input
    /// (e.g. `CommitPaths` with an empty path set) is invalid-params; actual filesystem and
    /// backend failures are internal errors.
    let coreErr (e: RepoError) : McpError =
        match e with
        | RepoError.InvalidInput _ -> McpError.InvalidParams e.Message
        | RepoError.Unsupported _ -> McpError.InvalidParams e.Message
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
