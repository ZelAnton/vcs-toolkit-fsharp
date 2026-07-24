module VcsToolkit.CliSupport.Tests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.CliSupport

let private exit (program: string) (code: int) (stderr: string) =
    ProcessError.Exit(program, code, "", stderr)

/// An `ICommandObserver` that records every start/finish notification for assertions.
type private RecordingObserver() =
    let started = System.Collections.Generic.List<CommandEvent>()

    let finished =
        System.Collections.Generic.List<CommandEvent * TimeSpan * Result<int, ProcessError>>()

    member _.Started = started
    member _.Finished = finished

    interface ICommandObserver with
        member _.OnStarted(command) = started.Add command

        member _.OnFinished(command, duration, outcome) =
            finished.Add((command, duration, outcome))

[<TestFixture>]
type GuardTests() =

    [<Test>]
    member _.RejectsEmptyAndLeadingDash() =
        Assert.That(rejectFlagLike "git" "branch name" "-evil" |> Result.isError)
        Assert.That(rejectFlagLike "git" "branch name" "" |> Result.isError)
        // Whitespace-only is as meaning-changing as empty — refuse it too.
        Assert.That(rejectFlagLike "git" "branch name" "  " |> Result.isError)
        Assert.That(rejectFlagLike "git" "branch name" "\t" |> Result.isError)
        Assert.That(rejectFlagLike "git" "branch name" "feature" |> Result.isOk)
        // Leading whitespace before a dash is still refused (the flag-check trims).
        Assert.That(rejectFlagLike "git" "remote" " --upload-pack=evil" |> Result.isError)
        // A leading-whitespace non-flag value is still accepted (not flag-like).
        Assert.That(rejectFlagLike "git" "branch name" "  feature" |> Result.isOk)

    [<Test>]
    member _.ErrorNamesTheProgram() =
        match rejectFlagLike "jj" "revset" "--remote" with
        | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "jj")
        | _ -> Assert.Fail "expected a Spawn error naming jj"

[<TestFixture>]
type ClassifierTests() =

    [<Test>]
    member _.ClassifiesMergeConflict() =
        let onStdout =
            ProcessError.Exit("git", 1, "CONFLICT (content): Merge conflict in a.fs", "")

        let onStderr = exit "git" 1 "Automatic merge failed; fix conflicts and then commit"
        let unrelated = exit "git" 128 "fatal: not a git repository"
        Assert.That(isMergeConflict onStdout)
        Assert.That(isMergeConflict onStderr)
        Assert.That(isMergeConflict unrelated, Is.False)
        Assert.That(isNothingToCommit onStdout, Is.False)

    [<Test>]
    member _.ClassifiesNothingToCommitAndTransientFetch() =
        let nothing =
            ProcessError.Exit("git", 1, "nothing to commit, working tree clean", "")

        Assert.That(isNothingToCommit nothing)

        let dns =
            exit "git" 128 "fatal: unable to access 'https://x/': Could not resolve host: x"

        Assert.That(isTransientFetchError dns)
        Assert.That(isTransientFetchError nothing, Is.False)

        // R6: a ProcessKit timeout is NOT a transient FETCH error — retrying a fetch that already
        // burned its whole deadline against a black-holed remote just multiplies the wait.
        let timeout = ProcessError.Timeout("git", TimeSpan.FromSeconds 10.0, "", "")
        Assert.That(isTransientFetchError timeout, Is.False)

    [<Test>]
    member _.ClassifiesLockContention() =
        let lockFailures =
            [ exit "git" 128 "fatal: Unable to create '/r/.git/index.lock': File exists."
              // jj's real wordings (no `the`; the full op-heads phrase).
              exit "jj" 1 "Error: Failed to lock working copy"
              exit "jj" 1 "Error: Failed to lock operation heads store" ]

        for e in lockFailures do
            Assert.That(isLockContention e, $"should be lock contention: {e}")
            Assert.That(isTransientFetchError e, Is.False, $"not a fetch error: {e}")

        let notLocks =
            [ exit "git" 1 "CONFLICT (content): Merge conflict in a.fs"
              exit "git" 128 "fatal: not a git repository"
              // Per-ref locks are NOT classified (a multi-ref op can fail one mid-way).
              exit "git" 1 "error: cannot lock ref 'refs/heads/x': reference already exists"
              // A per-ref lock whose PATH contains `index.lock` (a branch literally named
              // `index`) is excluded by the `refs/` guard — not the whole-repo index lock.
              exit "git" 128 "fatal: Unable to create '/r/.git/refs/heads/index.lock': File exists"
              ProcessError.Timeout("git", TimeSpan.FromSeconds 1.0, "", "") ]

        for e in notLocks do
            Assert.That(isLockContention e, Is.False, $"should NOT be lock contention: {e}")

    [<Test>]
    member _.UnfamiliarVariantsAreNotClassified() =
        let notReady = ProcessError.NotReady("git", TimeSpan.FromSeconds 5.0)
        let cancelled = ProcessError.Cancelled "git"

        for err in [ notReady; cancelled ] do
            Assert.That(isMergeConflict err, Is.False)
            Assert.That(isNothingToCommit err, Is.False)
            Assert.That(isTransientFetchError err, Is.False)

