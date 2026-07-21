module VcsToolkit.Mcp.Server.Tests.McpServerStdioE2eTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ModelContextProtocol
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open VcsToolkit.Mcp
open VcsToolkit.TestKit

// ---------------------------------------------------------------------------
// T-092: end-to-end smoke tests of the `vcs-mcp` binary. The library
// (VcsToolkit.Mcp) is hermetically unit-tested elsewhere; these tests instead
// spawn the REAL built binary as a child process over a TestKit git sandbox and
// drive it through the ModelContextProtocol SDK's *client* over stdio. They are the
// only layer that exercises the binary's SDK wiring (Program.fs): the initialize
// handshake, the list-tools / call-tool handlers, the ServerInfo/version wiring, and
// — critically — that the WriteGate security barrier actually refuses a mutating tool
// on the wire in the default read-only mode. A false-green here would erode that
// barrier's value, so the write-gate check is proven BOTH ways (refused without
// --allow-write, admitted with it, same tool) so the refusal can't be a mere inherent
// tool error.
//
// T-097 also pins the error-transport contract on the wire: an `McpError.InvalidParams`
// (the write gate's refusal, a bad/missing argument) is raised as a JSON-RPC **protocol**
// error the client sees as a thrown `McpException`/`McpProtocolException`, whereas an
// `McpError.Internal` (a backend command failure) comes back inside the tool result with
// `IsError = true` — so a client can programmatically tell "fix your call" apart from
// "the backend broke".
// ---------------------------------------------------------------------------

/// How to launch the freshly built `vcs-mcp` binary as a child process, plus the path to
/// its assembly (`Dll`) so a test can read the exact version the spawned process advertises.
type private Launch =
    { Command: string
      PrefixArgs: string list
      Dll: string }

/// Walk up from `start` to the directory holding the solution file `VcsToolkit.slnx`
/// (the repo/worktree root). `None` if it isn't found on the way up.
let private repoRootFrom (start: string) : string option =
    let mutable current: DirectoryInfo | null = DirectoryInfo start
    let mutable result = None

    while Option.isNone result && not (isNull current) do
        match current with
        | null -> ()
        | dir ->
            if File.Exists(Path.Combine(dir.FullName, "VcsToolkit.slnx")) then
                result <- Some dir.FullName
            else
                current <- dir.Parent

    result

/// Resolve the built `vcs-mcp` binary from the *server project's own* build output
/// (`src/VcsToolkit.Mcp.Server/bin/<config>/<tfm>/`), reached from the repo root and the
/// `<config>`/`<tfm>` of this test assembly's own output path (so a Debug/Release or TFM
/// switch tracks automatically). That output is the authoritative, self-consistent
/// artifact of the ordinary `dotnet build`/`dotnet test` — the copy-local `vcs-mcp`
/// next to the test assembly carries only a partial dependency closure and can't run the
/// host. Prefer the native apphost (`vcs-mcp.exe` on Windows, `vcs-mcp` elsewhere) and
/// fall back to `dotnet <dll>` when no apphost is produced (e.g. `UseAppHost=false`).
/// `None` when the server output isn't found — an abnormal run the guarded test skips
/// rather than hard-failing (it never trips under a normal solution `dotnet test`, where
/// the server is a build dependency, so it can't mask a real wiring regression).
let private resolveBinary () : Launch option =
    let baseDir = AppContext.BaseDirectory

    let segments =
        baseDir
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(
                [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |],
                StringSplitOptions.RemoveEmptyEntries
            )

    match repoRootFrom baseDir with
    | Some root when segments.Length >= 2 ->
        // .../tests/VcsToolkit.Mcp.Server.Tests/bin/<config>/<tfm>/ — mirror <config>/<tfm>.
        let tfm = segments.[segments.Length - 1]
        let config = segments.[segments.Length - 2]

        let serverBin =
            Path.Combine(root, "src", "VcsToolkit.Mcp.Server", "bin", config, tfm)

        let dll = Path.Combine(serverBin, "vcs-mcp.dll")

        let apphostName =
            if OperatingSystem.IsWindows() then
                "vcs-mcp.exe"
            else
                "vcs-mcp"

        let apphost = Path.Combine(serverBin, apphostName)

        if File.Exists apphost then
            Some
                { Command = apphost
                  PrefixArgs = []
                  Dll = dll }
        elif File.Exists dll then
            Some
                { Command = "dotnet"
                  PrefixArgs = [ dll ]
                  Dll = dll }
        else
            None
    | _ -> None

