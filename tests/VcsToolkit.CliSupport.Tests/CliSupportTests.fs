module VcsToolkit.CliSupport.Tests

open System
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.CliSupport

let private exit (program: string) (code: int) (stderr: string) =
    ProcessError.Exit(program, code, "", stderr)

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
