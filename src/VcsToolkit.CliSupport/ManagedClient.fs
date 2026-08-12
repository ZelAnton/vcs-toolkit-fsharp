namespace VcsToolkit.CliSupport

open System
open System.Threading
open System.Threading.Tasks
open ProcessKit

[<RequireQualifiedAccess>]
type ProcessEvent =
    | Started of pid: int option
    | Stdout of line: string
    | Stderr of line: string
    | Exited of outcome: Outcome

    member this.Name =
        match this with
        | ProcessEvent.Started _ -> "started"
        | ProcessEvent.Stdout _ -> "stdout"
        | ProcessEvent.Stderr _ -> "stderr"
        | ProcessEvent.Exited _ -> "exited"

    member this.Text =
        match this with
        | ProcessEvent.Stdout line
        | ProcessEvent.Stderr line -> Some line
        | ProcessEvent.Started _
        | ProcessEvent.Exited _ -> None

type ProgressCallback = ProcessEvent -> unit

[<NoEquality; NoComparison>]
type private ManagedConfig =
    { Program: string
      Runner: IProcessRunner
      DefaultTimeout: TimeSpan option
      DefaultInactivityTimeout: TimeSpan option
      DefaultEnv: (string * string) list
      EnvRemove: string list
      Cancel: CancellationToken
      Retry: RetryPolicy
      Credentials: ICredentialProvider option
      TokenEnv: (CredentialService * string) option
      ExpectedHost: string option
      Observer: ICommandObserver option
      OutputBuffer: OutputBufferPolicy
      OutputBudget: int option }

module private GracefulCancellation =

    let private complete
        (program: string)
        (grace: TimeSpan)
        (cancellationToken: CancellationToken)
        (running: RunningProcess)
        (capture: unit -> Task<Result<'T, ProcessError>>)
        : Task<Result<'T, ProcessError>> =
        task {
            use _registration =
                cancellationToken.Register(
                    Action(fun () ->
                        let stop = running.StopAsync grace

                        stop.ContinueWith(
                            Action<Task<Outcome>>(fun completed ->
                                if completed.IsFaulted then
                                    completed.Exception |> ignore),
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default
                        )
                        |> ignore)
                )

            let! result = capture ()

            if cancellationToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled program)
            else
                return result
        }

    let private capture
        (inner: IProcessRunner)
        (grace: TimeSpan)
        (command: Command)
        (cancellationToken: CancellationToken)
        (capture: RunningProcess -> Task<Result<'T, ProcessError>>)
        : Task<Result<'T, ProcessError>> =
        task {
            if cancellationToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled command.Program)
            else
                match! inner.SpawnAsync(command, CancellationToken.None) with
                | Error error -> return Error error
                | Ok running ->
                    use running = running
                    return! complete command.Program grace cancellationToken running (fun () -> capture running)
        }

    let runner (inner: IProcessRunner) (grace: TimeSpan) : IProcessRunner =
        ArgumentOutOfRangeException.ThrowIfLessThan(grace, TimeSpan.Zero, nameof grace)

        { new IProcessRunner with
            member _.CaptureStringAsync(command, cancellationToken) =
                capture inner grace command cancellationToken (fun runningProcess -> runningProcess.OutputStringAsync())

            member _.CaptureBytesAsync(command, cancellationToken) =
                capture inner grace command cancellationToken (fun runningProcess -> runningProcess.OutputBytesAsync())

            member _.SpawnAsync(command, cancellationToken) =
                inner.SpawnAsync(command, cancellationToken) }

