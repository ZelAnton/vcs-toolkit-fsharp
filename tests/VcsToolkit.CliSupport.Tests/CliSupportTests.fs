module VcsToolkit.CliSupport.Tests

open System
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
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
                Retry.retryAsync policy isLockContention (fun () ->
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
                Retry.retryAsync policy isLockContention (fun () ->
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
                Retry.retryAsync policy isLockContention (fun () ->
                    task {
                        calls.Value <- calls.Value + 1
                        return Error(exit "git" 128 "index.lock': File exists")
                    })

            Assert.That(Result.isError out)
            Assert.That(calls.Value, Is.EqualTo 4, "all attempts used")
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
