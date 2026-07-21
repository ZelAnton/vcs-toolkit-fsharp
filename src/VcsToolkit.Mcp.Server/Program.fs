module Main

// The `vcs-mcp` binary: an MCP server over stdio. An agent harness launches it with a
// `mcpServers` config entry; it speaks JSON-RPC on stdin/stdout. Read tools are always
// available; `--allow-write` enables every mutating tool, `--allow-tools` a named subset.
// The forge is auto-detected from the repo's `origin` remote unless `--forge` overrides it.
// The git client is hardened (repo hooks and config disabled) so serving a repository you
// didn't create can't execute its hooks, and every command carries a `--timeout`.

open System
open System.IO
open System.Reflection
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open ModelContextProtocol
open ModelContextProtocol.Server
open ModelContextProtocol.Protocol
open VcsToolkit.CliSupport
open VcsToolkit.Core
open VcsToolkit.Git
open VcsToolkit.Jj
open VcsToolkit.Forge
open VcsToolkit.Mcp

/// A hardened git client carrying the optional per-command timeout and diagnostic observer.
let private hardenedGit (timeout: TimeSpan option) (observer: ICommandObserver option) : Git =
    let g =
        match timeout with
        | Some t -> Git.Hardened().DefaultTimeout t
        | None -> Git.Hardened()

    match observer with
    | Some obs -> g.WithObserver obs
    | None -> g

/// A default jj client carrying the optional per-command timeout and diagnostic observer. jj
/// has no repo-config/hooks to harden the way git's `Hardened` does, so only those two apply.
let private timeoutJj (timeout: TimeSpan option) (observer: ICommandObserver option) : Jj =
    let j =
        match timeout with
        | Some t -> Jj.Create().DefaultTimeout t
        | None -> Jj.Create()

    match observer with
    | Some obs -> j.WithObserver obs
    | None -> j

/// Open the repo at `dir` with a hardened, timeout-bound, optionally-observed client.
/// Delegates detection, path absolutisation, and error mapping to the Core facade
/// (`Repo.OpenWith`); the binary only injects the hardened/timeout/observer client
/// configuration and flattens `RepoError` to its message. The factories are lazy, so only the
/// detected backend's client is built.
let private openRepo
    (dir: string)
    (timeout: TimeSpan option)
    (observer: ICommandObserver option)
    : Result<Repo, string> =
    Repo.OpenWith(dir, (fun () -> hardenedGit timeout observer), (fun () -> timeoutJj timeout observer))
    |> Result.mapError (fun (e: RepoError) -> e.Message)

/// Parse `jj git remote list` output (one `<name> <url>` line per configured remote,
/// space-separated — verified against jj 0.42) and return the URL configured for
/// `remote`, if listed. `None` when `remote` isn't in the list (or the output is empty).
let internal parseJjRemoteUrl (remote: string) (output: string) : string option =
    output.Split('\n')
    |> Array.tryPick (fun rawLine ->
        let line = rawLine.Trim()

        match line.IndexOf(' ') with
        | -1 -> None
        | idx ->
            let name = line.Substring(0, idx)

            if name = remote then
                Some(line.Substring(idx + 1).Trim())
            else
                None)

/// Best-effort: read the `origin` remote URL and classify its host.
///
/// Git-backed repos ask git directly (`remote get-url`). A jj-backed repo without git
/// colocation has no `.git` at its root for that to find, so it falls back to `jj git
/// remote list` (the `Jj.Run` escape hatch — there is no typed wrapper for this jj
/// subcommand) and parses the `<name> <url>` line for `origin` out of the raw text.
let internal detectForgeKind (repo: Repo) : Task<ForgeKind option> =
    task {
        match repo.Kind with
        | BackendKind.Git ->
            match repo.Git with
            | Option.None -> return Option.None // unreachable: Kind = Git implies Git = Some
            | Some git ->
                match! git.RemoteUrl(repo.Root, "origin") with
                | Ok url -> return ForgeKind.OfRemoteUrl url
                | Error _ -> return Option.None
        | BackendKind.Jj ->
            match repo.Jj with
            | Option.None -> return Option.None // unreachable: Kind = Jj implies Jj = Some
            | Some jj ->
                match! jj.Run(repo.Root, [ "git"; "remote"; "list"; "--ignore-working-copy"; "--color"; "never" ]) with
                | Error _ -> return Option.None
                | Ok output ->
                    match parseJjRemoteUrl "origin" output with
                    | Some url -> return ForgeKind.OfRemoteUrl url
                    | Option.None -> return Option.None
    }