module private ManagedParsing =

    let parse
        (program: string)
        (parser: string -> 'T)
        (runText: unit -> Task<Result<string, ProcessError>>)
        : Task<Result<'T, ProcessError>> =
        task {
            match! runText () with
            | Error error -> return Error error
            | Ok text ->
                try
                    return Ok(parser text)
                with ex ->
                    return Error(ProcessError.Parse(program, ex.Message))
        }

    let tryParse
        (program: string)
        (parser: string -> Result<'T, string>)
        (runText: unit -> Task<Result<string, ProcessError>>)
        : Task<Result<'T, ProcessError>> =
        task {
            match! runText () with
            | Error error -> return Error error
            | Ok text ->
                try
                    match parser text with
                    | Ok value -> return Ok value
                    | Error message -> return Error(ProcessError.Parse(program, message))
                with ex ->
                    return Error(ProcessError.Parse(program, ex.Message))
        }

/// A ProcessKit-runner wrapper that adds three opt-in concerns the CLI wrappers all
/// share, without touching a call site: lock-contention retry per a `RetryPolicy`
/// (off by default) on `Run`, `RunUnit`, `Probe`, `ExitCode`, `Parse`, and `TryParse`;
/// credential injection from an opt-in `ICredentialProvider` (off by default →
/// ambient auth); and a diagnostic `ICommandObserver` (off by default) notified around
/// every spawned command. With none configured it behaves like a bare runner. The default
/// constructor drives the real job-backed `JobRunner`; pass a `ScriptedRunner` via
/// `WithRunner` to inject a fake in tests.
[<Sealed>]
type ManagedClient private (cfg: ManagedConfig) =

    static let initial program runner =
        { Program = program
          Runner = runner
          DefaultTimeout = None
          DefaultInactivityTimeout = None
          DefaultEnv = []
          EnvRemove = []
          Cancel = CancellationToken.None
          Retry = RetryPolicy.None
          Credentials = None
          TokenEnv = None
          ExpectedHost = None
          Observer = None
          OutputBuffer = OutputBufferPolicy.Default
          OutputBudget = None }

    /// A client driving `program` on the real job-backed runner (no retry until `WithRetry`).
    static member Create(program: string) =
        ManagedClient(initial program (JobRunner()))

    /// A client driving `program` on `runner` — inject a fake in tests.
    static member WithRunner(program: string, runner: IProcessRunner) = ManagedClient(initial program runner)

    /// The underlying process runner (passthrough).
    member _.Runner = cfg.Runner

    /// The active retry policy.
    member _.RetryPolicy = cfg.Retry

    /// Whether a credential provider is configured.
    member _.HasCredentials = cfg.Credentials.IsSome

    /// Set the lock-contention retry policy (opt-in; default is no retry) for all
    /// zero-exit methods. `Parse` and `TryParse` retry process execution only; each
    /// parser runs once after a successful output is available.
    member _.WithRetry(policy: RetryPolicy) =
        ManagedClient { cfg with Retry = policy }

    /// Attach a credential provider (opt-in; default is none → ambient auth).
    member _.WithCredentials(provider: ICredentialProvider) =
        ManagedClient { cfg with Credentials = Some provider }

    /// Bind the resolved token to an environment variable injected on every command
    /// this client runs (the forge case: `GH_TOKEN`, `GITLAB_TOKEN`).
    member _.WithTokenEnv(service: CredentialService, var: string) =
        ManagedClient
            { cfg with
                TokenEnv = Some(service, var) }

    /// Bind the known target host of this client's operations (e.g. a configured forge host).
    /// It becomes the `CredentialRequest.Host` on the token-env injection path — so a host-keyed
    /// provider can pick the secret for *this* host instead of always being asked with `None`.
    /// A blank host is treated as no binding (stays unscoped). This scopes only the token-env
    /// path; `git` remote operations carry the per-operation host explicitly (see the `Git`
    /// client), so this binding never overrides a resolve that already knows its host.
    member _.WithExpectedHost(host: string) =
        ManagedClient
            { cfg with
                ExpectedHost = if String.IsNullOrWhiteSpace host then None else Some host }

    /// Attach a diagnostic observer (opt-in; default is none). It is notified as each command
    /// this client spawns starts and finishes — once per retry attempt — carrying the program,
    /// argv, working directory, attempt index, duration, and exit code or classified error.
    /// Secret values never reach it (see `CommandEvent`). With no observer configured the run
    /// path is exactly as before (no allocation, no extra work).
    member _.WithObserver(observer: ICommandObserver) =
        ManagedClient { cfg with Observer = Some observer }

    /// Bound the in-memory output retained by every command this client builds. The process
    /// pump still drains the child pipe, so a chatty child cannot deadlock; the policy keeps the
    /// prefix because MCP response budgets are prefix-oriented. `None` and non-positive values
    /// restore the unbounded default.
    member _.WithOutputBudget(bytes: int option) =
        let budget =
            match bytes with
            | Some value when value > 0 -> Some value
            | _ -> None

        let policy =
            match budget with
            | Some value when value > 0 ->
                // Keep a bounded diagnostic framing allowance so the MCP layer can still see
                // enough complete text/JSON to add its response envelope and truncation marker.
                // The child pipe is drained; this is a retained-capture ceiling, not backpressure.
                let allowance = 65_536

                let captureLimit =
                    if value > Int32.MaxValue - allowance then
                        Int32.MaxValue
                    else
                        value + allowance

                OutputBufferPolicy.Default.WithMaxBytes(captureLimit).WithOverflow(OverflowMode.DropNewest)
            | _ -> OutputBufferPolicy.Default

        ManagedClient
            { cfg with
                OutputBuffer = policy
                OutputBudget = budget }

    /// Whether this client has opted into the bounded text-capture path used by MCP.
    /// This stays internal because it only selects an implementation detail of the shared
    /// untrimmed-output plumbing; the public client surface remains `WithOutputBudget`.
    member internal _.HasOutputBudget = cfg.OutputBudget.IsSome

    /// Apply a default timeout to every command this client builds.
    member _.DefaultTimeout(timeout: TimeSpan) =
        ManagedClient
            { cfg with
                DefaultTimeout = Some timeout }

    /// Set the resettable output-inactivity window for streamed commands. The default is disabled;
    /// output on either stdout or stderr resets the window while a progress run is active.
    member _.DefaultInactivityTimeout(timeout: TimeSpan) =
        ManagedClient
            { cfg with
                DefaultInactivityTimeout = Some timeout }

    /// Set an environment variable on every command this client builds.
    member _.DefaultEnv(key: string, value: string) =
        ManagedClient
            { cfg with
                DefaultEnv = cfg.DefaultEnv @ [ (key, value) ]
                // A later explicit default wins over an earlier removal for the same
                // environment key; DefaultEnvRemove still wins when it is called later.
                EnvRemove =
                    cfg.EnvRemove
                    |> List.filter (fun removed ->
                        not (String.Equals(removed, key, StringComparison.OrdinalIgnoreCase))) }

    /// Remove an inherited environment variable on every command this client builds.
    member _.DefaultEnvRemove(key: string) =
        ManagedClient
            { cfg with
                EnvRemove = cfg.EnvRemove @ [ key ] }

    /// Cancel every command this client builds when `token` fires.
    member _.DefaultCancelOn(token: CancellationToken) =
        ManagedClient { cfg with Cancel = token }

    member private _.ApplyDefaults(cmd: Command) : Command =
        let mutable c = cmd

        match cfg.DefaultTimeout with
        | Some t -> c <- c.Timeout t
        | None -> ()

        for (k, v) in cfg.DefaultEnv do
            c <- c.Env(k, v)

        for k in cfg.EnvRemove do
            c <- c.EnvRemove k

        c <- c.OutputBuffer cfg.OutputBuffer

        c.CancelOn cfg.Cancel

    /// Build a `Command` for this client's program (defaults applied).
    member this.Command(args: string seq) : Command =
        this.ApplyDefaults(Command(cfg.Program).Args args)

    /// Build a `Command` bound to `dir` (defaults applied).
    member this.CommandIn(dir: string, args: string seq) : Command =
        this.ApplyDefaults(Command(cfg.Program).CurrentDir(dir).Args args)

    /// Resolve a credential for `service`/`host` from the configured provider. The `host` is
    /// passed through verbatim into the `CredentialRequest` (never silently overridden), so a
    /// host-keyed provider selects the secret for exactly that host. The fallback policy:
    /// no provider configured → `Ok None` (ambient auth); the provider returns `Ok None` →
    /// ambient; the provider returns a credential whose secret is empty/whitespace → ambient
    /// (injecting it would override the ambient login with nothing); the provider returns
    /// `Error` → the `Error` propagates (fail-closed — the caller must abort, not degrade to
    /// ambient silently).
    member _.ResolveCredential
        (service: CredentialService, host: string option)
        : Task<Result<Credential option, ProcessError>> =
        task {
            match cfg.Credentials with
            | None -> return Ok None
            | Some provider ->
                match! provider.Credential { Service = service; Host = host } with
                | Error e -> return Error e
                | Ok None -> return Ok None
                | Ok(Some cred) ->
                    match CredentialValidation.validate cred with
                    | Error e -> return Error e
                    | Ok() ->
                        // An empty (or whitespace-only) secret is not a usable credential:
                        // injecting it would override the ambient login with nothing.
                        if cred.Secret.Expose().Trim().Length = 0 then
                            return Ok None
                        else
                            return Ok(Some cred)
        }

    /// Inject the forge token env (if a token-env binding and a provider are both set). The
    /// resolve carries the client-bound `ExpectedHost` (from `WithExpectedHost`) as the request
    /// host — the token-env path has no per-operation host of its own — so a host-keyed provider
    /// serves the secret for this client's host rather than always being asked with `None`.
    ///
    /// Returns the prepared command paired with a flag that is `true` only when a secret was
    /// actually injected into it — the observer reports that fact (never the value); see `Wrap`.
    member private this.Prepare(cmd: Command) : Task<Result<Command * bool, ProcessError>> =
        task {
            match cfg.TokenEnv with
            | None -> return Ok(cmd, false)
            | Some(service, var) ->
                match! this.ResolveCredential(service, cfg.ExpectedHost) with
                | Error e -> return Error e
                | Ok None -> return Ok(cmd, false)
                | Ok(Some cred) -> return Ok(cmd.Env(var, cred.Secret.Expose()), true)
        }

    /// Instrument one run `op` with the diagnostic observer (if configured): emit a `started`
    /// event before each attempt and a `finished` event after, carrying the command's identity
    /// (program/argv/cwd — never its environment, so no secret leaks), the 0-based `Attempt`
    /// index, the measured duration, and the outcome (`codeOf` maps a success value to the exit
    /// code; a failure carries its `ProcessError`). Returned as a `unit -> Task<…>` so it slots
    /// straight into `Retry.retryAsync` and is re-invoked (with an incremented attempt) per retry;
    /// the non-retrying verbs invoke it once. With no observer configured it returns `op`
    /// unchanged — zero added work on the hot path.
    member private _.Wrap
        (prepared: Command)
        (hasSecret: bool)
        (codeOf: 'T -> int)
        (op: unit -> Task<Result<'T, ProcessError>>)
        : unit -> Task<Result<'T, ProcessError>> =
        match cfg.Observer with
        | None -> op
        | Some obs ->
            // Capture the command identity once — it is stable across retry attempts. `Arguments`
            // is the guarded/credential-helper argv, which by construction holds no secret value.
            let program = prepared.Program
            let argv = prepared.Arguments |> List.ofSeq |> CommandRedaction.argv
            let cwd = prepared.WorkingDirectory
            let attempt = ref 0

            fun () ->
                task {
                    let n = attempt.Value
                    attempt.Value <- n + 1

                    let ev =
                        { Program = program
                          Argv = argv
                          WorkingDirectory = cwd
                          Attempt = n
                          HasSecret = hasSecret }

                    Observer.started obs ev
                    let sw = System.Diagnostics.Stopwatch.StartNew()
                    let! result = op ()
                    sw.Stop()

                    let outcome =
                        match result with
                        | Ok value -> Ok(codeOf value)
                        | Error err -> Error err

                    Observer.finished obs ev sw.Elapsed outcome
                    return result
                }

    /// Require a zero exit and return stdout (trimmed), with credential injection and lock-retry.
    member this.Run(cmd: Command) : Task<Result<string, ProcessError>> =
        task {
            match! this.Prepare cmd with
            | Error e -> return Error e
            | Ok(prepared, hasSecret) ->
                return!
                    Retry.retryAsync
                        cfg.Retry
                        isLockContention
                        cfg.Cancel
                        (this.Wrap prepared hasSecret (fun _ -> 0) (fun () -> Runner.run cfg.Runner cfg.Cancel prepared))
        }

    /// Like `Run`, discarding the output.
    member this.RunUnit(cmd: Command) : Task<Result<unit, ProcessError>> =
        task {
            match! this.Prepare cmd with
            | Error e -> return Error e
            | Ok(prepared, hasSecret) ->
                return!
                    Retry.retryAsync
                        cfg.Retry
                        isLockContention
                        cfg.Cancel
                        (this.Wrap prepared hasSecret (fun _ -> 0) (fun () ->
                            Runner.runUnit cfg.Runner cfg.Cancel prepared))
        }

    /// Like `RunUnit`, but cancellation asks the running process to stop gracefully before escalation.
    member this.RunUnitWithCancellationGrace(cmd: Command, grace: TimeSpan) : Task<Result<unit, ProcessError>> =
        task {
            match! this.Prepare cmd with
            | Error e -> return Error e
            | Ok(prepared, hasSecret) ->
                let gracefulRunner = GracefulCancellation.runner cfg.Runner grace

                return!
                    Retry.retryAsync
                        cfg.Retry
                        isLockContention
                        cfg.Cancel
                        (this.Wrap prepared hasSecret (fun _ -> 0) (fun () ->
                            Runner.runUnit gracefulRunner cfg.Cancel prepared))
        }

    /// Run one command while forwarding a best-effort lifecycle and output stream to `progress`.
    /// The command is executed exactly once: replaying a partially observed network operation
    /// would make the event stream ambiguous, so callers decide whether to retry after `Exited`.
    member private this.RunWithProgressCore
        (cmd: Command, progress: ProgressCallback, cancellationGrace: TimeSpan option)
        : Task<Result<unit, ProcessError>> =
        task {
            match! this.Prepare cmd with
            | Error e -> return Error e
            | Ok(prepared, hasSecret) ->
                let mutable callbackActive = true

                let report event =
                    if callbackActive then
                        try
                            progress event
                        with _ ->
                            // A progress consumer is observational; disable a failing callback so
                            // its exception cannot strand the child or stop output draining.
                            callbackActive <- false

                let outputCommand =
                    match cfg.OutputBudget with
                    | Some value ->
                        let allowance = 65_536

                        let captureLimit =
                            if value > Int32.MaxValue - allowance then
                                Int32.MaxValue
                            else
                                value + allowance

                        let policy =
                            OutputBufferPolicy.Default.WithMaxBytes(captureLimit).WithOverflow(OverflowMode.DropOldest)

                        prepared.OutputBuffer policy
                    | None -> prepared
                let progressCommand =
                    match cfg.DefaultInactivityTimeout with
                    | Some timeout -> outputCommand.IdleTimeout timeout
                    | None -> outputCommand
                let streamed =
                    progressCommand
                        .OnStdoutLine(Action<string>(fun line -> report (ProcessEvent.Stdout line)))
                        .OnStderrLine(Action<string>(fun line -> report (ProcessEvent.Stderr line)))

                report (ProcessEvent.Started None)

                let runner =
                    match cancellationGrace with
                    | Some grace -> GracefulCancellation.runner cfg.Runner grace
                    | None -> cfg.Runner

                let! result =
                    this.Wrap
                        progressCommand
                        hasSecret
                        (fun (r: ProcessResult<string>) -> r.Code |> Option.defaultValue 0)
                        (fun () -> Runner.outputString runner cfg.Cancel streamed)
                        ()

                match result with
                | Error e -> return Error e
                | Ok processResult ->
                    report (ProcessEvent.Exited processResult.Outcome)

                    match ProcessResult.ensureSuccess processResult with
                    | Error e -> return Error e
                    | Ok _ -> return Ok()
        }

    /// Run one command while forwarding a progress stream and allowing cancellation to stop it
    /// gracefully before a hard kill.
    member this.RunWithProgressWithCancellationGrace
        (cmd: Command, progress: ProgressCallback, grace: TimeSpan)
        : Task<Result<unit, ProcessError>> =
        this.RunWithProgressCore(cmd, progress, Some grace)

    /// Run one command while forwarding a best-effort lifecycle and output stream to `progress`.
    member this.RunWithProgress(cmd: Command, progress: ProgressCallback) : Task<Result<unit, ProcessError>> =
        this.RunWithProgressCore(cmd, progress, None)

    /// Capture the full `ProcessResult` (a non-zero exit is data). Credential injection
    /// applied; no lock-retry (a lock failure surfaces as an `Ok` here, not an error).
    member this.Output(cmd: Command) : Task<Result<ProcessResult<string>, ProcessError>> =
        task {
            match! this.Prepare cmd with
            | Error e -> return Error e
            | Ok(prepared, hasSecret) ->
                return!
                    this.Wrap
                        prepared
                        hasSecret
                        (fun (r: ProcessResult<string>) -> r.Code |> Option.defaultValue 0)
                        (fun () -> Runner.outputString cfg.Runner cfg.Cancel prepared)
                        ()
        }

    /// Capture the full `ProcessResult` with stdout as **raw bytes** — byte-exact, unlike `Output`,
    /// whose string capture reconstructs from lines and drops the trailing newline. For blob/diff
    /// content that must round-trip verbatim. Credential injection applied; no lock-retry. A
    /// budgeted client refuses this escape hatch because ProcessKit's raw-byte capture bypasses
    /// `OutputBufferPolicy`; the shared untrimmed wrapper selects bounded text capture instead.
    member this.OutputBytes(cmd: Command) : Task<Result<ProcessResult<byte[]>, ProcessError>> =
        if cfg.OutputBudget.IsSome then
            task {
                return
                    Error(
                        ProcessError.Spawn(
                            cfg.Program,
                            "raw-byte capture cannot be combined with an output budget; use bounded text capture"
                        )
                    )
            }
        else
            task {
                match! this.Prepare cmd with
                | Error e -> return Error e
                | Ok(prepared, hasSecret) ->
                    return!
                        this.Wrap
                            prepared
                            hasSecret
                            (fun (r: ProcessResult<byte[]>) -> r.Code |> Option.defaultValue 0)
                            (fun () -> Runner.outputBytes cfg.Runner cfg.Cancel prepared)
                            ()
            }

    /// Read the exit code as a yes/no (0 -> true, 1 -> false), with credential injection and lock-retry.
    member this.Probe(cmd: Command) : Task<Result<bool, ProcessError>> =
        task {
            match! this.Prepare cmd with
            | Error e -> return Error e
            | Ok(prepared, hasSecret) ->
                return!
                    Retry.retryAsync
                        cfg.Retry
                        isLockContention
                        cfg.Cancel
                        (this.Wrap prepared hasSecret (fun ok -> if ok then 0 else 1) (fun () ->
                            Runner.probe cfg.Runner cfg.Cancel prepared))
        }

    /// The raw exit code, with credential injection and lock-retry.
    member this.ExitCode(cmd: Command) : Task<Result<int, ProcessError>> =
        task {
            match! this.Prepare cmd with
            | Error e -> return Error e
            | Ok(prepared, hasSecret) ->
                return!
                    Retry.retryAsync
                        cfg.Retry
                        isLockContention
                        cfg.Cancel
                        (this.Wrap prepared hasSecret id (fun () -> Runner.exitCode cfg.Runner cfg.Cancel prepared))
        }

    /// Require a zero exit and parse the trimmed stdout (credential injection and lock-retry applied).
    member this.Parse(cmd: Command, parser: string -> 'T) : Task<Result<'T, ProcessError>> =
        task {
            match! this.Prepare cmd with
            | Error e -> return Error e
            | Ok(prepared, hasSecret) ->
                // The observer wraps the process execution only (inside the retry loop); the parser
                // still runs once, after a successful output — its work is not a command execution.
                let runText () =
                    Retry.retryAsync
                        cfg.Retry
                        isLockContention
                        cfg.Cancel
                        (this.Wrap prepared hasSecret (fun _ -> 0) (fun () -> Runner.run cfg.Runner cfg.Cancel prepared))

                return! ManagedParsing.parse cfg.Program parser runText
        }

    /// Like `Parse`, but the parser returns its own `Result` (credential injection and lock-retry applied).
    member this.TryParse(cmd: Command, parser: string -> Result<'T, string>) : Task<Result<'T, ProcessError>> =
        task {
            match! this.Prepare cmd with
            | Error e -> return Error e
            | Ok(prepared, hasSecret) ->
                let runText () =
                    Retry.retryAsync
                        cfg.Retry
                        isLockContention
                        cfg.Cancel
                        (this.Wrap prepared hasSecret (fun _ -> 0) (fun () -> Runner.run cfg.Runner cfg.Cancel prepared))

                return! ManagedParsing.tryParse cfg.Program parser runText
        }