/// Whether `git` is on PATH — the TestKit sandbox spawns the real `git`. Git-only, so
/// (unlike the jj guards, see K-034) it always skips when absent rather than failing.
let private gitAvailable () : bool =
    try
        Raw.git "." [ "--version" ]
        true
    with _ ->
        // git isn't on PATH (or failed to spawn) — the guarded test can't run.
        false

/// Whether the tool result reported a tool-level error (`isError: true`). The MCP client
/// returns such a result normally (it is not a JSON-RPC protocol error), so this reads
/// the flag rather than catching an exception.
let private isError (result: CallToolResult) : bool =
    result.IsError.HasValue && result.IsError.Value

/// The single text content block the server returns (its JSON body).
let private textOf (result: CallToolResult) : string =
    let block =
        result.Content
        |> Seq.tryPick (fun c ->
            match c with
            | :? TextContentBlock as t -> Some t
            | _ -> None)

    match block with
    | Some t -> t.Text
    | None -> failwith "tool result carried no text content block"

/// Invoke `tool` (with optional `args`) expecting the server to answer with a JSON-RPC
/// **protocol** error rather than a tool result — the wire form of `McpError.InvalidParams`.
/// Returns the `McpException` the SDK client re-raises so the caller can assert on its message
/// (and `ErrorCode`, when it is an `McpProtocolException`). Fails the test if a result came back
/// instead, which would mean the error had been flattened into an `IsError` execution result and
/// the client could no longer tell it apart from a backend failure.
let private expectProtocolError
    (client: McpClient)
    (ct: CancellationToken)
    (tool: string)
    (args: IReadOnlyDictionary<string, obj | null> | null)
    : Task<McpException> =
    task {
        let mutable caught: McpException option = None

        try
            let! _ = client.CallToolAsync(tool, args, cancellationToken = ct)
            ()
        with :? McpException as ex ->
            // The server returned a JSON-RPC error; the SDK client surfaces it as this throw
            // (not as a returned CallToolResult). Capture it for the caller to inspect.
            caught <- Some ex

        match caught with
        | Some ex -> return ex
        | None -> return failwith $"tool {tool} returned a result but a JSON-RPC protocol error was expected"
    }

/// Assert `ex` reports the invalid-params protocol code. The SDK client re-raises a server
/// JSON-RPC error as an `McpProtocolException` carrying `ErrorCode`; if a plain `McpException`
/// surfaces instead (no code), the throw itself is the load-bearing signal, so only the code is
/// skipped, not the test.
let private assertInvalidParamsCode (ex: McpException) : unit =
    match ex with
    | :? McpProtocolException as pe -> Assert.That(pe.ErrorCode, Is.EqualTo McpErrorCode.InvalidParams)
    | _ -> ()

/// Spawn `vcs-mcp` over a fresh git sandbox (seeded with one commit on `main`),
/// connect the SDK client over stdio, run `body`, then tear the child process down. The
/// sandbox has no `origin` remote, so no forge is detected and no forge CLI is needed.
let private e2e (extraArgs: string list) (body: McpClient -> CancellationToken -> Task<unit>) : Task =
    task {
        match resolveBinary () with
        | None -> Assert.Ignore "vcs-mcp build output not found next to the test assembly (server project not built)"
        | Some launch ->
            if not (gitAvailable ()) then
                Assert.Ignore "git not available on PATH"

            use sandbox = GitSandbox.Init "mcp-e2e"
            sandbox.CommitFile("README.md", "hello\n", "seed the working copy so HEAD is born")

            // A generous ceiling so a hung child can't wedge the whole test run.
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 60.0)

            let args =
                ResizeArray<string>(launch.PrefixArgs @ [ "--repo"; sandbox.Path ] @ extraArgs)

            let options =
                StdioClientTransportOptions(
                    Command = launch.Command,
                    Arguments = args,
                    Name = "vcs-mcp-e2e",
                    WorkingDirectory = sandbox.Path
                )

            let transport = new StdioClientTransport(options)
            let! client = McpClient.CreateAsync(transport, cancellationToken = cts.Token)

            try
                do! body client cts.Token
            finally
                // Dispose the client (and with it the transport) BEFORE the sandbox dir is
                // removed, so the child process releases the repo it is serving.
                (client :> IAsyncDisposable).DisposeAsync().GetAwaiter().GetResult()
    }

