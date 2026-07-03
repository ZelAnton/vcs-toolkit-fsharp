module Main

// The `vcs-mcp` binary: an MCP server over stdio. An agent harness launches it with a
// `mcpServers` config entry; it speaks JSON-RPC on stdin/stdout. Read tools are always
// available; `--allow-write` enables every mutating tool, `--allow-tools` a named subset.
// The forge is auto-detected from the repo's `origin` remote unless `--forge` overrides it.
// The git client is hardened (repo hooks and config disabled) so serving a repository you
// didn't create can't execute its hooks, and every command carries a `--timeout`.

open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open ModelContextProtocol.Server
open ModelContextProtocol.Protocol
open VcsToolkit.Core
open VcsToolkit.Git
open VcsToolkit.Jj
open VcsToolkit.Forge
open VcsToolkit.Mcp

/// A hardened git client carrying the optional per-command timeout.
let private hardenedGit (timeout: TimeSpan option) : Git =
    match timeout with
    | Some t -> Git.Hardened().DefaultTimeout t
    | None -> Git.Hardened()

/// Open the repo at `dir` with a hardened, timeout-bound client. Mirrors `Repo.Open`'s
/// detection but injects the hardened/timeout client instead of the plain default.
let private openRepo (dir: string) (timeout: TimeSpan option) : Result<Repo, string> =
    let abs = IO.Path.GetFullPath dir

    match Detect.detect abs with
    | Option.None -> Error(sprintf "no git or jj repository found at or above %s" abs)
    | Some located ->
        match located.Kind with
        | BackendKind.Git -> Ok(Repo.FromGit(located.Root, abs, hardenedGit timeout))
        | BackendKind.Jj ->
            let jj =
                match timeout with
                | Some t -> Jj.Create().DefaultTimeout t
                | None -> Jj.Create()

            Ok(Repo.FromJj(located.Root, abs, jj))

/// Best-effort: read the `origin` remote URL and classify its host.
let private detectForgeKind (root: string) (timeout: TimeSpan option) : Task<ForgeKind option> =
    task {
        match! (hardenedGit timeout).RemoteUrl(root, "origin") with
        | Ok url -> return ForgeKind.OfRemoteUrl url
        | Error _ -> return Option.None
    }

/// Pick the forge: the explicit `--forge`, else the `origin` remote's host, else none.
let private resolveForge (repo: Repo) (forced: ForgeKind option) (timeout: TimeSpan option) : Task<Forge option> =
    task {
        let cwd = repo.Root

        let! kind =
            match forced with
            | Some k -> Task.FromResult(Some k)
            | Option.None -> detectForgeKind repo.Root timeout

        match kind with
        | Some ForgeKind.GitHub ->
            let c = VcsToolkit.GitHub.GitHub.Create()

            let c =
                match timeout with
                | Some t -> c.DefaultTimeout t
                | None -> c

            return Some(Forge.FromGitHub(cwd, c))
        | Some ForgeKind.GitLab ->
            let c = VcsToolkit.GitLab.GitLab.Create()

            let c =
                match timeout with
                | Some t -> c.DefaultTimeout t
                | None -> c

            return Some(Forge.FromGitLab(cwd, c))
        | Some ForgeKind.Gitea ->
            let c = VcsToolkit.Gitea.Gitea.Create()

            let c =
                match timeout with
                | Some t -> c.DefaultTimeout t
                | None -> c

            return Some(Forge.FromGitea(cwd, c))
        | _ -> return Option.None
    }

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
    content.Add(TextContentBlock(Text = text, Type = "text") :> ContentBlock)

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
            match ctx.Params with
            | null -> ValueTask<CallToolResult>(textResult "missing call parameters" true)
            | p ->
                let name = p.Name

                let argsElem =
                    match p.Arguments with
                    | null -> JsonDocument.Parse("{}").RootElement
                    | dict -> JsonSerializer.SerializeToElement dict

                let work =
                    task {
                        match! Catalog.callTool server name argsElem with
                        | Ok json -> return textResult json false
                        | Error e -> return textResult e.Message true
                    }

                ValueTask<CallToolResult>(work))

    builder.Services
        .AddMcpServer(fun options ->
            options.ServerInfo <- Implementation(Name = "vcs-mcp", Version = "1.0.0")
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
        match openRepo args.Repo args.Timeout with
        | Error msg ->
            eprintfn "vcs-mcp: %s" msg
            1
        | Ok repo ->
            let forge = (resolveForge repo args.Forge args.Timeout).GetAwaiter().GetResult()
            use server = new VcsMcpServer(repo, forge, args.Writes)
            (runServer server).GetAwaiter().GetResult()
            0