[<TestFixture>]
type RetryTests() =

    [<Test>]
    member _.RetriesThenSucceeds() : Task =
        task {
            let policy = RetryPolicy.None.WithAttempts 4
            let lockErr = exit "git" 128 "Unable to create '/r/.git/index.lock': File exists"
            let calls = ref 0

            let! out =
                Retry.retryAsync policy isLockContention CancellationToken.None (fun () ->
                    task {
                        let n = calls.Value
                        calls.Value <- n + 1
                        if n < 2 then return Error lockErr else return Ok n
                    })

            match out with
            | Ok v -> Assert.That(v, Is.EqualTo 2)
            | Error _ -> Assert.Fail "expected success after retries"

            Assert.That(calls.Value, Is.EqualTo 3, "1 try + 2 retries")
        }

    [<Test>]
    member _.NonRetryableErrorReturnsImmediately() : Task =
        task {
            let policy = RetryPolicy.None.WithAttempts 4
            let calls = ref 0

            let! out =
                Retry.retryAsync policy isLockContention CancellationToken.None (fun () ->
                    task {
                        calls.Value <- calls.Value + 1
                        return Error(exit "git" 1 "real, deterministic failure")
                    })

            Assert.That(Result.isError out)
            Assert.That(calls.Value, Is.EqualTo 1, "non-retryable -> single attempt")
        }

    [<Test>]
    member _.PersistentLockExhaustsAttempts() : Task =
        task {
            let policy = RetryPolicy.None.WithAttempts 4
            let calls = ref 0

            let! out =
                Retry.retryAsync policy isLockContention CancellationToken.None (fun () ->
                    task {
                        calls.Value <- calls.Value + 1
                        return Error(exit "git" 128 "index.lock': File exists")
                    })

            Assert.That(Result.isError out)
            Assert.That(calls.Value, Is.EqualTo 4, "all attempts used")
        }

    [<Test>]
    member _.CancellationDuringBackoffStopsRetryImmediately() : Task =
        task {
            // A backoff long enough that "sleeps it out" vs. "cancels immediately" is
            // unmistakable in the elapsed-time assertion below.
            let policy =
                RetryPolicy.None.WithAttempts(4).WithBaseBackoff(TimeSpan.FromSeconds 30.0)

            let lockErr = exit "git" 128 "Unable to create '/r/.git/index.lock': File exists"
            let calls = ref 0
            use cts = new CancellationTokenSource()
            // Cancelled up front: the first attempt must still run (observed only around the
            // backoff sleep), but the sleep before attempt 2 must fail fast on the already-fired
            // token rather than actually sleeping — deterministic, no timing race.
            cts.Cancel()

            let sw = System.Diagnostics.Stopwatch.StartNew()

            let! out =
                Retry.retryAsync policy isLockContention cts.Token (fun () ->
                    task {
                        calls.Value <- calls.Value + 1
                        return Error lockErr
                    })

            sw.Stop()

            match out with
            | Error(ProcessError.Cancelled program) -> Assert.That(program, Is.EqualTo "git")
            | _ -> Assert.Fail $"expected a Cancelled error, got {out}"

            Assert.That(calls.Value, Is.EqualTo 1, "cancelled during backoff -> no second attempt")

            Assert.That(
                sw.Elapsed,
                Is.LessThan(TimeSpan.FromSeconds 5.0),
                "cancellation must not sleep out the remaining backoff"
            )
        }

    [<Test>]
    member _.ParseRetriesLockContentionAndParsesOnlyTheSuccessfulOutput() : Task =
        task {
            let attempts = ref 0
            let parserCalls = ref 0
            let lockErr = exit "git" 128 "Unable to create '/r/.git/index.lock': File exists"

            let runner =
                ScriptedRunner()
                    .When(
                        (fun _ ->
                            attempts.Value <- attempts.Value + 1
                            attempts.Value = 1),
                        Reply.Error lockErr
                    )
                    .Fallback(Reply.Ok "42")

            let client =
                ManagedClient.WithRunner("git", runner).WithRetry(RetryPolicy.None.WithAttempts 2)

            let! result =
                client.Parse(
                    client.Command [ "rev-parse"; "--verify"; "HEAD" ],
                    fun output ->
                        parserCalls.Value <- parserCalls.Value + 1
                        Int32.Parse output
                )

            match result with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"expected successful retry, got {error}"

            Assert.That(attempts.Value, Is.EqualTo 2, "first lock failure and one successful retry")
            Assert.That(parserCalls.Value, Is.EqualTo 1, "parser runs only for the successful output")
        }

    [<Test>]
    member _.TryParseRetriesLockContentionAndParsesOnlyTheSuccessfulOutput() : Task =
        task {
            let attempts = ref 0
            let parserCalls = ref 0
            let lockErr = exit "git" 128 "Unable to create '/r/.git/index.lock': File exists"

            let runner =
                ScriptedRunner()
                    .When(
                        (fun _ ->
                            attempts.Value <- attempts.Value + 1
                            attempts.Value = 1),
                        Reply.Error lockErr
                    )
                    .Fallback(Reply.Ok "42")

            let client =
                ManagedClient.WithRunner("git", runner).WithRetry(RetryPolicy.None.WithAttempts 2)

            let! result =
                client.TryParse(
                    client.Command [ "rev-parse"; "--verify"; "HEAD" ],
                    fun output ->
                        parserCalls.Value <- parserCalls.Value + 1

                        match Int32.TryParse output with
                        | true, value -> Ok value
                        | false, _ -> Error "expected an integer"
                )

            match result with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"expected successful retry, got {error}"

            Assert.That(attempts.Value, Is.EqualTo 2, "first lock failure and one successful retry")
            Assert.That(parserCalls.Value, Is.EqualTo 1, "parser runs only for the successful output")
        }