/// Pick the forge: the explicit `--forge`, else the `origin` remote's host, else none.
let internal resolveForge
    (repo: Repo)
    (forced: ForgeKind option)
    (timeout: TimeSpan option)
    (observer: ICommandObserver option)
    : Task<Forge option> =
    task {
        let cwd = repo.Root

        let! kind =
            match forced with
            | Some k -> Task.FromResult(Some k)
            | Option.None -> detectForgeKind repo

        match kind with
        | Some ForgeKind.GitHub ->
            let c = VcsToolkit.GitHub.GitHub.Create()

            let c =
                match timeout with
                | Some t -> c.DefaultTimeout t
                | None -> c

            let c =
                match observer with
                | Some obs -> c.WithObserver obs
                | None -> c

            return Some(Forge.FromGitHub(cwd, c))
        | Some ForgeKind.GitLab ->
            let c = VcsToolkit.GitLab.GitLab.Create()

            let c =
                match timeout with
                | Some t -> c.DefaultTimeout t
                | None -> c

            let c =
                match observer with
                | Some obs -> c.WithObserver obs
                | None -> c

            return Some(Forge.FromGitLab(cwd, c))
        | Some ForgeKind.Gitea ->
            let c = VcsToolkit.Gitea.Gitea.Create()

            let c =
                match timeout with
                | Some t -> c.DefaultTimeout t
                | None -> c

            let c =
                match observer with
                | Some obs -> c.WithObserver obs
                | None -> c

            return Some(Forge.FromGitea(cwd, c))
        | _ -> return Option.None
    }

/// Read an assembly's informational version with a safe fallback for unusual launch contexts.
let internal readVersionFromAssembly (asm: Assembly | null) : string =
    match asm with
    | null -> "0.0.0-unknown"
    | asm ->
        match asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null -> "0.0.0-unknown"
        | attr when String.IsNullOrWhiteSpace attr.InformationalVersion -> "0.0.0-unknown"
        | attr -> attr.InformationalVersion

/// The server's advertised version, read from the entry assembly's
/// `AssemblyInformationalVersionAttribute` (set from the shared `<Version>` in
/// `src/Directory.Build.props`, overridden at release time via `/p:Version=...` — see
/// CLAUDE.md "Release packaging"). Falls back to `"0.0.0-unknown"` when the attribute is
/// absent (e.g. some non-standard launch scenario without a full assembly build), so the
/// handshake never silently reverts to a stale hardcoded literal.
let internal serverVersion () : string =
    Assembly.GetEntryAssembly() |> readVersionFromAssembly

/// The `Implementation` metadata advertised in the MCP handshake: the fixed server name paired
/// with `serverVersion()`. Extracted out of `runServer` so tests can prove `ServerInfo.Version`
/// is actually wired to `serverVersion()` end to end, not merely that `serverVersion()` itself
/// happens not to be the old hardcoded literal in isolation.
let internal buildServerInfo () : Implementation =
    Implementation(Name = "vcs-mcp", Version = serverVersion ())

/// Build the MCP `Tool` list from the library's catalogue (schema + annotation hints).
let private buildTools () : ResizeArray<Tool> =
    let tools = ResizeArray<Tool>()

    for spec in Catalog.all do
        let t = Tool(Name = spec.Name, Description = spec.Description)
        t.InputSchema <- JsonDocument.Parse(Catalog.inputSchema spec).RootElement.Clone()

        let ann = ToolAnnotations()
        ann.ReadOnlyHint <- Nullable spec.ReadOnly
        ann.DestructiveHint <- Nullable spec.Destructive
        ann.IdempotentHint <- Nullable spec.Idempotent
        t.Annotations <- ann
        tools.Add t

    tools

/// A tool result carrying one text block (an error result sets `IsError`).
let private textResult (text: string) (isError: bool) : CallToolResult =
    let content = ResizeArray<ContentBlock>()
    // `TextContentBlock.Type` is fixed to "text" in the SDK (no longer settable).
    content.Add(TextContentBlock(Text = text) :> ContentBlock)

    if isError then
        CallToolResult(Content = content, IsError = Nullable true)
    else
        CallToolResult(Content = content)

