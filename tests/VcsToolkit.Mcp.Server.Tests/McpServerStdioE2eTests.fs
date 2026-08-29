module VcsToolkit.Mcp.Server.Tests.McpServerStdioE2eTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Reflection
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open NUnit.Framework
open ModelContextProtocol
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open VcsToolkit.Agent
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

/// Resolve the built `vcs-agent` assembly that owns the CLI adapter. The outcome replay
/// invokes its internal async entry point by reflection because both executable projects
/// intentionally compile a global `Main` module; a direct assembly reference would make
/// those two module types collide in this test assembly.
let private resolveAgentAssembly () : string option =
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
        let tfm = segments.[segments.Length - 1]
        let config = segments.[segments.Length - 2]

        let assembly =
            Path.Combine(root, "src", "VcsToolkit.Agent.Server", "bin", config, tfm, "vcs-agent.dll")

        if File.Exists assembly then Some assembly else None
    | _ -> None

/// Execute the real `vcs-agent` argument/exit/wire adapter from its built assembly.
let private runAgentCli
    (assemblyPath: string)
    (argv: string array)
    (cancellationToken: CancellationToken)
    : Task<AgentExecution> =
    let assembly = Assembly.LoadFrom assemblyPath

    let entryPoint =
        assembly.GetType "Main"
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failwith "vcs-agent assembly has no Main module")

    let run =
        entryPoint.GetMethod(
            "runWithCancellation",
            BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic
        )
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failwith "vcs-agent Main has no runWithCancellation adapter")

    match run.Invoke(null, [| box argv; box cancellationToken |]) with
    | :? Task<AgentExecution> as execution -> execution
    | _ -> failwith "vcs-agent Main.runWithCancellation returned an unexpected task type"

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

/// Transport disposal closes stdin and starts child shutdown, but on Windows the child can need
/// a short tail to unwind the host and dispose its command-log writer. Wait for that ownership to
/// end before using File.ReadAllText, whose default sharing mode deliberately refuses an active
/// writer. The final unguarded read preserves the useful IOException if the child never releases it.
let private readAllTextAfterWriterExit (path: string) : Task<string> =
    task {
        let deadline = Stopwatch.StartNew()
        let mutable text = None

        while text.IsNone && deadline.Elapsed < TimeSpan.FromSeconds 5.0 do
            try
                text <- Some(File.ReadAllText path)
            with :? IOException ->
                // The MCP transport has been disposed, so the server is already shutting down;
                // allow its async host teardown to reach Program.main's log-sink finally block.
                do! Task.Delay 50

        match text with
        | Some value -> return value
        | None -> return File.ReadAllText path
    }

/// A loopback HTTP remote that accepts Git's first fetch request and holds it until released.
/// This makes a real repo_fetch command in-flight without relying on hooks, which the server's
/// hardened Git profile intentionally disables.
type private BlockingHttpServer() =
    let port =
        use probe = new TcpListener(IPAddress.Loopback, 0)
        probe.Start()
        (probe.LocalEndpoint :?> IPEndPoint).Port

    let listener = new HttpListener()

    let requestReceived =
        new TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let release =
        new TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    do
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()

    let requestLoop =
        task {
            try
                let! context = listener.GetContextAsync()
                requestReceived.TrySetResult() |> ignore
                do! release.Task
                context.Response.StatusCode <- 200
                context.Response.Close()
            with
            | :? HttpListenerException
            | :? ObjectDisposedException ->
                // Teardown closes the listener while the request loop is waiting; no response is
                // needed once the child process has been cancelled or the test has finished.
                ()
        }

    member _.Url = $"http://127.0.0.1:{port}/"

    member _.WaitForRequest(timeout: TimeSpan) : Task<bool> =
        task {
            let! completed = Task.WhenAny(requestReceived.Task :> Task, Task.Delay timeout)
            return Object.ReferenceEquals(completed, requestReceived.Task)
        }

    member _.Release() = release.TrySetResult() |> ignore

    interface IDisposable with
        member _.Dispose() =
            release.TrySetResult() |> ignore
            listener.Stop()
            listener.Close()
            let _ = requestLoop.Status
            ()

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
let private e2eWithSandbox
    (extraArgs: string list)
    (prepare: GitSandbox -> unit)
    (body: GitSandbox -> McpClient -> CancellationToken -> Task<unit>)
    : Task =
    task {
        match resolveBinary () with
        | None -> Assert.Ignore "vcs-mcp build output not found next to the test assembly (server project not built)"
        | Some launch ->
            if not (gitAvailable ()) then
                Assert.Ignore "git not available on PATH"

            use sandbox = GitSandbox.Init "mcp-e2e"
            sandbox.CommitFile("README.md", "hello\n", "seed the working copy so HEAD is born")
            prepare sandbox

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
                do! body sandbox client cts.Token
            finally
                // Dispose the client (and with it the transport) BEFORE the sandbox dir is
                // removed, so the child process releases the repo it is serving.
                (client :> IAsyncDisposable).DisposeAsync().GetAwaiter().GetResult()
    }