[<TestFixture>]
type CredentialTests() =

    [<Test>]
    member _.SecretRedactsButExposes() =
        let s = Secret "hunter2"
        Assert.That(s.ToString(), Is.EqualTo "***")
        Assert.That(s.Expose(), Is.EqualTo "hunter2")

    [<Test>]
    member _.CredentialToStringRedactsSecretButShowsUsername() =
        // The secret must never appear in a rendered Credential; the username may.
        let up = Credential.Userpass("alice", "s3cr3t")
        let upText = up.ToString()
        Assert.That(upText.Contains "s3cr3t", Is.False, $"secret leaked: {upText}")
        Assert.That(upText.Contains "alice", $"username should render: {upText}")
        Assert.That(upText.Contains "***")

        let tok = Credential.Token "t0p-secret"
        Assert.That(tok.ToString().Contains "t0p-secret", Is.False, "token secret leaked")
        Assert.That(tok.ToString().Contains "***")

    [<Test>]
    member _.GitCredentialHelperKeepsSecretOutOfArgv() =
        let h =
            Credentials.gitCredentialHelper (Credential.Userpass("alice", "s3cr3t")) None

        for a in h.ConfigArgs do
            Assert.That(a.Contains "s3cr3t", Is.False, $"secret leaked into argv: {a}")

        Assert.That(h.ConfigArgs |> List.exists (fun a -> a.Contains "VCS_TOOLKIT_GIT_PASSWORD"))
        // A leading empty helper clears inherited helpers.
        Assert.That(h.ConfigArgs |> List.exists (fun a -> a = "credential.helper="))

        let _, pw = h.Env |> List.find (fun (k, _) -> k = "VCS_TOOLKIT_GIT_PASSWORD")
        Assert.That(pw.Expose(), Is.EqualTo "s3cr3t")
        let _, user = h.Env |> List.find (fun (k, _) -> k = "VCS_TOOLKIT_GIT_USERNAME")
        Assert.That(user.Expose(), Is.EqualTo "alice")

    [<Test>]
    member _.GitCredentialHelperScopesToHost() =
        // Unscoped: the host env is empty (the snippet's `test -z` gate passes).
        let unscoped = Credentials.gitCredentialHelper (Credential.Token "t0p-secret") None
        let _, h0 = unscoped.Env |> List.find (fun (k, _) -> k = "VCS_TOOLKIT_GIT_HOST")
        Assert.That(h0.Expose(), Is.EqualTo "")

        // Scoped: the host env carries the expected host (with port + case), and the snippet
        // gates the release on git's request host matching it — while the secret stays out of argv.
        let scoped =
            Credentials.gitCredentialHelper (Credential.Token "t0p-secret") (Some "GitHub.com:8443")

        let _, h1 = scoped.Env |> List.find (fun (k, _) -> k = "VCS_TOOLKIT_GIT_HOST")
        Assert.That(h1.Expose(), Is.EqualTo "GitHub.com:8443")
        // The snippet gates on the host env var by NAME; the raw secret never appears in argv.
        Assert.That(scoped.ConfigArgs |> List.exists (fun a -> a.Contains "VCS_TOOLKIT_GIT_HOST"))

        for a in scoped.ConfigArgs do
            Assert.That(a.Contains "t0p-secret", Is.False, $"secret leaked into argv: {a}")

    [<Test>]
    member _.HttpsHostExtractsHostWithPortAndCase() =
        Assert.That(Credentials.httpsHost "https://github.com/o/r.git", Is.EqualTo(Some "github.com"))
        // Port and case are kept (git's `host=` carries them; the snippet compares byte-for-byte).
        Assert.That(Credentials.httpsHost "https://GitHub.com:8443/o/r", Is.EqualTo(Some "GitHub.com:8443"))
        // Userinfo is stripped.
        Assert.That(Credentials.httpsHost "https://user:pass@example.com/o/r", Is.EqualTo(Some "example.com"))
        // Non-https, and an IPv6 literal, stay unscoped (None).
        Assert.That(Credentials.httpsHost "git@github.com:o/r.git", Is.EqualTo Option.None)
        Assert.That(Credentials.httpsHost "https://[::1]:443/o/r", Is.EqualTo Option.None)

    [<Test>]
    member _.HttpsHostSchemeIsCaseInsensitive() =
        // git/curl accept the scheme case-insensitively; a mixed-case `HTTPS://` on an
        // externally-supplied clone URL must NOT silently defeat host scoping (it would build an
        // unscoped helper, leaking the secret to any host git touches on redirect/submodule).
        Assert.That(Credentials.httpsHost "HTTPS://github.com/x/y", Is.EqualTo(Some "github.com"))
        Assert.That(Credentials.httpsHost "Https://github.com/x/y", Is.EqualTo(Some "github.com"))
        Assert.That(Credentials.httpsHost "hTTps://github.com/x/y", Is.EqualTo(Some "github.com"))
        // The host is still returned byte-for-byte — only the SCHEME comparison is case-folded,
        // the host case is preserved verbatim (git compares `host=` literally).
        Assert.That(Credentials.httpsHost "HTTPS://GitHub.COM:8443/x/y", Is.EqualTo(Some "GitHub.COM:8443"))
        // Non-https schemes stay unscoped (None) regardless of capitalization.
        Assert.That(Credentials.httpsHost "HTTP://github.com/x/y", Is.EqualTo Option.None)
        Assert.That(Credentials.httpsHost "SSH://git@github.com/x/y", Is.EqualTo Option.None)
        Assert.That(Credentials.httpsHost "FTP://github.com/x/y", Is.EqualTo Option.None)

    [<Test>]
    member _.GitCredentialHelperDefaultsUsername() =
        let h = Credentials.gitCredentialHelper (Credential.Token "t") None
        let _, user = h.Env |> List.find (fun (k, _) -> k = "VCS_TOOLKIT_GIT_USERNAME")
        Assert.That(user.Expose(), Is.EqualTo Credentials.DefaultGitUsername)

    [<Test>]
    member _.ResolveCredentialIsOptIn() : Task =
        task {
            let client = ManagedClient.Create "git"
            Assert.That(client.HasCredentials, Is.False)

            match! client.ResolveCredential(CredentialService.Git, None) with
            | Ok None -> ()
            | _ -> Assert.Fail "no provider -> ambient (None)"

            let withCred =
                client.WithCredentials(StaticCredential.Token "t0k" :> ICredentialProvider)

            Assert.That(withCred.HasCredentials)

            match! withCred.ResolveCredential(CredentialService.Git, None) with
            | Ok(Some cred) -> Assert.That(cred.Secret.Expose(), Is.EqualTo "t0k")
            | _ -> Assert.Fail "provider yields a credential"
        }

    [<Test>]
    member _.ResolveCredentialTreatsEmptySecretAsAmbient() : Task =
        task {
            for blank in [ ""; "   "; "\t\n" ] do
                let client =
                    (ManagedClient.Create "git").WithCredentials(StaticCredential.Token blank :> ICredentialProvider)

                match! client.ResolveCredential(CredentialService.GitHub, None) with
                | Ok None -> ()
                | _ -> Assert.Fail $"blank secret {blank} -> ambient (None)"
        }

    [<Test>]
    member _.ProviderFnRoutesOnService() : Task =
        task {
            let provider =
                Credentials.providerFn (fun r ->
                    match r.Service with
                    | CredentialService.GitHub -> Ok(Some(Credential.Token "gh"))
                    | _ -> Ok None)

            match! provider.Credential(CredentialRequest.Create CredentialService.GitHub) with
            | Ok(Some cred) -> Assert.That(cred.Secret.Expose(), Is.EqualTo "gh")
            | _ -> Assert.Fail "GitHub yields a credential"

            match! provider.Credential(CredentialRequest.Create CredentialService.GitLab) with
            | Ok None -> ()
            | _ -> Assert.Fail "GitLab defers to ambient"
        }

    [<Test>]
    member _.ResolveCredentialPassesHostToProvider() : Task =
        task {
            // A host-keyed provider must receive the exact host it was asked to resolve for —
            // the host in `CredentialRequest` is passed through verbatim, never overridden.
            let seen = ref (None: string option option)

            let provider =
                Credentials.providerFn (fun r ->
                    seen.Value <- Some r.Host
                    Ok(Some(Credential.Token "tok")))

            let client = (ManagedClient.Create "git").WithCredentials provider

            match! client.ResolveCredential(CredentialService.Git, Some "github.com") with
            | Ok(Some _) -> ()
            | _ -> Assert.Fail "provider yields a credential"

            Assert.That(
                seen.Value,
                Is.EqualTo(Some(Some "github.com")),
                "the resolve host reaches the CredentialRequest"
            )
        }

    [<Test>]
    member _.ResolveCredentialFailsClosedOnProviderError() : Task =
        task {
            // A provider error must NOT degrade to ambient (Ok None) — it propagates so the
            // caller can abort (fail-closed), never silently running unauthenticated.
            let provider =
                Credentials.providerFn (fun _ -> Error(exit "git" 1 "vault unreachable"))

            let client = (ManagedClient.Create "git").WithCredentials provider

            match! client.ResolveCredential(CredentialService.Git, None) with
            | Error(ProcessError.Exit(_, _, _, stderr)) -> Assert.That(stderr, Is.EqualTo "vault unreachable")
            | other -> Assert.Fail $"a provider Error must propagate (fail-closed), got {other}"
        }

    [<Test>]
    member _.TokenEnvInjectionCarriesExpectedHostToProvider() : Task =
        task {
            // The token-env injection path (`Prepare`) resolves with the client-bound
            // `WithExpectedHost` value, so a host-keyed provider is asked for THIS host instead
            // of the old hard-coded `None`.
            let seen = ref (None: string option option)

            let provider =
                Credentials.providerFn (fun r ->
                    seen.Value <- Some r.Host
                    Ok(Some(Credential.Token "gh-secret")))

            let client =
                ManagedClient
                    .WithRunner("gh", ScriptedRunner().Fallback(Reply.Ok ""))
                    .WithTokenEnv(CredentialService.GitHub, "GH_TOKEN")
                    .WithExpectedHost("github.enterprise.example")
                    .WithCredentials(provider)

            match! client.Run(client.Command [ "auth"; "status" ]) with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"token-env injection must not fail the command: {e}"

            Assert.That(
                seen.Value,
                Is.EqualTo(Some(Some "github.enterprise.example")),
                "the bound expected host reaches the CredentialRequest on the token-env path"
            )
        }

    [<Test>]
    member _.TokenEnvWithoutExpectedHostResolvesWithNone() : Task =
        task {
            // Without `WithExpectedHost` the token-env path has no host of its own (the forge CLI
            // resolves the host itself), so the request host is `None`.
            let seen = ref (None: string option option)

            let provider =
                Credentials.providerFn (fun r ->
                    seen.Value <- Some r.Host
                    Ok(Some(Credential.Token "gh-secret")))

            let client =
                ManagedClient
                    .WithRunner("gh", ScriptedRunner().Fallback(Reply.Ok ""))
                    .WithTokenEnv(CredentialService.GitHub, "GH_TOKEN")
                    .WithCredentials(provider)

            match! client.Run(client.Command [ "auth"; "status" ]) with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"{e}"

            Assert.That(seen.Value, Is.EqualTo(Some(None: string option)), "no bound host -> the request host is None")
        }

    [<Test>]
    member _.TokenEnvFailsClosedWithoutSpawning() : Task =
        task {
            // A provider error on the token-env path aborts before spawning — the process must
            // NOT run (it would run unauthenticated) and the error must surface (not ambient).
            let spawned = ref false

            let runner =
                ScriptedRunner()
                    .When(
                        (fun _ ->
                            spawned.Value <- true
                            true),
                        Reply.Ok ""
                    )

            let provider =
                Credentials.providerFn (fun _ -> Error(exit "gh" 1 "vault unreachable"))

            let client =
                ManagedClient
                    .WithRunner("gh", runner)
                    .WithTokenEnv(CredentialService.GitHub, "GH_TOKEN")
                    .WithExpectedHost("github.com")
                    .WithCredentials(provider)

            match! client.Run(client.Command [ "auth"; "status" ]) with
            | Error(ProcessError.Exit(_, _, _, stderr)) -> Assert.That(stderr, Is.EqualTo "vault unreachable")
            | other -> Assert.Fail $"token-env resolve must fail closed: {other}"

            Assert.That(spawned.Value, Is.False, "a fail-closed resolve must not spawn the command")
        }

    [<Test>]
    member _.TokenEnvDeferringProviderRunsAmbient() : Task =
        task {
            // `Ok None` and an empty/whitespace secret both defer to ambient auth: the command
            // still runs (no token injected), never failing closed.
            for provider in
                [ Credentials.providerFn (fun _ -> Ok None)
                  Credentials.providerFn (fun _ -> Ok(Some(Credential.Token "   "))) ] do
                let client =
                    ManagedClient
                        .WithRunner("gh", ScriptedRunner().Fallback(Reply.Ok "ok"))
                        .WithTokenEnv(CredentialService.GitHub, "GH_TOKEN")
                        .WithExpectedHost("github.com")
                        .WithCredentials(provider)

                match! client.Run(client.Command [ "auth"; "status" ]) with
                | Ok out -> Assert.That(out, Is.EqualTo "ok")
                | Error e -> Assert.Fail $"ambient fallback must let the command run: {e}"
        }

    [<Test>]
    member _.WithExpectedHostTreatsBlankAsNoBinding() : Task =
        task {
            // A blank expected host is treated as no binding (stays unscoped) rather than
            // scoping a host-keyed provider to an empty host it can never match.
            for blank in [ ""; "   " ] do
                let seen = ref (None: string option option)

                let provider =
                    Credentials.providerFn (fun r ->
                        seen.Value <- Some r.Host
                        Ok(Some(Credential.Token "tok")))

                let client =
                    ManagedClient
                        .WithRunner("gh", ScriptedRunner().Fallback(Reply.Ok ""))
                        .WithTokenEnv(CredentialService.GitHub, "GH_TOKEN")
                        .WithExpectedHost(blank)
                        .WithCredentials(provider)

                match! client.Run(client.Command [ "auth"; "status" ]) with
                | Ok _ -> ()
                | Error e -> Assert.Fail $"{e}"

                Assert.That(seen.Value, Is.EqualTo(Some(None: string option)), $"blank host {blank} -> no binding")
        }