[<TestFixture>]
type McpServerStdioE2eTests() =

    /// A successful `McpClient.CreateAsync` IS the `initialize` handshake; assert the server
    /// advertised the `Implementation` built by `Main.buildServerInfo`.
    [<Test>]
    member _.InitializeHandshakeAdvertisesProgramServerInfo() : Task =
        e2e [] (fun client _ct ->
            task {
                let info = client.ServerInfo
                Assert.That(info.Name, Is.EqualTo "vcs-mcp")

                // The spawned binary reports `serverVersion()` = its own entry assembly's
                // informational version. Read that exact version straight off the file that was
                // launched (its ProductVersion is the AssemblyInformationalVersion) for an
                // independently derived expected value — proving `buildServerInfo`/`serverVersion`
                // reaches the wire, not merely that some non-empty version came back. (Reading the
                // spawned file, not the in-process copy, sidesteps any drift between the two.)
                match resolveBinary () with
                | Some launch ->
                    let expectedVersion = FileVersionInfo.GetVersionInfo(launch.Dll).ProductVersion
                    Assert.That(expectedVersion, Is.Not.EqualTo "0.0.0-unknown")
                    Assert.That(info.Version, Is.EqualTo expectedVersion)
                | None -> Assert.Fail "server binary path unresolved inside e2e (should not happen)"
            })

    /// `tools/list` over the wire must advertise exactly `Catalog.all`, with each tool's
    /// `ReadOnlyHint` matching the `WriteGate` contract (read-only iff not write-gated).
    [<Test>]
    member _.ToolsListMatchesCatalogAndWriteGateMarkup() : Task =
        e2e [] (fun client ct ->
            task {
                let! (tools: IList<McpClientTool>) = client.ListToolsAsync(cancellationToken = ct)
                let liveNames = tools |> Seq.map (fun t -> t.Name) |> Set.ofSeq
                let catalogNames = Catalog.all |> List.map (fun t -> t.Name) |> Set.ofList
                // Compare via F# structural `=` (K-017: `Is.EqualTo` on collections is FS0041-ambiguous).
                Assert.That((liveNames = catalogNames), Is.True, "tools/list must advertise exactly Catalog.all")

                for tool in tools do
                    match tool.ProtocolTool.Annotations with
                    | null -> Assert.Fail $"tool {tool.Name} advertised no annotations"
                    | ann ->
                        Assert.That(ann.ReadOnlyHint.HasValue, Is.True, tool.Name)
                        // Cross-check the advertised hint against the independent WriteGate
                        // source of truth, not the catalogue's own ReadOnly flag.
                        let expectedReadOnly = not (WriteTools.asSet.Contains tool.Name)
                        Assert.That(ann.ReadOnlyHint.Value, Is.EqualTo expectedReadOnly, tool.Name)

                for writeTool in WriteTools.all do
                    Assert.That(liveNames.Contains writeTool, Is.True, $"write tool {writeTool} must be present")
            })

    /// A read tool (`repo_snapshot`) called against the sandbox returns a well-formed JSON
    /// snapshot of the seeded state.
    [<Test>]
    member _.ReadToolRepoSnapshotReturnsWellFormedJson() : Task =
        e2e [] (fun client ct ->
            task {
                let! result = client.CallToolAsync("repo_snapshot", cancellationToken = ct)
                Assert.That(isError result, Is.False, "repo_snapshot must succeed against a real sandbox")

                use doc = JsonDocument.Parse(textOf result)
                let root = doc.RootElement
                Assert.That(root.ValueKind, Is.EqualTo JsonValueKind.Object)
                // The sandbox is one clean commit on `main`.
                Assert.That(root.GetProperty("branch").GetString(), Is.EqualTo "main")
                Assert.That(root.GetProperty("dirty").GetBoolean(), Is.False)
                Assert.That(root.GetProperty("conflicted").GetBoolean(), Is.False)
                Assert.That(root.GetProperty("operation").GetString(), Is.EqualTo "Clear")
                // `head` is the committed oid (a present, non-null string) on a born repo.
                Assert.That(root.GetProperty("head").ValueKind, Is.EqualTo JsonValueKind.String)
            })

    /// The write gate refuses a mutating tool in the default read-only mode. `repo_abort_in_progress`
    /// is write-gated and argument-free, so the gate rejects it before touching the repo. The refusal
    /// is an `McpError.InvalidParams`, which the server raises as a JSON-RPC **protocol** error — so
    /// the client sees a thrown `McpException` (with the invalid-params code), NOT an `IsError` result.
    [<Test>]
    member _.WriteToolRefusedInDefaultReadOnlyMode() : Task =
        e2e [] (fun client ct ->
            task {
                let! ex = expectProtocolError client ct "repo_abort_in_progress" null
                Assert.That(ex.Message, Does.Contain "allow-write", "the refusal must cite the write gate")
                assertInvalidParamsCode ex
            })

    /// A bad/missing tool argument is an `McpError.InvalidParams` and must likewise surface as a
    /// JSON-RPC protocol error (a thrown `McpException`), not an `IsError` result. `repo_show_file`
    /// is a read tool (no write gate in the way) whose required `rev`/`path` arguments are omitted
    /// here, so the argument parse fails before any backend spawn.
    [<Test>]
    member _.BadArgumentSurfacesAsProtocolErrorNotResult() : Task =
        e2e [] (fun client ct ->
            task {
                let! ex = expectProtocolError client ct "repo_show_file" null
                Assert.That(ex.Message, Does.Contain "rev", "the error must name the missing required argument")
                assertInvalidParamsCode ex
            })

    /// The contrast to the two protocol-error cases: an `McpError.Internal` — a real backend
    /// command failure — comes back INSIDE the tool result with `IsError = true`, not as a thrown
    /// protocol error, so a client can tell "the backend broke" apart from "fix your call".
    /// `repo_checkout` to a nonexistent ref is admitted by `--allow-write`, then fails at `git`
    /// (a `RepoError.Vcs`, which maps to `McpError.Internal`).
    [<Test>]
    member _.InternalBackendFailureSurfacesAsIsErrorResult() : Task =
        e2e [ "--allow-write" ] (fun client ct ->
            task {
                let args = Dictionary<string, obj | null>()
                args["reference"] <- "no-such-ref-xyz-t097"

                let! result = client.CallToolAsync("repo_checkout", args, cancellationToken = ct)
                // A backend failure is a tool-execution error: it is a RESULT with IsError set,
                // never a thrown protocol error. Reaching this line already proves no throw.
                Assert.That(isError result, Is.True, "a backend command failure must surface as an IsError result")
            })

    /// Positive control: the SAME write tool passes the gate under `--allow-write`, proving the
    /// refusal above is the gate and not an inherent tool error. On a clean repo the abort is a
    /// no-op reporting `Clear`.
    [<Test>]
    member _.WriteToolAdmittedUnderAllowWrite() : Task =
        e2e [ "--allow-write" ] (fun client ct ->
            task {
                let! result = client.CallToolAsync("repo_abort_in_progress", cancellationToken = ct)
                Assert.That(isError result, Is.False, "with --allow-write the gate must admit the write tool")

                use doc = JsonDocument.Parse(textOf result)
                Assert.That(doc.RootElement.GetProperty("operation").GetString(), Is.EqualTo "Clear")
            })

    /// T-107: `--log-commands <path>` attaches a diagnostic observer to the repo's git client —
    /// a real tool call (`repo_snapshot`, which spawns several `git` reads) leaves matching
    /// start/finish lines in the file, with the exit code visible and no secrets involved (the
    /// sandbox carries no credentials to begin with).
    [<Test>]
    member _.LogCommandsFlagWritesStartAndFinishLinesToFile() : Task =
        task {
            use logDir = new TempDir("mcp-log-commands")
            let logPath = Path.Combine(logDir.Path, "commands.log")

            do!
                e2e [ "--log-commands"; logPath ] (fun client ct ->
                    task {
                        let! result = client.CallToolAsync("repo_snapshot", cancellationToken = ct)
                        Assert.That(isError result, Is.False, "repo_snapshot must succeed against a real sandbox")
                    })

            let logText = File.ReadAllText logPath
            Assert.That(logText, Does.Contain "vcs-mcp: start program=git", "a start line for the git client")
            Assert.That(logText, Does.Contain "vcs-mcp: done  program=git", "a finish line for the git client")
            Assert.That(logText, Does.Contain "outcome=ok(", "the observed command succeeded")
        }