let private e2e (extraArgs: string list) (body: McpClient -> CancellationToken -> Task<unit>) : Task =
    e2eWithSandbox extraArgs ignore (fun _ client ct -> body client ct)

/// Send a cancellation notification through a raw initialized JSON-RPC stdio session and
/// distinguish an actual CallToolResult from the SDK's specified response suppression. The
/// ordinary client cancels its own pending task immediately, so this raw path is necessary to
/// prove whether the host emitted a response rather than inferring that from client state.
let private callCancelledToolOverRawStdio
    (extraArgs: string list)
    (repoPath: string)
    (remote: BlockingHttpServer)
    (tool: string)
    (arguments: JsonElement)
    (requestId: string)
    (expectedRevision: string)
    (cancellationToken: CancellationToken)
    : Task<CallToolResult option> =
    task {
        match resolveBinary () with
        | None -> return failwith "vcs-mcp build output disappeared during stdio replay"
        | Some launch ->
            let startInfo = ProcessStartInfo()
            startInfo.FileName <- launch.Command
            startInfo.WorkingDirectory <- repoPath
            startInfo.UseShellExecute <- false
            startInfo.CreateNoWindow <- true
            startInfo.RedirectStandardInput <- true
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true

            for argument in launch.PrefixArgs @ [ "--repo"; repoPath ] @ extraArgs do
                startInfo.ArgumentList.Add argument

            use childProcess = new Process(StartInfo = startInfo)

            if not (childProcess.Start()) then
                failwith "failed to start vcs-mcp raw stdio replay process"

            let stderrRead = childProcess.StandardError.ReadToEndAsync(cancellationToken)
            let stdoutLines = Channel.CreateUnbounded<string>()

            let stdoutPump =
                task {
                    try
                        let mutable reading = true

                        while reading do
                            let! line = childProcess.StandardOutput.ReadLineAsync()

                            match line with
                            | null -> reading <- false
                            | value -> do! stdoutLines.Writer.WriteAsync(value, CancellationToken.None)

                        stdoutLines.Writer.TryComplete() |> ignore
                    with ex ->
                        stdoutLines.Writer.TryComplete ex |> ignore
                        return raise ex
                }

            let sendJson (json: string) : Task =
                task {
                    do! childProcess.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken)
                    do! childProcess.StandardInput.FlushAsync cancellationToken
                }

            let readJson (stage: string) : Task<string> =
                task {
                    try
                        return! stdoutLines.Reader.ReadAsync(cancellationToken).AsTask()
                    with :? ChannelClosedException ->
                        let stderr = stderrRead.GetAwaiter().GetResult()
                        return failwith $"vcs-mcp closed stdout during {stage}: {stderr}"
                }

            try
                let initialize =
                    JsonSerializer.Serialize(
                        {| jsonrpc = "2.0"
                           id = 1
                           method = "initialize"
                           ``params`` =
                            {| protocolVersion = "2025-06-18"
                               capabilities = Dictionary<string, obj>()
                               clientInfo =
                                {| name = "outcome-corpus-raw"
                                   version = "1" |} |} |},
                        McpJsonUtilities.DefaultOptions
                    )

                do! sendJson initialize
                let! initializeResponse = readJson "initialize"
                use initializeDocument = JsonDocument.Parse initializeResponse
                let initializeRoot = initializeDocument.RootElement
                Assert.That(initializeRoot.GetProperty("id").GetInt32(), Is.EqualTo 1)
                Assert.That(initializeRoot.TryGetProperty("result") |> fst, Is.True, initializeResponse)

                let initialized =
                    JsonSerializer.Serialize(
                        {| jsonrpc = "2.0"
                           method = "notifications/initialized" |},
                        McpJsonUtilities.DefaultOptions
                    )

                do! sendJson initialized

                use emptyArguments = JsonDocument.Parse "{}"

                let fetch =
                    JsonSerializer.Serialize(
                        {| jsonrpc = "2.0"
                           id = "outcome-corpus-lock-holder"
                           method = RequestMethods.ToolsCall
                           ``params`` =
                            {| name = "repo_fetch"
                               arguments = emptyArguments.RootElement |} |},
                        McpJsonUtilities.DefaultOptions
                    )

                do! sendJson fetch
                let! started = remote.WaitForRequest(TimeSpan.FromSeconds 5.0)

                Assert.That(started, Is.True, "raw cancellation replay must hold the real repository write lock")

                let call =
                    JsonSerializer.Serialize(
                        {| jsonrpc = "2.0"
                           id = requestId
                           method = RequestMethods.ToolsCall
                           ``params`` = {| name = tool; arguments = arguments |} |},
                        McpJsonUtilities.DefaultOptions
                    )

                do! sendJson call
                do! Task.Delay(250, cancellationToken)

                let cancel =
                    JsonSerializer.Serialize(
                        {| jsonrpc = "2.0"
                           method = NotificationMethods.CancelledNotification
                           ``params`` =
                            {| requestId = requestId
                               reason = "versioned outcome replay" |} |},
                        McpJsonUtilities.DefaultOptions
                    )

                do! sendJson cancel

                let cancelLockHolder =
                    JsonSerializer.Serialize(
                        {| jsonrpc = "2.0"
                           method = NotificationMethods.CancelledNotification
                           ``params`` =
                            {| requestId = "outcome-corpus-lock-holder"
                               reason = "release outcome replay fixture" |} |},
                        McpJsonUtilities.DefaultOptions
                    )

                do! sendJson cancelLockHolder
                remote.Release()

                let probe =
                    JsonSerializer.Serialize(
                        {| jsonrpc = "2.0"
                           id = "outcome-corpus-after-cancel"
                           method = RequestMethods.ToolsCall
                           ``params`` =
                            {| name = "repo_snapshot"
                               arguments = emptyArguments.RootElement |} |},
                        McpJsonUtilities.DefaultOptions
                    )

                do! sendJson probe
                let mutable probeSeen = false

                let inspectResponse (response: string) =
                    use responseDocument = JsonDocument.Parse response
                    let root = responseDocument.RootElement
                    let hasId, idElement = root.TryGetProperty "id"

                    if hasId then
                        let responseId =
                            match idElement.ValueKind with
                            | JsonValueKind.String -> idElement.GetString()
                            | JsonValueKind.Number -> string (idElement.GetInt64())
                            | _ -> null

                        match responseId with
                        | value when value = requestId ->
                            Assert.Fail($"cancelled target request produced a late response: {response}")
                        | "outcome-corpus-lock-holder" ->
                            Assert.Fail($"cancelled lock-holder request produced a late response: {response}")
                        | "outcome-corpus-after-cancel" ->
                            Assert.That(probeSeen, Is.False, "post-cancellation probe responded more than once")
                            Assert.That(root.TryGetProperty("error") |> fst, Is.False, response)

                            let probeResult =
                                JsonSerializer.Deserialize<CallToolResult>(
                                    root.GetProperty("result").GetRawText(),
                                    McpJsonUtilities.DefaultOptions
                                )
                                |> Option.ofObj
                                |> Option.defaultWith (fun () -> failwith "post-cancellation tools/call returned null")

                            Assert.That(
                                isError probeResult,
                                Is.False,
                                "the raw MCP session must remain usable after cancellation"
                            )

                            use snapshot = JsonDocument.Parse(textOf probeResult)
                            let snapshotRoot = snapshot.RootElement
                            Assert.That(snapshotRoot.GetProperty("head").GetString(), Is.EqualTo expectedRevision)
                            Assert.That(snapshotRoot.GetProperty("dirty").GetBoolean(), Is.True)
                            probeSeen <- true
                        | _ -> Assert.Fail($"unexpected post-cancellation JSON-RPC response: {response}")

                let mutable quiescent = false

                while not quiescent do
                    use quietPeriod = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                    quietPeriod.CancelAfter(TimeSpan.FromSeconds 1.0)

                    try
                        let! response = stdoutLines.Reader.ReadAsync(quietPeriod.Token).AsTask()
                        inspectResponse response
                    with :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                        // One full second with no response bounds the live-session race window;
                        // terminal shutdown below then drains anything that was still in flight.
                        quiescent <- true

                childProcess.StandardInput.Close()

                if not (childProcess.WaitForExit 5000) then
                    childProcess.Kill(entireProcessTree = true)
                    childProcess.WaitForExit()
                    Assert.Fail "vcs-mcp did not reach terminal shutdown after stdin closed"

                do! stdoutPump
                let mutable buffered = Unchecked.defaultof<string>

                while stdoutLines.Reader.TryRead(&buffered) do
                    inspectResponse buffered

                Assert.That(probeSeen, Is.True, "post-cancellation probe response was not observed before shutdown")

                return None
            finally
                remote.Release()
                childProcess.StandardInput.Close()

                if not (childProcess.WaitForExit 5000) then
                    childProcess.Kill(entireProcessTree = true)
                    childProcess.WaitForExit()
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

    /// `tools/list` advertises the capability-aware intent subset plus every compatible
    /// low-level tool. Annotation hints still match the independent WriteGate contract.
    [<Test>]
    member _.ToolsListMatchesCatalogAndWriteGateMarkup() : Task =
        e2e [] (fun client ct ->
            task {
                let! (tools: IList<McpClientTool>) = client.ListToolsAsync(cancellationToken = ct)
                let liveNames = tools |> Seq.map (fun t -> t.Name) |> Set.ofSeq

                let catalogNames =
                    Catalog.all
                    |> List.filter (fun tool ->
                        not (tool.Name.StartsWith("agent_", StringComparison.Ordinal))
                        || tool.Name = "agent_inspect"
                        || tool.Name = "agent_changes")
                    |> List.map _.Name
                    |> Set.ofList

                // Compare via F# structural `=` (K-017: `Is.EqualTo` on collections is FS0041-ambiguous).
                Assert.That((liveNames = catalogNames), Is.True, "tools/list must match server capabilities")

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
                    if writeTool.StartsWith("agent_", StringComparison.Ordinal) then
                        Assert.That(
                            liveNames.Contains writeTool,
                            Is.False,
                            $"unavailable intent {writeTool} must be omitted"
                        )
                    else
                        Assert.That(
                            liveNames.Contains writeTool,
                            Is.True,
                            $"low-level write {writeTool} must remain present"
                        )
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

            let! logText = readAllTextAfterWriterExit logPath
            Assert.That(logText, Does.Contain "vcs-mcp: start program=git", "a start line for the git client")
            Assert.That(logText, Does.Contain "vcs-mcp: done  program=git", "a finish line for the git client")
            Assert.That(logText, Does.Contain "outcome=ok(", "the observed command succeeded")
        }

    /// The request cancellation must cross the SDK handler boundary while a real write command
    /// is in flight. A blocking loopback fetch makes the first call hold the server's repo lock;
    /// cancelling that MCP request must release the lock so a second write call can complete.
    [<Test>]
    member _.RequestCancellationStopsInFlightWriteAndKeepsServerUsable() : Task =
        task {
            use blockingRemote = new BlockingHttpServer()

            let prepare (sandbox: GitSandbox) =
                sandbox.Git [ "remote"; "add"; "origin"; blockingRemote.Url ]

            try
                do!
                    e2eWithSandbox [ "--allow-write" ] prepare (fun _sandbox client ct ->
                        task {
                            let checkoutArgs = Dictionary<string, obj | null>()
                            checkoutArgs["reference"] <- "main"

                            use requestCts = new CancellationTokenSource()

                            let firstCall =
                                client.CallToolAsync("repo_fetch", cancellationToken = requestCts.Token).AsTask()

                            let! started = blockingRemote.WaitForRequest(TimeSpan.FromSeconds 5.0)

                            Assert.That(started, Is.True, "the first MCP call must reach the in-flight backend fetch")

                            let secondCall =
                                client.CallToolAsync("repo_checkout", checkoutArgs, cancellationToken = ct).AsTask()

                            let secondCallAsTask = secondCall :> Task
                            let! stillWaiting = Task.WhenAny(secondCallAsTask, Task.Delay 250)

                            Assert.That(
                                Object.ReferenceEquals(stillWaiting, secondCallAsTask),
                                Is.False,
                                "the second write must wait while the first call owns the repo lock"
                            )

                            requestCts.Cancel()

                            let firstCallAsTask = firstCall :> Task
                            let! firstFinished = Task.WhenAny(firstCallAsTask, Task.Delay(TimeSpan.FromSeconds 5.0))

                            Assert.That(
                                Object.ReferenceEquals(firstFinished, firstCallAsTask),
                                Is.True,
                                "the cancelled MCP request must finish promptly"
                            )

                            let! secondFinished = Task.WhenAny(secondCallAsTask, Task.Delay(TimeSpan.FromSeconds 5.0))

                            Assert.That(
                                Object.ReferenceEquals(secondFinished, secondCallAsTask),
                                Is.True,
                                "the follow-up write must run after cancellation releases the repo lock"
                            )

                            let! secondResult = secondCall

                            Assert.That(
                                isError secondResult,
                                Is.False,
                                "the repository must remain usable after cancellation"
                            )

                            let! snapshot = client.CallToolAsync("repo_snapshot", cancellationToken = ct)
                            Assert.That(isError snapshot, Is.False, "a read after cancellation must still succeed")
                        })
            finally
                // If an implementation fails to cancel the fetch, release it before the client
                // and sandbox teardown so the failed test cannot strand a child process.
                blockingRemote.Release()
        }

    /// Replay the mandatory versioned outcomes through both executable transport adapters:
    /// `vcs-agent`'s real argument/exit renderer and the spawned `vcs-mcp` host's initialized
    /// JSON-RPC `tools/call` handler over stdio. Cancellation uses the protocol notification
    /// for a known request id while the call is waiting for the real per-repository write lock,
    /// and asserts the SDK's response-suppressing cancellation semantics explicitly rather than
    /// pretending that this transport produces a CallToolResult for a cancelled request.
    [<Test; NonParallelizable>]
    member _.VersionedOutcomeCorpusConvergesAcrossCliAndMcpStdioTransports() : Task =
        task {
            match resolveAgentAssembly (), repoRootFrom AppContext.BaseDirectory with
            | None, _ -> Assert.Ignore "vcs-agent build output not found (Agent.Server build dependency missing)"
            | _, None -> Assert.Ignore "repository root not found from the MCP server test output"
            | Some agentAssembly, Some repoRoot ->
                let corpusPath =
                    Path.Combine(repoRoot, "evals", "vcs-agent", "outcome-corpus.v1.json")

                use corpus = JsonDocument.Parse(File.ReadAllText corpusPath)
                let corpusRoot = corpus.RootElement

                let requiredString (element: JsonElement) (propertyName: string) : string =
                    match element.GetProperty(propertyName).GetString() with
                    | null -> failwith $"required corpus property '{propertyName}' is null"
                    | value -> value

                Assert.That(corpusRoot.GetProperty("contractVersion").GetString(), Is.EqualTo Agent.ContractVersion)

                let replay = corpusRoot.GetProperty "replay"

                Assert.That(
                    replay.GetProperty("cliAdapter").GetString(),
                    Is.EqualTo "vcs-agent Main.runWithCancellation"
                )

                Assert.That(
                    replay.GetProperty("mcpAdapter").GetString(),
                    Is.EqualTo "vcs-mcp stdio JSON-RPC tools/call"
                )

                Assert.That(
                    replay.GetProperty("normalization").GetString(),
                    Is.EqualTo(
                        "CallToolResult text uses the complete JSON envelope with insignificant whitespace removed; MCP cancellation is a response-suppressing JSON-RPC request cancellation"
                    )
                )

                let provenance = corpusRoot.GetProperty("provenance").EnumerateArray() |> Seq.toList

                let provenancePaths =
                    provenance |> Seq.map (fun entry -> requiredString entry "path") |> Set.ofSeq

                let mandatoryProvenance =
                    Set.ofList
                        [ "src/VcsToolkit.Agent/Contract.fs"
                          "src/VcsToolkit.Agent/Agent.fs"
                          "src/VcsToolkit.Agent.Server/Program.fs"
                          "src/VcsToolkit.Mcp/Server.fs"
                          "src/VcsToolkit.Mcp/Catalog.fs"
                          "src/VcsToolkit.Mcp.Server/Program.fs"
                          "tests/VcsToolkit.Mcp.Tests/McpTests.fs"
                          "tests/VcsToolkit.Mcp.Server.Tests/McpServerStdioE2eTests.fs"
                          "skills/using-vcs-agent/SKILL.md"
                          "skills/using-vcs-agent/references/contract.v1.json" ]

                Assert.That(
                    (provenancePaths = mandatoryProvenance),
                    Is.True,
                    "outcome provenance cannot omit or substitute a transport, replay, or Skill authority"
                )

                for entry in provenance do
                    let relative = requiredString entry "path"
                    let expectedHash = requiredString entry "sha256"

                    let currentHash =
                        Path.Combine(repoRoot, relative)
                        |> File.ReadAllBytes
                        |> SHA256.HashData
                        |> Convert.ToHexString
                        |> _.ToLowerInvariant()

                    Assert.That(currentHash, Is.EqualTo expectedHash, $"stale outcome provenance: {relative}")

                let skillContractPath =
                    Path.Combine(repoRoot, requiredString replay "cliContractPath")

                use skillContract = JsonDocument.Parse(File.ReadAllText skillContractPath)
                let skillRoot = skillContract.RootElement

                Assert.That(
                    skillRoot.GetProperty("agentContractVersion").GetString(),
                    Is.EqualTo Agent.ContractVersion,
                    "Skill and outcome corpus must target the current Agent contract"
                )

                let routingPolicy = skillRoot.GetProperty "routingPolicy"

                Assert.That(
                    routingPolicy.GetProperty("authorizationDenied").GetProperty("mutationOutcome").GetString(),
                    Is.EqualTo "denied"
                )

                Assert.That(
                    routingPolicy.GetProperty("giteaPublication").GetProperty("agentCapability").GetString(),
                    Is.EqualTo "unsupported-forge"
                )

                let scenarios = corpusRoot.GetProperty("scenarios").EnumerateArray() |> Seq.toList

                let scenarioIds =
                    scenarios |> Seq.map (fun scenario -> requiredString scenario "id") |> Set.ofSeq

                let mandatoryScenarioIds =
                    Set.ofList [ "write-denied"; "unsupported-forge"; "cancellation"; "output-limit" ]

                Assert.That(
                    (scenarioIds = mandatoryScenarioIds),
                    Is.True,
                    "the mandatory denial, unsupported, cancellation, and output-budget replay set cannot be narrowed"
                )

                let normalizeJson (json: string) =
                    use document = JsonDocument.Parse json
                    JsonSerializer.Serialize document.RootElement

                let replacePlaceholders repo revision (value: string) =
                    value
                        .Replace("<repo>", repo, StringComparison.Ordinal)
                        .Replace("<revision>", revision, StringComparison.Ordinal)

                let replayScenario (scenario: JsonElement) (blockingRemote: BlockingHttpServer option) : Task =
                    let id = requiredString scenario "id"
                    let configuration = requiredString scenario "mcpConfiguration"

                    let prepare (sandbox: GitSandbox) =
                        match configuration with
                        | "conflicted-git" ->
                            sandbox.CommitFile("a.txt", "base\n", "seed conflict fixture")
                            sandbox.Branch "feature"
                            sandbox.Checkout "feature"
                            sandbox.CommitFile("a.txt", "feature change\n", "feature")
                            sandbox.Checkout "main"
                            sandbox.CommitFile("a.txt", "main change\n", "main")

                            try
                                sandbox.Git [ "merge"; "-q"; "--no-edit"; "feature" ]
                            with _ ->
                                // The fixture deliberately leaves the merge unresolved so commit is denied.
                                ()
                        | "gitea-git" ->
                            sandbox.CommitFile("a.txt", "content\n", "seed Gitea fixture")
                            sandbox.Git [ "remote"; "add"; "origin"; "https://gitea.example/owner/project.git" ]
                        | "cancellation-git" ->
                            sandbox.CommitFile("a.txt", "content\n", "seed cancellation fixture")
                            sandbox.Write("a.txt", "selected change must remain uncommitted\n")

                            match blockingRemote with
                            | Some remote -> sandbox.Git [ "remote"; "add"; "origin"; remote.Url ]
                            | None -> failwith "cancellation replay requires a blocking remote"
                        | "large-changes-git" ->
                            for index in 0..39 do
                                sandbox.Write($"untracked-outcome-{index:D2}.txt", "changed\n")
                        | other -> failwith $"unknown outcome corpus configuration: {other}"

                    let outputBudget =
                        if configuration = "large-changes-git" then
                            Agent.MinimumOutputLimitBytes
                        else
                            65_536

                    let extraArgs =
                        [ "--allow-write"; "--output-budget"; string outputBudget ]
                        @ if configuration = "gitea-git" then
                              [ "--forge"; "gitea" ]
                          else
                              []

                    e2eWithSandbox extraArgs prepare (fun sandbox client ct ->
                        task {
                            let revision = sandbox.RevParse "HEAD"
                            let cliScenario = scenario.GetProperty "cli"

                            let argv =
                                cliScenario.GetProperty("argv").EnumerateArray()
                                |> Seq.map (fun argument ->
                                    match argument.GetString() with
                                    | null -> failwith $"{id}: CLI argv contains null"
                                    | value -> replacePlaceholders sandbox.Path revision value)
                                |> Seq.toArray

                            let operationName = argv |> Array.head

                            let skillCommands =
                                skillRoot.GetProperty("commands").EnumerateArray()
                                |> Seq.map (fun command ->
                                    match command.GetString() with
                                    | null -> failwith "Skill command is null"
                                    | value -> value)
                                |> Set.ofSeq

                            Assert.That(
                                skillCommands.Contains operationName,
                                Is.True,
                                $"{id}: command is absent from the Skill"
                            )

                            let skillOptions =
                                skillRoot.GetProperty("requiredOptions").GetProperty(operationName).EnumerateArray()
                                |> Seq.map (fun option ->
                                    match option.GetString() with
                                    | null -> failwith $"{id}: Skill option is null"
                                    | value -> value)
                                |> Set.ofSeq

                            let cliOptions =
                                argv |> Seq.filter _.StartsWith("--", StringComparison.Ordinal) |> Set.ofSeq

                            Assert.That(
                                (cliOptions = skillOptions),
                                Is.True,
                                $"{id}: corpus argv must use the complete Skill-prescribed option surface"
                            )

                            use cliCancellation = new CancellationTokenSource()

                            match requiredString cliScenario "cancellation" with
                            | "none" -> ()
                            | "pre-cancelled" -> cliCancellation.Cancel()
                            | other -> failwith $"{id}: unknown Skill cancellation mode: {other}"

                            let! cli = runAgentCli agentAssembly argv cliCancellation.Token

                            let argumentsJson =
                                scenario.GetProperty("mcpArguments").GetRawText()
                                |> replacePlaceholders sandbox.Path revision

                            use argumentsDocument = JsonDocument.Parse argumentsJson
                            let arguments = Dictionary<string, JsonElement>()

                            for property in argumentsDocument.RootElement.EnumerateObject() do
                                arguments[property.Name] <- property.Value.Clone()

                            let request =
                                CallToolRequestParams(Name = requiredString scenario "mcpTool", Arguments = arguments)

                            let requestId = RequestId($"outcome-corpus-{id}")

                            let mutationStateBefore =
                                match blockingRemote with
                                | Some _ ->
                                    let head = sandbox.RevParse "HEAD"
                                    sandbox.Git [ "diff"; "--quiet"; "--cached"; "--"; "a.txt" ]
                                    let selectedPathContent = File.ReadAllText(Path.Combine(sandbox.Path, "a.txt"))
                                    Some(head, selectedPathContent)
                                | None -> None

                            let startMcpCall () =
                                client
                                    .SendRequestAsync<CallToolRequestParams, CallToolResult>(
                                        RequestMethods.ToolsCall,
                                        request,
                                        McpJsonUtilities.DefaultOptions,
                                        requestId,
                                        CancellationToken.None
                                    )
                                    .AsTask()

                            let! mcpResult =
                                task {
                                    match blockingRemote with
                                    | None ->
                                        let! result = startMcpCall ()
                                        return Some result
                                    | Some remote ->
                                        return!
                                            callCancelledToolOverRawStdio
                                                extraArgs
                                                sandbox.Path
                                                remote
                                                request.Name
                                                argumentsDocument.RootElement
                                                (requestId.ToString())
                                                revision
                                                ct
                                }

                            match mcpResult with
                            | Some result ->
                                Assert.That(
                                    requiredString scenario "mcpTransportOutcome",
                                    Is.EqualTo "call-tool-result",
                                    id
                                )

                                Assert.That(
                                    isError result,
                                    Is.False,
                                    $"{id}: Agent outcome is a successful tool result"
                                )

                                Assert.That(
                                    result.Content.Count,
                                    Is.EqualTo 1,
                                    $"{id}: exact CallToolResult content shape"
                                )

                                Assert.That(
                                    result.StructuredContent.HasValue,
                                    Is.False,
                                    $"{id}: outcome is carried only by the canonical text envelope"
                                )

                                let mcp = textOf result

                                Assert.That(
                                    normalizeJson mcp,
                                    Is.EqualTo(normalizeJson cli.Stdout),
                                    $"{id}: complete normalized CLI and MCP stdio envelopes diverged"
                                )
                            | None ->
                                Assert.That(id, Is.EqualTo "cancellation")

                                Assert.That(
                                    requiredString scenario "mcpTransportOutcome",
                                    Is.EqualTo "request-cancelled",
                                    id
                                )

                                match mutationStateBefore with
                                | Some(headBefore, selectedPathContentBefore) ->
                                    Assert.That(
                                        sandbox.RevParse "HEAD",
                                        Is.EqualTo headBefore,
                                        "cancelled agent_commit must not create a commit"
                                    )

                                    sandbox.Git [ "diff"; "--quiet"; "--cached"; "--"; "a.txt" ]

                                    Assert.That(
                                        File.ReadAllText(Path.Combine(sandbox.Path, "a.txt")),
                                        Is.EqualTo selectedPathContentBefore,
                                        "cancelled agent_commit must not alter the selected path"
                                    )
                                | None -> Assert.Fail "cancellation replay did not capture mutation state"

                            use actual = JsonDocument.Parse cli.Stdout
                            let root = actual.RootElement
                            let errorCode = requiredString scenario "errorCode"
                            let expectedExit = scenario.GetProperty("expectedExit").GetInt32()

                            Assert.That(cliScenario.GetProperty("expectedError").GetString(), Is.EqualTo errorCode, id)

                            Assert.That(
                                cliScenario.GetProperty("expectedExit").GetInt32(),
                                Is.EqualTo expectedExit,
                                id
                            )

                            Assert.That(
                                skillRoot.GetProperty("errorExits").GetProperty(errorCode).GetInt32(),
                                Is.EqualTo expectedExit,
                                id
                            )

                            Assert.That(cli.ExitCode, Is.EqualTo expectedExit, id)
                            Assert.That(cli.Stderr, Is.EqualTo($"vcs-agent: {errorCode}\n"), id)

                            Assert.That(
                                root.GetProperty("operation").GetString(),
                                Is.EqualTo(scenario.GetProperty("operation").GetString()),
                                id
                            )

                            Assert.That(
                                root.GetProperty("status").GetString(),
                                Is.EqualTo(scenario.GetProperty("status").GetString()),
                                id
                            )

                            Assert.That(
                                root.GetProperty("terminal").GetBoolean(),
                                Is.EqualTo(scenario.GetProperty("terminal").GetBoolean()),
                                id
                            )

                            Assert.That(
                                root.GetProperty("error").GetProperty("code").GetString(),
                                Is.EqualTo errorCode,
                                id
                            )
                        })

                for scenario in scenarios do
                    if requiredString scenario "id" = "cancellation" then
                        use blockingRemote = new BlockingHttpServer()

                        try
                            do! replayScenario scenario (Some blockingRemote)
                        finally
                            blockingRemote.Release()
                    else
                        do! replayScenario scenario None
        }

    /// A file sink whose parent directory does not exist is a normal startup failure: the
    /// executable reports it on stderr and exits without exposing an exception stack trace.
    [<Test>]
    member _.UnavailableLogCommandsPathReportsStartupError() =
        match resolveBinary () with
        | None -> Assert.Ignore "vcs-mcp build output not found next to the test assembly (server project not built)"
        | Some launch ->
            use workingDir = new TempDir("mcp-log-open-failure")

            let logPath = Path.Combine(workingDir.Path, "missing-parent", "commands.log")

            let startInfo = ProcessStartInfo()
            startInfo.FileName <- launch.Command
            startInfo.WorkingDirectory <- workingDir.Path
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardInput <- true
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true

            for arg in launch.PrefixArgs @ [ "--repo"; workingDir.Path; "--log-commands"; logPath ] do
                startInfo.ArgumentList.Add arg

            use proc = new Process()
            proc.StartInfo <- startInfo
            Assert.That(proc.Start(), Is.True, "vcs-mcp process must start")
            proc.StandardInput.Close()

            let stdoutTask = proc.StandardOutput.ReadToEndAsync()
            let stderrTask = proc.StandardError.ReadToEndAsync()

            if not (proc.WaitForExit(10_000)) then
                proc.Kill(entireProcessTree = true)
                Assert.Fail "vcs-mcp did not exit after the command-log file failed to open"

            let stdout = stdoutTask.GetAwaiter().GetResult()
            let stderr = stderrTask.GetAwaiter().GetResult()

            let stderrLines =
                stderr.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)

            Assert.That(proc.ExitCode, Is.EqualTo 1)
            Assert.That(stdout, Is.Empty)
            Assert.That(stderrLines.Length, Is.EqualTo 1, stderr)
            Assert.That(stderrLines[0], Does.StartWith "vcs-mcp: ")
            Assert.That(stderrLines[0], Does.Contain logPath)
            Assert.That(stderr, Does.Not.Contain "Unhandled exception")