[<TestFixture>]
type ObserverTests() =

    [<Test>]
    member _.ObserverSeesStartAndFinishWithFields() : Task =
        task {
            let obs = RecordingObserver()

            let client =
                ManagedClient.WithRunner("git", ScriptedRunner().Fallback(Reply.Ok "hello")).WithObserver obs

            match! client.Run(client.CommandIn("/repo", [ "status"; "--short" ])) with
            | Ok out -> Assert.That(out, Is.EqualTo "hello")
            | Error e -> Assert.Fail $"{e}"

            Assert.That(obs.Started.Count, Is.EqualTo 1, "one start event")
            Assert.That(obs.Finished.Count, Is.EqualTo 1, "one finish event")

            let started = obs.Started[0]
            Assert.That(started.Program, Is.EqualTo "git")
            // Structural `=` for the argv list (Is.EqualTo is FS0041-ambiguous for F# lists — K-017).
            Assert.That(started.Argv = [ "status"; "--short" ], "argv is reported verbatim")
            Assert.That(started.WorkingDirectory, Is.EqualTo(Some "/repo"))
            Assert.That(started.Attempt, Is.EqualTo 0, "first attempt is index 0")
            Assert.That(started.HasSecret, Is.False, "no token-env secret was injected")

            let ev, duration, outcome = obs.Finished[0]
            Assert.That(ev.Program, Is.EqualTo "git")
            Assert.That(ev.Argv = [ "status"; "--short" ])
            Assert.That(ev.Attempt, Is.EqualTo 0)
            Assert.That(duration, Is.GreaterThanOrEqualTo TimeSpan.Zero)

            match outcome with
            | Ok code -> Assert.That(code, Is.EqualTo 0, "a zero-exit Run reports exit code 0")
            | Error e -> Assert.Fail $"expected a success outcome, got {e}"
        }

    [<Test>]
    member _.ObserverAttemptCounterIncrementsAcrossRetries() : Task =
        task {
            let obs = RecordingObserver()
            let attempts = ref 0
            let lockErr = exit "git" 128 "Unable to create '/r/.git/index.lock': File exists"

            let runner =
                ScriptedRunner()
                    .When(
                        (fun _ ->
                            attempts.Value <- attempts.Value + 1
                            attempts.Value = 1),
                        Reply.Error lockErr
                    )
                    .Fallback(Reply.Ok "ok")

            let client =
                ManagedClient.WithRunner("git", runner).WithRetry(RetryPolicy.None.WithAttempts 3).WithObserver obs

            match! client.Run(client.Command [ "fetch" ]) with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"expected a successful retry, got {e}"

            // Two attempts observed: attempt 0 (lock failure), then attempt 1 (success).
            Assert.That(obs.Started.Count, Is.EqualTo 2, "one start per attempt")
            Assert.That(obs.Finished.Count, Is.EqualTo 2, "one finish per attempt")
            Assert.That(obs.Started[0].Attempt, Is.EqualTo 0)
            Assert.That(obs.Started[1].Attempt, Is.EqualTo 1, "the retry increments the attempt counter")

            let _, _, outcome0 = obs.Finished[0]
            let _, _, outcome1 = obs.Finished[1]

            match outcome0 with
            | Error e -> Assert.That(isLockContention e, "the first finish carries the classifiable lock error")
            | Ok _ -> Assert.Fail "expected the first attempt to fail with a lock error"

            match outcome1 with
            | Ok code -> Assert.That(code, Is.EqualTo 0, "the successful retry reports exit 0")
            | Error e -> Assert.Fail $"expected the retry to succeed, got {e}"
        }

    [<Test>]
    member _.ObserverNeverSeesSecretValueButSignalsPresence() : Task =
        task {
            // A token-env secret is injected into the command's environment; the observer must
            // learn only the FACT (HasSecret = true), never the value.
            let secretValue = "super-secret-token-value-42"
            let obs = RecordingObserver()

            let client =
                ManagedClient
                    .WithRunner("gh", ScriptedRunner().Fallback(Reply.Ok ""))
                    .WithTokenEnv(CredentialService.GitHub, "GH_TOKEN")
                    .WithCredentials(StaticCredential.Token secretValue :> ICredentialProvider)
                    .WithObserver
                    obs

            match! client.Run(client.Command [ "auth"; "status" ]) with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"{e}"

            Assert.That(obs.Started.Count, Is.EqualTo 1)
            Assert.That(obs.Finished.Count, Is.EqualTo 1)

            // Presence is signalled on both the start and finish identity.
            Assert.That(obs.Started[0].HasSecret, "an injected token-env secret must set HasSecret")
            let finishedEv, _, _ = obs.Finished[0]
            Assert.That(finishedEv.HasSecret)

            // The value never appears anywhere in the recorded events (whole-record render).
            let rendered =
                [ for e in obs.Started -> sprintf "%A" e ]
                @ [ for e, d, o in obs.Finished -> sprintf "%A|%A|%A" e d o ]
                |> String.concat "\n"

            Assert.That(rendered.Contains secretValue, Is.False, "the secret value must never reach an observer event")

            // And specifically the argv carries no secret.
            for e in obs.Started do
                for a in e.Argv do
                    Assert.That(a.Contains secretValue, Is.False, $"secret leaked into argv: {a}")
        }

    [<Test>]
    member _.ObserverReportsActualExitCodeFromOutput() : Task =
        task {
            // `Output` surfaces a non-zero exit as data (an `Ok` result); the observer's finish
            // outcome must carry that real exit code, not a flattened 0.
            let obs = RecordingObserver()

            let client =
                ManagedClient.WithRunner("git", ScriptedRunner().Fallback(Reply.Fail(2, "bad"))).WithObserver obs

            match! client.Output(client.Command [ "diff"; "--quiet" ]) with
            | Ok res -> Assert.That(res.Code = Some 2, "Output surfaces the non-zero exit as data")
            | Error e -> Assert.Fail $"Output must not error on a non-zero exit, got {e}"

            Assert.That(obs.Finished.Count, Is.EqualTo 1)
            let _, _, outcome = obs.Finished[0]

            match outcome with
            | Ok code -> Assert.That(code, Is.EqualTo 2, "the finish outcome carries the real exit code")
            | Error e -> Assert.Fail $"expected an exit-code outcome, got {e}"
        }

    [<Test>]
    member _.ThrowingObserverDoesNotFailTheCommand() : Task =
        task {
            // A diagnostic observer is isolated: an exception it throws must not fail (or perturb)
            // the command it observes.
            let obs =
                { new ICommandObserver with
                    member _.OnStarted(_) =
                        raise (InvalidOperationException "boom in OnStarted")

                    member _.OnFinished(_, _, _) =
                        raise (InvalidOperationException "boom in OnFinished") }

            let client =
                ManagedClient.WithRunner("git", ScriptedRunner().Fallback(Reply.Ok "ok")).WithObserver obs

            match! client.Run(client.Command [ "status" ]) with
            | Ok out -> Assert.That(out, Is.EqualTo "ok")
            | Error e -> Assert.Fail $"a throwing observer must not fail the command: {e}"
        }

