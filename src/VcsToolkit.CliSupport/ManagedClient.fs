namespace VcsToolkit.CliSupport

open System
open System.Threading
open System.Threading.Tasks
open ProcessKit

[<NoEquality; NoComparison>]
type private ManagedConfig =
    { Program: string
      Runner: IProcessRunner
      DefaultTimeout: TimeSpan option
      DefaultEnv: (string * string) list
      EnvRemove: string list
      Cancel: CancellationToken
      Retry: RetryPolicy
      Credentials: ICredentialProvider option
      TokenEnv: (CredentialService * string) option
      ExpectedHost: string option
      Observer: ICommandObserver option }

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
          DefaultEnv = []
          EnvRemove = []
          Cancel = CancellationToken.None
          Retry = RetryPolicy.None
          Credentials = None
          TokenEnv = None
          ExpectedHost = None
          Observer = None }

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

    /// Apply a default timeout to every command this client builds.
    member _.DefaultTimeout(timeout: TimeSpan) =
        ManagedClient
            { cfg with
                DefaultTimeout = Some timeout }

    /// Set an environment variable on every command this client builds.
    member _.DefaultEnv(key: string, value: string) =
        ManagedClient
            { cfg with
                DefaultEnv = cfg.DefaultEnv @ [ (key, value) ] }

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
            let argv = List.ofSeq prepared.Arguments
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
    /// content that must round-trip verbatim. Credential injection applied; no lock-retry.
    member this.OutputBytes(cmd: Command) : Task<Result<ProcessResult<byte[]>, ProcessError>> =
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