/// Configure and run the MCP server over stdio until the client disconnects.
let private runServer (server: VcsMcpServer) : Task =
    let builder = Host.CreateApplicationBuilder()
    // MCP speaks over stdout — send all logs to stderr so they don't corrupt the protocol.
    builder.Logging.AddConsole(fun o -> o.LogToStandardErrorThreshold <- LogLevel.Trace)
    |> ignore

    let tools = buildTools ()

    let listHandler =
        McpRequestHandler<ListToolsRequestParams, ListToolsResult>(fun _ctx _ct ->
            ValueTask<ListToolsResult>(ListToolsResult(Tools = tools)))

    let callHandler =
        McpRequestHandler<CallToolRequestParams, CallToolResult>(fun ctx _ct ->
            // `ctx.Params` is non-nullable in the SDK (a call-tool request always carries params).
            let p = ctx.Params
            let name = p.Name

            let argsElem =
                match p.Arguments with
                | null -> JsonDocument.Parse("{}").RootElement
                | dict -> JsonSerializer.SerializeToElement dict

            let work =
                task {
                    match! Catalog.callTool server name argsElem with
                    | Ok json -> return textResult json false
                    | Error(McpError.InvalidParams message) ->
                        // A protocol-level "fix your call" error (unknown tool, bad/missing
                        // argument, disabled write, unsupported forge op): raise it as a JSON-RPC
                        // error carrying `McpErrorCode.InvalidParams`, so a client can tell it apart
                        // from a tool-execution failure. `McpProtocolException` is the SDK type
                        // whose `ErrorCode` drives the resulting `JsonRpcError`; a plain
                        // `McpException` or an `IsError` result would instead be an execution error.
                        return raise (McpProtocolException(message, McpErrorCode.InvalidParams))
                    | Error(McpError.Internal message) ->
                        // A backend/network execution failure: surface it inside the result with
                        // `IsError = true` — the MCP convention for execution errors (the model
                        // sees the detail and can self-correct), not a protocol error.
                        return textResult message true
                }

            ValueTask<CallToolResult>(work))

    builder.Services
        .AddMcpServer(fun options ->
            options.ServerInfo <- buildServerInfo ()
            let caps = ServerCapabilities()
            caps.Tools <- ToolsCapability()
            options.Capabilities <- caps

            options.ServerInstructions <-
                "Drive a git/jj repository (and its forge) through typed tools. Read tools (repo_*/forge_* queries) are always available; mutating tools require the server to have been started with --allow-write or --allow-tools name,..., and reject calls otherwise.")
        .WithStdioServerTransport()
        .WithListToolsHandler(listHandler)
        .WithCallToolHandler(callHandler)
    |> ignore

    builder.Build().RunAsync()

/// The `TextWriter` a `--log-commands` sink writes to, and a cleanup action to run once at
/// shutdown: for `LogSink.File` this disposes the owned `StreamWriter`; for `LogSink.Stderr`
/// it is a no-op, since the console owns that stream. The file writer is opened in append mode
/// and auto-flushing (matched by `CommandLog.Writer`'s own explicit `Flush()` per line, so the
/// log survives an abrupt process exit rather than losing a buffered tail).
let private openLogSink (sink: LogSink) : TextWriter * (unit -> unit) =
    match sink with
    | LogSink.Stderr -> Console.Error, ignore
    | LogSink.File path ->
        let stream = new StreamWriter(path, append = true)
        stream.AutoFlush <- true
        stream :> TextWriter, (fun () -> stream.Dispose())

[<EntryPoint>]
let main argv =
    match Args.parse (List.ofArray argv) with
    | Error msg ->
        eprintfn "vcs-mcp: %s" msg
        1
    | Ok Option.None ->
        printfn "%s" Args.usage
        0
    | Ok(Some args) ->
        let observer, cleanupLog =
            match args.LogCommands with
            | Option.None -> Option.None, ignore
            | Some sink ->
                let writer, cleanup = openLogSink sink
                Some(CommandLog.Writer(writer) :> ICommandObserver), cleanup

        try
            match openRepo args.Repo args.Timeout observer with
            | Error msg ->
                eprintfn "vcs-mcp: %s" msg
                1
            | Ok repo ->
                let forge =
                    (resolveForge repo args.Forge args.Timeout observer).GetAwaiter().GetResult()

                use server = new VcsMcpServer(repo, forge, args.Writes, args.OutputBudget)
                (runServer server).GetAwaiter().GetResult()
                0
        finally
            cleanupLog ()