[<TestFixture>]
type CloneDestCleanableTests() =

    let mutable tempRoot = ""

    [<SetUp>]
    member _.SetUp() =
        tempRoot <- Path.Combine(Path.GetTempPath(), "vcs-toolkit-clone-cleanable-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory tempRoot |> ignore

    [<TearDown>]
    member _.TearDown() =
        try
            if Directory.Exists tempRoot then
                Directory.Delete(tempRoot, true)
        with _ ->
            // Best-effort cleanup of the scratch temp tree; a leftover empty/near-empty temp dir
            // from a failed test is not fatal.
            ()

    [<Test>]
    member _.AbsentPathIsCleanable() =
        // Real filesystem probes (the production `Directory.Exists` / `EnumerateFileSystemEntries`)
        // through the actual public entry point.
        let dest = Path.Combine(tempRoot, "does-not-exist")
        Assert.That(cloneDestCleanable dest)

    [<Test>]
    member _.EmptyExistingDirectoryIsCleanable() =
        let dest = Path.Combine(tempRoot, "empty")
        Directory.CreateDirectory dest |> ignore
        Assert.That(cloneDestCleanable dest)

    [<Test>]
    member _.NonEmptyDirectoryIsNotCleanable() =
        let dest = Path.Combine(tempRoot, "nonempty")
        Directory.CreateDirectory dest |> ignore
        File.WriteAllText(Path.Combine(dest, "keep.txt"), "the caller's data")
        Assert.That(cloneDestCleanable dest, Is.False)

    [<Test>]
    member _.PreExistingFileIsNotCleanable() =
        // `dest` exists as a plain file rather than a directory. `File.Exists`'s true-side
        // short-circuit catches this on every platform; its false-side remains untrusted, so
        // unreadable destinations still go through the fail-closed enumeration path (R-01).
        let dest = Path.Combine(tempRoot, "a-file")
        File.WriteAllText(dest, "the caller's data")
        Assert.That(cloneDestCleanable dest, Is.False)

    // The remaining scenarios exercise `cloneDestCleanableCore` directly with a stubbed
    // `enumerate`, forcing a specific exception to reproduce a real unreadable directory
    // deterministically across platforms, rather than depending on OS-specific permission tricks
    // (denying read access) that behave differently per-OS and per-CI-runner privilege level.

    [<Test>]
    member _.EnumerationUnauthorizedAccessIsNotCleanable() =
        // R-01 regression: an existing-but-unreadable directory is exactly the case where
        // `Directory.Exists`/`File.Exists` silently swallow the access error and report `false`
        // (as if absent). `cloneDestCleanableCore` takes no `exists` probe at all - it can only
        // ever see this outcome through `enumerate` raising `UnauthorizedAccessException`, which
        // must fail closed (not cleanable), never fall through to "absent, therefore cleanable".
        let throwing =
            fun (_: string) -> raise (UnauthorizedAccessException "access denied"): string seq

        Assert.That(cloneDestCleanableCore throwing "irrelevant", Is.False)

    [<Test>]
    member _.EnumerationIOExceptionIsNotCleanable() =
        let throwing =
            fun (_: string) -> raise (IOException "transient I/O error"): string seq

        Assert.That(cloneDestCleanableCore throwing "irrelevant", Is.False)

    [<Test>]
    member _.EnumerationUnforeseenExceptionIsNotCleanable() =
        // "Any other error" - fail-closed even for exception types not explicitly named above.
        let throwing =
            fun (_: string) -> raise (InvalidOperationException "unexpected"): string seq

        Assert.That(cloneDestCleanableCore throwing "irrelevant", Is.False)

    [<Test>]
    member _.EnumerationDirectoryNotFoundIsCleanable() =
        // A `DirectoryNotFoundException` from `enumerate` is *proven* absence - cleanable, unlike
        // every other enumeration failure above.
        let throwing =
            fun (_: string) -> raise (DirectoryNotFoundException "gone"): string seq

        Assert.That(cloneDestCleanableCore throwing "irrelevant")

[<TestFixture>]
type RemoteUrlTests() =

    [<Test>]
    member _.ScpAuthorityTakesHostBeforeFirstDelimiterThenDropsUserinfo() =
        // Honest scp forms: the host is the authority before the first `:`/`/`, with any
        // `user@` userinfo dropped within it.
        Assert.That(RemoteUrl.scpAuthority "git@github.com:owner/repo", Is.EqualTo "github.com")
        Assert.That(RemoteUrl.scpAuthority "github.com:owner/repo", Is.EqualTo "github.com")
        Assert.That(RemoteUrl.scpAuthority "github.com/owner/repo", Is.EqualTo "github.com")

        // Security: an `@` in the PATH (after the first `:`/`/`) must NOT move the host boundary.
        // The authority is sliced off first, so the path's `@trusted` suffix can never
        // masquerade as the host — unlike dropping userinfo across the whole URL by the last `@`,
        // which is exactly the scp spoof this primitive exists to prevent.
        Assert.That(RemoteUrl.scpAuthority "evil.com:x@gitlab.com", Is.EqualTo "evil.com")
        Assert.That(RemoteUrl.scpAuthority "evil.com/x@github.com", Is.EqualTo "evil.com")
        Assert.That(RemoteUrl.scpAuthority "git@evil.com:x@gitlab.com", Is.EqualTo "evil.com")
