module VcsToolkit.Forge.Tests

open System
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.CliSupport
open VcsToolkit.Forge
open VcsToolkit.Diff

// Forge handles over a scripted runner, per backend.
let private ghForge (tokens: string list) (reply: Reply) =
    Forge.FromGitHub(".", VcsToolkit.GitHub.GitHub.WithRunner(ScriptedRunner().On(tokens, reply)))

let private glForge (tokens: string list) (reply: Reply) =
    Forge.FromGitLab(".", VcsToolkit.GitLab.GitLab.WithRunner(ScriptedRunner().On(tokens, reply)))

let private teaForge (tokens: string list) (reply: Reply) =
    Forge.FromGitea(".", VcsToolkit.Gitea.Gitea.WithRunner(ScriptedRunner().On(tokens, reply)))

// Create a unique temp directory, run `f` against it, then remove it.
// On macOS, Path.GetTempPath() returns /var/... which is a symlink to /private/var.
// Directory.GetCurrentDirectory() after chdir() returns the resolved path /private/var/...
// To ensure expected paths built via Path.Combine match the OS-level resolved cwd,
// canonicalize the base directory by round-tripping through SetCurrentDirectory/GetCurrentDirectory.
let private withTempDir (f: string -> unit) =
    let unresolved =
        Path.Combine(Path.GetTempPath(), "vcs-forge-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory unresolved |> ignore

    // Canonicalize: chdir to it, get the resolved path, chdir back.
    let previous = Directory.GetCurrentDirectory()

    let dir =
        try
            Directory.SetCurrentDirectory unresolved
            Directory.GetCurrentDirectory()
        finally
            Directory.SetCurrentDirectory previous

    try
        f dir
    finally
        try
            Directory.Delete(dir, true)
        with
        | :? IOException ->
            // A test may still hold a file, or have removed its sandbox itself; cleanup must not hide its result.
            ()
        | :? UnauthorizedAccessException ->
            // Windows can briefly deny removal while a test-created handle is being released; preserve the test result.
            ()

// Change the process cwd only for the scope that needs to prove a handle captured its path.
let private withCurrentDirectory (dir: string) (f: unit -> unit) =
    let previous = Directory.GetCurrentDirectory()

    try
        Directory.SetCurrentDirectory dir
        f ()
    finally
        Directory.SetCurrentDirectory previous

// ---------------------------------------------------------------------------
// Forge construction — stored paths must be independent of process cwd
// ---------------------------------------------------------------------------

[<TestFixture>]
[<NonParallelizable>]
type ForgeConstructionTests() =

    [<Test>]
    member _.RelativePathsAreCapturedByEveryConstructor() =
        withTempDir (fun sandbox ->
            let initial = Path.Combine(sandbox, "initial")
            let later = Path.Combine(sandbox, "later")
            let expectedCwd = Path.Combine(initial, "bound")
            let expectedAt = Path.Combine(initial, "at")
            let handles: (string * Forge) list ref = ref []
            let atHolder: Forge option ref = ref None
            Directory.CreateDirectory initial |> ignore
            Directory.CreateDirectory later |> ignore

            withCurrentDirectory initial (fun () ->
                handles.Value <-
                    [ "GitHub", Forge.GitHub "bound"
                      "GitLab", Forge.GitLab "bound"
                      "Gitea", Forge.Gitea "bound"
                      "GitHubWithToken", Forge.GitHubWithToken("bound", "token")
                      "GitLabWithToken", Forge.GitLabWithToken("bound", "token")
                      "FromGitHub", Forge.FromGitHub("bound", VcsToolkit.GitHub.GitHub.WithRunner(ScriptedRunner()))
                      "FromGitLab", Forge.FromGitLab("bound", VcsToolkit.GitLab.GitLab.WithRunner(ScriptedRunner()))
                      "FromGitea", Forge.FromGitea("bound", VcsToolkit.Gitea.Gitea.WithRunner(ScriptedRunner()))
                      "FromUnknown", Forge.FromUnknown "bound" ]

                atHolder.Value <- Some((Forge.FromUnknown "bound").At "at"))

            withCurrentDirectory later (fun () ->
                for name, forge in handles.Value do
                    Assert.That(forge.Cwd, Is.EqualTo expectedCwd, $"{name} captures its relative cwd now")

                match atHolder.Value with
                | Some at -> Assert.That(at.Cwd, Is.EqualTo expectedAt, "At captures its relative dir now")
                | None -> Assert.Fail "the relative Forge.At handle was not created"))

    [<Test>]
    member _.InvalidPathsThrowArgumentExceptionWithTheInputParameterName() =
        let invalidPaths = [ ""; string (char 0) ]

        let constructors: (string * (string -> Forge)) list =
            [ "GitHub", Forge.GitHub
              "GitLab", Forge.GitLab
              "Gitea", Forge.Gitea
              "GitHubWithToken", fun cwd -> Forge.GitHubWithToken(cwd, "token")
              "GitLabWithToken", fun cwd -> Forge.GitLabWithToken(cwd, "token")
              "FromGitHub", fun cwd -> Forge.FromGitHub(cwd, VcsToolkit.GitHub.GitHub.WithRunner(ScriptedRunner()))
              "FromGitLab", fun cwd -> Forge.FromGitLab(cwd, VcsToolkit.GitLab.GitLab.WithRunner(ScriptedRunner()))
              "FromGitea", fun cwd -> Forge.FromGitea(cwd, VcsToolkit.Gitea.Gitea.WithRunner(ScriptedRunner()))
              "FromUnknown", Forge.FromUnknown ]

        let requireArgumentException (action: Action) : ArgumentException =
            let caughtException = Assert.Throws<ArgumentException>(action)

            match caughtException with
            | null -> raise (InvalidOperationException "Assert.Throws returned null unexpectedly")
            | nonNullException -> nonNullException

        for invalid in invalidPaths do
            for name, makeForge in constructors do
                let ex = requireArgumentException (Action(fun () -> makeForge invalid |> ignore))
                Assert.That(ex.ParamName, Is.EqualTo "cwd", $"{name} must report the cwd parameter")

            let forge = Forge.FromUnknown "."
            let at = requireArgumentException (Action(fun () -> forge.At invalid |> ignore))
            Assert.That(at.ParamName, Is.EqualTo "dir", "At must report the dir parameter")

// ---------------------------------------------------------------------------
// ForgeKind.OfRemoteUrl — the security-sensitive host classifier
// ---------------------------------------------------------------------------

[<TestFixture>]
type ForgeKindTests() =

    [<Test>]
    member _.ClassifiesPublicSaasHosts() =
        let cases =
            [ "https://github.com/o/r.git", ForgeKind.GitHub
              "git@github.com:o/r.git", ForgeKind.GitHub
              "https://foo.github.com/o/r", ForgeKind.GitHub // proper subdomain
              "https://gitlab.com/o/r", ForgeKind.GitLab
              "https://user:pass@gitlab.com/o/r", ForgeKind.GitLab // userinfo stripped
              "ssh://git@gitlab.com:22/o/r.git", ForgeKind.GitLab
              "https://gitea.com/o/r.git", ForgeKind.Gitea
              "git@codeberg.org:o/r.git", ForgeKind.Gitea
              "https://docs.codeberg.org/o/r", ForgeKind.Gitea ]

        for url, want in cases do
            match ForgeKind.OfRemoteUrl url with
            | Some k -> Assert.That(k, Is.EqualTo want, $"{url}")
            | None -> Assert.Fail $"{url} should classify as {want.AsString}"

    [<Test>]
    member _.RejectsSelfHostedAndLookalikes() =
        // A self-hosted instance, and — crucially — a lookalike an attacker controls, must
        // NOT classify as a trusted forge: the safe answer is None.
        let urls =
            [ "https://gitlab.example.com/o/r.git" // self-hosted GitLab
              "https://gitea.example.org/o/r.git" // self-hosted Gitea
              "https://git.acme.io/o/r.git" // arbitrary
              "https://gitlab.com.attacker.net/o/r" // lookalike
              "git@gitlab.attacker.com:o/r.git" // lookalike
              "https://my-gitea-host.evil.com/o/r" // substring spoof
              "https://notgithub.com/o/r" // suffix without the dot
              "https://github.com.evil.example/o/r" // lookalike
              "" ]

        for url in urls do
            Assert.That(ForgeKind.OfRemoteUrl url, Is.EqualTo None, $"{url} must not classify")

    [<Test>]
    member _.RejectsBracketedNonIpv6Spoofs() =
        // A bracketed *name* — or a colon-bearing fake ending in a trusted domain — must
        // NOT be unwrapped and classified (only a genuine IPv6 literal is unwrapped, and no
        // IPv6 literal is a trusted SaaS host).
        let urls =
            [ "https://[gitlab.com]/o/r"
              "https://[a:b.gitlab.com]/o/r" // colon-bearing fake ending in .gitlab.com
              "https://[github.com]/o/r"
              "https://[::1]/o/r" // genuine IPv6 — but never a trusted host
              "https://[::ffff:gitea.com]/o/r" // not a real IPv6 literal
              // IPv6 zone/scope-id spoofs: .NET's parser accepts an arbitrary scope string
              // that Rust rejects; the scope text must not be able to spoof a trusted suffix.
              "https://[fe80::1%evil.gitlab.com]/o/r"
              "https://[::1%evil.github.com]/o/r"
              "https://[fe80::1%25evil.gitea.com]/o/r" ] // %25-encoded scope

        for url in urls do
            Assert.That(ForgeKind.OfRemoteUrl url, Is.EqualTo None, $"{url} must not classify as a trusted forge")

    [<Test>]
    member _.AsStringMapsEachKind() =
        Assert.That(ForgeKind.GitHub.AsString, Is.EqualTo "github")
        Assert.That(ForgeKind.GitLab.AsString, Is.EqualTo "gitlab")
        Assert.That(ForgeKind.Gitea.AsString, Is.EqualTo "gitea")
        Assert.That(ForgeKind.Unknown.AsString, Is.EqualTo "unknown")

    [<Test>]
    member _.ForgeOpAllEnumeratesTheVaryingOps() =
        Assert.That(ForgeOp.All.Length, Is.EqualTo 5)
        Assert.That(List.contains ForgeOp.PrChecks ForgeOp.All, Is.True)
        Assert.That(List.contains ForgeOp.PrDiff ForgeOp.All, Is.True)

// ---------------------------------------------------------------------------
// ForgeError classifiers
// ---------------------------------------------------------------------------

[<TestFixture>]
type ErrorTests() =

    let exit (stderr: string) =
        ForgeError.Forge(ProcessError.Exit("gh", 1, "", stderr))

    [<Test>]
    member _.ClassifiesAuthFailures() =
        for msg in
            [ "HTTP 401: Bad credentials (https://api.github.com/graphql)"
              "401 Unauthorized (could not authenticate, run `glab auth login`)"
              "you are not logged in. Run gh auth login to authenticate"
              "GraphQL: requires authentication" ] do
            Assert.That((exit msg).IsUnauthorized, Is.True, $"{msg}")
            Assert.That((exit msg).IsRateLimited, Is.False, $"{msg}")

    [<Test>]
    member _.ClassifiesRateLimits() =
        for msg in
            [ "API rate limit exceeded for user ID 123"
              "HTTP 429: Too Many Requests"
              "You have exceeded a secondary rate limit (abuse detection mechanism)" ] do
            Assert.That((exit msg).IsRateLimited, Is.True, $"{msg}")
            Assert.That((exit msg).IsUnauthorized, Is.False, $"{msg}")

    [<Test>]
    member _.DoesNotFalsePositiveOnEchoedNumbers() =
        // Status-qualified markers (`http 401`), not bare integers, so a not-found that
        // echoes 401/429 is neither auth nor rate-limit.
        Assert.That((exit "Could not resolve to a PullRequest with the number of 401.").IsUnauthorized, Is.False)
        Assert.That((exit "Could not resolve to an Issue with the number of 429.").IsRateLimited, Is.False)
        Assert.That((exit "no pull requests found").IsUnauthorized, Is.False)

    [<Test>]
    member _.DoesNotFullyUnicodeFoldNonAsciiInput() =
        // `CliOutput` lowercases via `Classify.asciiLower` (the same ASCII-only fold
        // `Classify.exitOutputMatches` uses for git/jj), not `ToLowerInvariant`. U+212A
        // KELVIN SIGN is the textbook case: it folds to ASCII 'k' under a full Unicode
        // invariant fold, but `asciiLower` only touches 'A'-'Z' and leaves it untouched —
        // so it can never spuriously complete an ASCII marker word the way a full fold
        // could. Confirm the shared helper itself preserves that contract...
        let kelvin = "K"
        Assert.That(asciiLower kelvin, Is.EqualTo kelvin)
        Assert.That(kelvin.ToLowerInvariant(), Is.EqualTo "k")
        // ...and that feeding it through `ForgeError.CliOutput` still classifies as
        // neither auth nor rate-limit: a lone non-ASCII character carries no marker.
        Assert.That((exit kelvin).IsUnauthorized, Is.False)
        Assert.That((exit kelvin).IsRateLimited, Is.False)

    [<Test>]
    member _.ClassifiesNotFoundAndUnsupported() =
        Assert.That(ForgeError.Forge(ProcessError.NotFound("gh", None)).IsNotFound, Is.True)
        // The compiler-generated case tester distinguishes Unsupported.
        Assert.That((ForgeError.Unsupported(ForgeKind.Gitea, "prChecks")).IsUnsupported, Is.True)
        Assert.That((ForgeError.InvalidInput "x").IsUnsupported, Is.False)
        // The facade's own variants carry no CLI body → never auth/rate-limit.
        Assert.That((ForgeError.InvalidInput "x").IsUnauthorized, Is.False)
        Assert.That((ForgeError.Unsupported(ForgeKind.Gitea, "prChecks")).Message, Does.Contain "gitea")

// ---------------------------------------------------------------------------
// Backend dispatch, capability gaps, and the input guards
// ---------------------------------------------------------------------------

[<TestFixture>]
type DispatchTests() =

    [<Test>]
    member _.KindAndSupportsReflectTheBackend() =
        let unknown = Forge.FromUnknown "."
        Assert.That(unknown.Kind, Is.EqualTo ForgeKind.Unknown)
        // An Unknown handle supports nothing — agrees with its all-Unsupported dispatch.
        Assert.That(unknown.Supports ForgeOp.PrChecks, Is.False)
        Assert.That(unknown.Supports ForgeOp.RepoView, Is.False)
        Assert.That(unknown.Supports ForgeOp.PrDiff, Is.False)

        let gh = ghForge [ "pr"; "list" ] (Reply.Ok "[]")
        Assert.That(gh.Kind, Is.EqualTo ForgeKind.GitHub)
        Assert.That(gh.Supports ForgeOp.PrChecks, Is.True)
        Assert.That(gh.Supports ForgeOp.PrDiff, Is.True)
        Assert.That(gh.Cwd, Is.EqualTo(Directory.GetCurrentDirectory()))

        let gl = glForge [ "mr"; "list" ] (Reply.Ok "[]")
        Assert.That(gl.Kind, Is.EqualTo ForgeKind.GitLab)
        Assert.That(gl.Supports ForgeOp.PrDiff, Is.True)

        let tea = teaForge [ "pr"; "list" ] (Reply.Ok "[]")
        // Gitea supports NONE of the varying ops.
        Assert.That(tea.Supports ForgeOp.RepoView, Is.False)
        Assert.That(tea.Supports ForgeOp.PrChecks, Is.False)
        Assert.That(tea.Supports ForgeOp.ReleaseView, Is.False)
        Assert.That(tea.Supports ForgeOp.PrDiff, Is.False)

    [<Test>]
    member _.RawClientAccessorsReturnTheBackendClientOnly() =
        // Each `*Client` escape hatch is `Some` only for its own backend — the `Repo.Git`/`Repo.Jj`
        // analogue, letting a consumer reach the raw client (e.g. for `gh`-only ops) after building
        // the handle with a convenience constructor.
        let gh = ghForge [ "pr"; "list" ] (Reply.Ok "[]")
        Assert.That(gh.GitHubClient.IsSome, "GitHub-backed → GitHubClient is Some")
        Assert.That(gh.GitLabClient.IsNone)
        Assert.That(gh.GiteaClient.IsNone)

        let gl = glForge [ "mr"; "list" ] (Reply.Ok "[]")
        Assert.That(gl.GitLabClient.IsSome)
        Assert.That(gl.GitHubClient.IsNone)
        Assert.That(gl.GiteaClient.IsNone)

        let tea = teaForge [ "pr"; "list" ] (Reply.Ok "[]")
        Assert.That(tea.GiteaClient.IsSome)
        Assert.That(tea.GitHubClient.IsNone)

        let unknown = Forge.FromUnknown "."
        Assert.That(unknown.GitHubClient.IsNone, "Unknown backend → no client")
        Assert.That(unknown.GitLabClient.IsNone)
        Assert.That(unknown.GiteaClient.IsNone)

    [<Test>]
    member _.GitHubPrListMapsUnifiedState() : Task =
        task {
            let json =
                """[{"number":42,"title":"Add X","state":"MERGED","headRefName":"feat","baseRefName":"main","url":"u"}]"""

            let forge = ghForge [ "pr"; "list"; "--json" ] (Reply.Ok json)

            match! forge.PrList() with
            | Ok [ pr ] ->
                Assert.That(pr.Number, Is.EqualTo 42UL)
                Assert.That(pr.State, Is.EqualTo ForgePrState.Merged, "GitHub MERGED → Merged")
                Assert.That(pr.SourceBranch, Is.EqualTo "feat")
                Assert.That(pr.TargetBranch, Is.EqualTo "main")
            | Ok other -> Assert.Fail $"expected one PR, got {other.Length}"
            | Error e -> Assert.Fail $"pr list failed: {e.Message}"
        }

    [<Test>]
    member _.GitLabMrListNormalisesOpenedState() : Task =
        task {
            let json =
                """[{"iid":7,"title":"MR","state":"opened","source_branch":"s","target_branch":"main","web_url":"u","draft":true}]"""

            let forge = glForge [ "mr"; "list" ] (Reply.Ok json)

            match! forge.PrList() with
            | Ok [ pr ] ->
                Assert.That(pr.Number, Is.EqualTo 7UL, "GitLab iid → number")
                Assert.That(pr.State, Is.EqualTo ForgePrState.Open, "GitLab 'opened' → Open")
                Assert.That(pr.Draft, Is.EqualTo(Some true), "GitLab reports draft on the lean surface → Some")
            | Ok other -> Assert.Fail $"expected one MR, got {other.Length}"
            | Error e -> Assert.Fail $"mr list failed: {e.Message}"
        }

    [<Test>]
    member _.GiteaPrListDerivesMergedFromFlag() : Task =
        task {
            let json =
                """[{"index":"9","title":"done","state":"merged","head":"f","base":"main","url":"u"}]"""

            let forge = teaForge [ "pr"; "list"; "--fields" ] (Reply.Ok json)

            match! forge.PrList() with
            | Ok [ pr ] ->
                Assert.That(pr.Number, Is.EqualTo 9UL)
                Assert.That(pr.State, Is.EqualTo ForgePrState.Merged, "tea merged flag → Merged")
            | Ok other -> Assert.Fail $"expected one PR, got {other.Length}"
            | Error e -> Assert.Fail $"pr list failed: {e.Message}"
        }

    [<Test>]
    member _.GiteaUnsupportedOpsReturnUnsupportedWithoutSpawning() : Task =
        task {
            // An empty ScriptedRunner (no fallback) RAISES on any spawn — so these tests
            // also prove the dispatch short-circuits to `Unsupported` before spawning.
            let forge =
                Forge.FromGitea(".", VcsToolkit.Gitea.Gitea.WithRunner(ScriptedRunner()))

            let isUnsupported (t: Task<Result<'T, ForgeError>>) =
                task {
                    let! r = t

                    return
                        match r with
                        | Error e -> e.IsUnsupported
                        | Ok _ -> false
                }

            let! a = isUnsupported (forge.RepoView())
            let! b = isUnsupported (forge.PrChecks 1UL)
            let! c = isUnsupported (forge.ReleaseView "v1")
            let! d = isUnsupported (forge.PrMarkReady 1UL)

            for flag, name in [ a, "repoView"; b, "prChecks"; c, "releaseView"; d, "prMarkReady" ] do
                Assert.That(flag, Is.True, $"Gitea {name} must be Unsupported without spawning")
        }

    [<Test>]
    member _.UnknownHandleIsInertButHonest() : Task =
        task {
            let forge = Forge.FromUnknown "."

            match! forge.AuthStatus() with
            | Ok v -> Assert.That(v, Is.False, "no CLI to probe → false, without spawning")
            | Error e -> Assert.Fail $"auth status failed: {e.Message}"

            let isUnsupported (t: Task<Result<'T, ForgeError>>) =
                task {
                    let! r = t

                    return
                        match r with
                        | Error e -> e.IsUnsupported
                        | Ok _ -> false
                }

            // Every operation (bar auth/capabilities) is Unsupported on an Unknown handle.
            let! a = isUnsupported (forge.PrList())
            let! b = isUnsupported (forge.PrView 1UL)
            let! c = isUnsupported (forge.RepoView())
            let! d = isUnsupported (forge.PrMerge(1UL, PrMerge.Merge))
            let! e = isUnsupported (forge.IssueCreate("t", "b"))
            let! f = isUnsupported (forge.ReleaseView "v1")

            for flag, name in
                [ a, "prList"
                  b, "prView"
                  c, "repoView"
                  d, "prMerge"
                  e, "issueCreate"
                  f, "releaseView" ] do
                Assert.That(flag, Is.True, $"Unknown {name} must be Unsupported")

            match! forge.Capabilities() with
            | Ok caps -> Assert.That(caps, Is.EqualTo ForgeCapabilities.AllFalse)
            | Error err -> Assert.Fail $"capabilities failed: {err.Message}"
        }

    [<Test>]
    member _.GitHubPrChecksAggregatesBuckets() : Task =
        task {
            // A failing check dominates the coarse status.
            let failing =
                ghForge
                    [ "pr"; "checks"; "1"; "--json" ]
                    (Reply.Ok
                        """[{"name":"a","state":"SUCCESS","bucket":"pass","workflow":"","link":"","startedAt":"","completedAt":""},{"name":"b","state":"FAILURE","bucket":"fail","workflow":"","link":"","startedAt":"","completedAt":""}]""")

            match! failing.PrChecks 1UL with
            | Ok status -> Assert.That(status, Is.EqualTo CiStatus.Failing, "any fail ⇒ Failing")
            | Error err -> Assert.Fail $"pr checks failed: {err.Message}"

            // All-pass ⇒ Passing.
            let passing =
                ghForge
                    [ "pr"; "checks"; "1"; "--json" ]
                    (Reply.Ok
                        """[{"name":"a","state":"SUCCESS","bucket":"pass","workflow":"","link":"","startedAt":"","completedAt":""}]""")

            match! passing.PrChecks 1UL with
            | Ok status -> Assert.That(status, Is.EqualTo CiStatus.Passing)
            | Error err -> Assert.Fail $"pr checks failed: {err.Message}"
        }

    [<Test>]
    member _.GitLabRepoViewSplitsOwnerAndConservesPrivacy() : Task =
        task {
            // owner = everything before the last `/`; an absent visibility is NOT private.
            let json =
                """{"name":"cli","path_with_namespace":"group/sub/cli","default_branch":"main","web_url":"u"}"""

            let forge = glForge [ "repo"; "view"; "--output"; "json" ] (Reply.Ok json)

            match! forge.RepoView() with
            | Ok repo ->
                Assert.That(repo.Name, Is.EqualTo "cli")
                Assert.That(repo.Owner, Is.EqualTo "group/sub", "owner is the namespace before the last /")
                Assert.That(repo.Private, Is.EqualTo None, "absent visibility → unknown (None), never a false 'public'")
            | Error err -> Assert.Fail $"repo view failed: {err.Message}"
        }

    [<Test>]
    member _.PrDiffDispatchesToTheUnderlyingClientAndParsesFileDiffs() : Task =
        task {
            let raw =
                "diff --git a/foo.txt b/foo.txt\n--- a/foo.txt\n+++ b/foo.txt\n@@ -1,1 +1,2 @@\n unchanged\n+added\n"

            let gh = ghForge [ "pr"; "diff"; "1" ] (Reply.Ok raw)

            match! gh.PrDiff 1UL with
            | Ok [ file ] ->
                Assert.That(file.Path, Is.EqualTo "foo.txt")
                Assert.That(file.Change, Is.EqualTo ChangeKind.Modified)
            | Ok other -> Assert.Fail $"expected one file diff, got {other.Length}"
            | Error e -> Assert.Fail $"gh pr diff failed: {e.Message}"

            let gl = glForge [ "mr"; "diff"; "1" ] (Reply.Ok raw)

            match! gl.PrDiff 1UL with
            | Ok [ file ] ->
                Assert.That(file.Path, Is.EqualTo "foo.txt")
                Assert.That(file.Change, Is.EqualTo ChangeKind.Modified)
            | Ok other -> Assert.Fail $"expected one file diff, got {other.Length}"
            | Error e -> Assert.Fail $"glab mr diff failed: {e.Message}"
        }

    [<Test>]
    member _.PrDiffIsUnsupportedOnGiteaAndUnknownWithoutSpawning() : Task =
        task {
            let tea = Forge.FromGitea(".", VcsToolkit.Gitea.Gitea.WithRunner(ScriptedRunner()))

            match! tea.PrDiff 1UL with
            | Error e -> Assert.That(e.IsUnsupported, Is.True, "Gitea prDiff must be Unsupported without spawning")
            | Ok _ -> Assert.Fail "Gitea prDiff must be Unsupported"

            let unknown = Forge.FromUnknown "."

            match! unknown.PrDiff 1UL with
            | Error e -> Assert.That(e.IsUnsupported, Is.True, "Unknown prDiff must be Unsupported")
            | Ok _ -> Assert.Fail "Unknown prDiff must be Unsupported"
        }

    [<Test>]
    member _.PrCommentRejectsEmptyBody() : Task =
        task {
            let forge =
                Forge.FromGitHub(".", VcsToolkit.GitHub.GitHub.WithRunner(ScriptedRunner().Fallback(Reply.Ok "")))

            match! forge.PrComment(1UL, "   ") with
            | Error e -> Assert.That(e.Message, Does.Contain "empty")
            | Ok _ -> Assert.Fail "a whitespace-only comment body must be refused before spawning"
        }

    [<Test>]
    member _.PrEditRejectsBothNone() : Task =
        task {
            let forge =
                Forge.FromGitHub(".", VcsToolkit.GitHub.GitHub.WithRunner(ScriptedRunner().Fallback(Reply.Ok "")))

            match! forge.PrEdit(1UL, PrEdit.Create()) with
            | Error e -> Assert.That(e.Message, Does.Contain "at least one")
            | Ok() -> Assert.Fail "an edit with nothing to change must be refused before spawning"
        }

    [<Test>]
    member _.CapabilitiesIntersectWithAuth() : Task =
        task {
            // Authed GitHub → the full ships-the-command map, all true; version + kind surfaced.
            let authed =
                Forge.FromGitHub(
                    ".",
                    VcsToolkit.GitHub.GitHub.WithRunner(
                        ScriptedRunner()
                            .On([ "--version" ], Reply.Ok "gh version 2.40.0\n")
                            .On([ "auth"; "status" ], Reply.Exit 0)
                    )
                )

            match! authed.Capabilities() with
            | Ok caps ->
                Assert.That(caps.Authed, Is.True)
                Assert.That(caps.PrCreate, Is.True)
                Assert.That(caps.PrChecks, Is.True)
                Assert.That(caps.Kind, Is.EqualTo ForgeKind.GitHub)
                Assert.That(caps.Version |> Option.map (fun v -> v.ToString()), Is.EqualTo(Some "2.40.0"))
            | Error e -> Assert.Fail $"capabilities failed: {e.Message}"

            // Unauthenticated → every per-op flag zeroed, but the version/kind still reported.
            let unauthed =
                Forge.FromGitHub(
                    ".",
                    VcsToolkit.GitHub.GitHub.WithRunner(
                        ScriptedRunner()
                            .On([ "--version" ], Reply.Ok "gh version 2.40.0\n")
                            .On([ "auth"; "status" ], Reply.Exit 1)
                    )
                )

            match! unauthed.Capabilities() with
            | Ok caps ->
                Assert.That(caps.Authed, Is.False)
                Assert.That(caps.PrCreate, Is.False)
                Assert.That(caps.Kind, Is.EqualTo ForgeKind.GitHub, "backend kind reported even when unauthed")

                Assert.That(
                    caps.Version |> Option.map (fun v -> v.ToString()),
                    Is.EqualTo(Some "2.40.0"),
                    "the CLI version is reported independently of auth"
                )
            | Error e -> Assert.Fail $"capabilities failed: {e.Message}"

            // Gitea's static map has no checks command.
            let tea =
                Forge.FromGitea(
                    ".",
                    VcsToolkit.Gitea.Gitea.WithRunner(
                        ScriptedRunner()
                            .On([ "--version" ], Reply.Ok "tea version 0.9.2\n")
                            .On([ "login"; "list" ], Reply.Ok """[{"name":"g"}]""")
                    )
                )

            match! tea.Capabilities() with
            | Ok caps ->
                Assert.That(caps.Authed, Is.True)
                Assert.That(caps.PrChecks, Is.False, "tea has no checks command")
                Assert.That(caps.PrCreate, Is.True)
                Assert.That(caps.Kind, Is.EqualTo ForgeKind.Gitea)
            | Error e -> Assert.Fail $"capabilities failed: {e.Message}"
        }

// ---------------------------------------------------------------------------
// Optional-field contract: `None` (backend didn't report) vs a confirmed
// `Some false`/`Some true`, isolated per backend and per field.
// ---------------------------------------------------------------------------

[<TestFixture>]
type OptionalFieldTests() =

    // --- ForgePr.Draft — Some only where the backend surfaces it, else None ---

    [<Test>]
    member _.GitHubPrDraftIsNoneWhenUnreported() : Task =
        task {
            // gh's lean PR `--json` carries no `isDraft` → the draft state is unknown, None.
            let json =
                """[{"number":1,"title":"t","state":"OPEN","headRefName":"f","baseRefName":"main","url":"u"}]"""

            let forge = ghForge [ "pr"; "list"; "--json" ] (Reply.Ok json)

            match! forge.PrList() with
            | Ok [ pr ] -> Assert.That(pr.Draft, Is.EqualTo None, "GitHub PR draft is unreported → None")
            | Ok other -> Assert.Fail $"expected one PR, got {other.Length}"
            | Error e -> Assert.Fail $"pr list failed: {e.Message}"
        }

    [<Test>]
    member _.GitLabMrDraftIsConfirmedBothWays() : Task =
        task {
            // GitLab carries `draft` on its MR surface → a confirmed Some, both values.
            let draftJson =
                """[{"iid":1,"title":"t","state":"opened","source_branch":"s","target_branch":"main","web_url":"u","draft":true}]"""

            let draftForge = glForge [ "mr"; "list" ] (Reply.Ok draftJson)

            match! draftForge.PrList() with
            | Ok [ pr ] -> Assert.That(pr.Draft, Is.EqualTo(Some true), "draft:true → Some true")
            | Ok other -> Assert.Fail $"expected one MR, got {other.Length}"
            | Error e -> Assert.Fail $"mr list failed: {e.Message}"

            let readyJson =
                """[{"iid":1,"title":"t","state":"opened","source_branch":"s","target_branch":"main","web_url":"u","draft":false}]"""

            let readyForge = glForge [ "mr"; "list" ] (Reply.Ok readyJson)

            match! readyForge.PrList() with
            | Ok [ pr ] ->
                Assert.That(pr.Draft, Is.EqualTo(Some false), "draft:false → confirmed Some false, distinct from None")
            | Ok other -> Assert.Fail $"expected one MR, got {other.Length}"
            | Error e -> Assert.Fail $"mr list failed: {e.Message}"
        }

    [<Test>]
    member _.GiteaPrDraftIsNoneWhenUnreported() : Task =
        task {
            // tea's lean PR surface has no draft column → None.
            let json =
                """[{"index":"1","title":"t","state":"open","head":"f","base":"main","url":"u"}]"""

            let forge = teaForge [ "pr"; "list"; "--fields" ] (Reply.Ok json)

            match! forge.PrList() with
            | Ok [ pr ] -> Assert.That(pr.Draft, Is.EqualTo None, "Gitea PR draft is unreported → None")
            | Ok other -> Assert.Fail $"expected one PR, got {other.Length}"
            | Error e -> Assert.Fail $"pr list failed: {e.Message}"
        }

    // --- ForgeRepo.Private — Some when visibility known, None when absent ---

    [<Test>]
    member _.GitHubRepoPrivateIsConfirmedBothWays() : Task =
        task {
            // gh's repo surface carries `isPrivate` → a confirmed Some, both values.
            let privateJson =
                """{"name":"r","owner":{"login":"o"},"url":"u","isPrivate":true,"defaultBranchRef":{"name":"main"}}"""

            let privateForge = ghForge [ "repo"; "view" ] (Reply.Ok privateJson)

            match! privateForge.RepoView() with
            | Ok repo -> Assert.That(repo.Private, Is.EqualTo(Some true), "isPrivate:true → Some true")
            | Error e -> Assert.Fail $"repo view failed: {e.Message}"

            let publicJson =
                """{"name":"r","owner":{"login":"o"},"url":"u","isPrivate":false,"defaultBranchRef":{"name":"main"}}"""

            let publicForge = ghForge [ "repo"; "view" ] (Reply.Ok publicJson)

            match! publicForge.RepoView() with
            | Ok repo -> Assert.That(repo.Private, Is.EqualTo(Some false), "isPrivate:false → confirmed Some false")
            | Error e -> Assert.Fail $"repo view failed: {e.Message}"
        }

    [<Test>]
    member _.GitLabRepoPrivateReflectsVisibilityElseNone() : Task =
        task {
            // Known visibility → Some (private iff not "public"); absent → None (unknown).
            let privateJson =
                """{"name":"cli","path_with_namespace":"g/cli","default_branch":"main","web_url":"u","visibility":"private"}"""

            let privateForge =
                glForge [ "repo"; "view"; "--output"; "json" ] (Reply.Ok privateJson)

            match! privateForge.RepoView() with
            | Ok repo -> Assert.That(repo.Private, Is.EqualTo(Some true), "visibility private → Some true")
            | Error e -> Assert.Fail $"repo view failed: {e.Message}"

            let publicJson =
                """{"name":"cli","path_with_namespace":"g/cli","default_branch":"main","web_url":"u","visibility":"public"}"""

            let publicForge =
                glForge [ "repo"; "view"; "--output"; "json" ] (Reply.Ok publicJson)

            match! publicForge.RepoView() with
            | Ok repo -> Assert.That(repo.Private, Is.EqualTo(Some false), "visibility public → confirmed Some false")
            | Error e -> Assert.Fail $"repo view failed: {e.Message}"

            let unknownJson =
                """{"name":"cli","path_with_namespace":"g/cli","default_branch":"main","web_url":"u"}"""

            let unknownForge =
                glForge [ "repo"; "view"; "--output"; "json" ] (Reply.Ok unknownJson)

            match! unknownForge.RepoView() with
            | Ok repo ->
                Assert.That(repo.Private, Is.EqualTo None, "absent visibility → None (unknown), not Some false")
            | Error e -> Assert.Fail $"repo view failed: {e.Message}"
        }

    // --- ForgeRelease.Draft / Prerelease — Some where the backend has the concept ---

    [<Test>]
    member _.GitHubReleaseFlagsAreConfirmed() : Task =
        task {
            // gh's release surface carries isDraft/isPrerelease → Some, distinguishing a
            // confirmed `Some false` from an absent-concept None.
            let json =
                """[{"tagName":"v1","name":"1","isDraft":true,"isPrerelease":false,"publishedAt":"t"}]"""

            let forge = ghForge [ "release"; "list" ] (Reply.Ok json)

            match! forge.ReleaseList() with
            | Ok [ rel ] ->
                Assert.That(rel.Draft, Is.EqualTo(Some true), "gh isDraft:true → Some true")
                Assert.That(rel.Prerelease, Is.EqualTo(Some false), "gh isPrerelease:false → confirmed Some false")
            | Ok other -> Assert.Fail $"expected one release, got {other.Length}"
            | Error e -> Assert.Fail $"release list failed: {e.Message}"
        }

    [<Test>]
    member _.GitLabReleaseFlagsAreNone() : Task =
        task {
            // GitLab releases have no draft/pre-release concept → both None (never Some false).
            let json = """[{"tag_name":"v1","name":"One","description":"notes"}]"""
            let forge = glForge [ "release"; "list" ] (Reply.Ok json)

            match! forge.ReleaseList() with
            | Ok [ rel ] ->
                Assert.That(rel.Draft, Is.EqualTo None, "GitLab has no release draft → None")
                Assert.That(rel.Prerelease, Is.EqualTo None, "GitLab has no pre-release → None")
            | Ok other -> Assert.Fail $"expected one release, got {other.Length}"
            | Error e -> Assert.Fail $"release list failed: {e.Message}"
        }

    [<Test>]
    member _.GiteaReleaseFlagsAreConfirmed() : Task =
        task {
            // tea's release `Status` column drives both flags → Some, and the complementary
            // flag is a confirmed `Some false`, not None.
            let draftJson = """[{"tag-_name":"v1","title":"One","status":"draft"}]"""
            let draftForge = teaForge [ "releases"; "list" ] (Reply.Ok draftJson)

            match! draftForge.ReleaseList() with
            | Ok [ rel ] ->
                Assert.That(rel.Draft, Is.EqualTo(Some true), "status draft → Some true")
                Assert.That(rel.Prerelease, Is.EqualTo(Some false), "status draft → prerelease confirmed Some false")
            | Ok other -> Assert.Fail $"expected one release, got {other.Length}"
            | Error e -> Assert.Fail $"release list failed: {e.Message}"

            let preJson = """[{"tag-_name":"v2","title":"RC","status":"prerelease"}]"""
            let preForge = teaForge [ "releases"; "list" ] (Reply.Ok preJson)

            match! preForge.ReleaseList() with
            | Ok [ rel ] ->
                Assert.That(rel.Prerelease, Is.EqualTo(Some true), "status prerelease → Some true")
                Assert.That(rel.Draft, Is.EqualTo(Some false), "status prerelease → draft confirmed Some false")
            | Ok other -> Assert.Fail $"expected one release, got {other.Length}"
            | Error e -> Assert.Fail $"release list failed: {e.Message}"
        }

    // --- ForgePr/ForgeIssue Labels & Assignees — Some on GitHub/GitLab, None on Gitea ---

    [<Test>]
    member _.GitHubPrLabelsAndAssigneesAreConfirmed() : Task =
        task {
            // gh returns labels/assignees as arrays of objects (`[{"name": …}]`,
            // `[{"login": …}]`); the wrapper flattens them and reports a confirmed Some.
            let json =
                """[{"number":1,"title":"t","state":"OPEN","headRefName":"f","baseRefName":"main","url":"u","labels":[{"name":"bug"},{"name":"p1"}],"assignees":[{"login":"octocat"}]}]"""

            let forge = ghForge [ "pr"; "list"; "--json" ] (Reply.Ok json)

            match! forge.PrList() with
            | Ok [ pr ] ->
                Assert.That(pr.Labels, Is.EqualTo(Some [ "bug"; "p1" ]), "gh labels flattened from name → Some")
                Assert.That(pr.Assignees, Is.EqualTo(Some [ "octocat" ]), "gh assignees flattened from login → Some")
            | Ok other -> Assert.Fail $"expected one PR, got {other.Length}"
            | Error e -> Assert.Fail $"pr list failed: {e.Message}"

            // An empty labels/assignees array is a *confirmed* "none" → Some [], not None.
            let emptyJson =
                """[{"number":2,"title":"t","state":"OPEN","headRefName":"f","baseRefName":"main","url":"u","labels":[],"assignees":[]}]"""

            let emptyForge = ghForge [ "pr"; "list"; "--json" ] (Reply.Ok emptyJson)

            match! emptyForge.PrList() with
            | Ok [ pr ] ->
                Assert.That(pr.Labels, Is.EqualTo(Some List.empty<string>), "empty labels → confirmed Some []")
                Assert.That(pr.Assignees, Is.EqualTo(Some List.empty<string>), "empty assignees → confirmed Some []")
            | Ok other -> Assert.Fail $"expected one PR, got {other.Length}"
            | Error e -> Assert.Fail $"pr list failed: {e.Message}"
        }

    [<Test>]
    member _.GitLabMrLabelsAndAssigneesAreConfirmed() : Task =
        task {
            // GitLab's `labels` are already plain strings; `assignees` is an array of User
            // objects flattened to `username`. Both → a confirmed Some.
            let json =
                """[{"iid":1,"title":"t","state":"opened","source_branch":"s","target_branch":"main","web_url":"u","draft":false,"labels":["bug","confirmed"],"assignees":[{"username":"steiza"},{"username":"andyfeller"}]}]"""

            let forge = glForge [ "mr"; "list" ] (Reply.Ok json)

            match! forge.PrList() with
            | Ok [ pr ] ->
                Assert.That(
                    pr.Labels,
                    Is.EqualTo(Some [ "bug"; "confirmed" ]),
                    "GitLab labels are plain strings → Some"
                )

                Assert.That(
                    pr.Assignees,
                    Is.EqualTo(Some [ "steiza"; "andyfeller" ]),
                    "GitLab assignees flattened from username → Some"
                )
            | Ok other -> Assert.Fail $"expected one MR, got {other.Length}"
            | Error e -> Assert.Fail $"mr list failed: {e.Message}"

            let emptyJson =
                """[{"iid":2,"title":"t","state":"opened","source_branch":"s","target_branch":"main","web_url":"u","draft":false,"labels":[],"assignees":[]}]"""

            let emptyForge = glForge [ "mr"; "list" ] (Reply.Ok emptyJson)

            match! emptyForge.PrList() with
            | Ok [ pr ] ->
                Assert.That(pr.Labels, Is.EqualTo(Some List.empty<string>), "empty labels → confirmed Some []")
                Assert.That(pr.Assignees, Is.EqualTo(Some List.empty<string>), "empty assignees → confirmed Some []")
            | Ok other -> Assert.Fail $"expected one MR, got {other.Length}"
            | Error e -> Assert.Fail $"mr list failed: {e.Message}"
        }

    [<Test>]
    member _.GiteaPrLabelsAndAssigneesAreNone() : Task =
        task {
            // tea's PR table has no labels/assignees columns → honest None (unknown), not [].
            let json =
                """[{"index":"1","title":"t","state":"open","head":"f","base":"main","url":"u"}]"""

            let forge = teaForge [ "pr"; "list"; "--fields" ] (Reply.Ok json)

            match! forge.PrList() with
            | Ok [ pr ] ->
                Assert.That(pr.Labels, Is.EqualTo None, "Gitea PR labels unreported → None")
                Assert.That(pr.Assignees, Is.EqualTo None, "Gitea PR assignees unreported → None")
            | Ok other -> Assert.Fail $"expected one PR, got {other.Length}"
            | Error e -> Assert.Fail $"pr list failed: {e.Message}"
        }

    [<Test>]
    member _.GitHubIssueLabelsAndAssigneesAreConfirmed() : Task =
        task {
            let json =
                """[{"number":3,"title":"t","state":"OPEN","body":"b","url":"u","labels":[{"name":"docs"}],"assignees":[{"login":"andyfeller"}]}]"""

            let forge = ghForge [ "issue"; "list" ] (Reply.Ok json)

            match! forge.IssueList() with
            | Ok [ issue ] ->
                Assert.That(issue.Labels, Is.EqualTo(Some [ "docs" ]), "gh issue labels → Some")
                Assert.That(issue.Assignees, Is.EqualTo(Some [ "andyfeller" ]), "gh issue assignees → Some")
            | Ok other -> Assert.Fail $"expected one issue, got {other.Length}"
            | Error e -> Assert.Fail $"issue list failed: {e.Message}"

            let emptyJson =
                """[{"number":4,"title":"t","state":"OPEN","body":"b","url":"u","labels":[],"assignees":[]}]"""

            let emptyForge = ghForge [ "issue"; "list" ] (Reply.Ok emptyJson)

            match! emptyForge.IssueList() with
            | Ok [ issue ] ->
                Assert.That(issue.Labels, Is.EqualTo(Some List.empty<string>), "empty issue labels → Some []")
                Assert.That(issue.Assignees, Is.EqualTo(Some List.empty<string>), "empty issue assignees → Some []")
            | Ok other -> Assert.Fail $"expected one issue, got {other.Length}"
            | Error e -> Assert.Fail $"issue list failed: {e.Message}"
        }

    [<Test>]
    member _.GitLabIssueLabelsAndAssigneesAreConfirmed() : Task =
        task {
            let json =
                """[{"iid":1,"title":"t","state":"opened","description":"b","web_url":"u","labels":["bug"],"assignees":[{"username":"steiza"}]}]"""

            let forge = glForge [ "issue"; "list" ] (Reply.Ok json)

            match! forge.IssueList() with
            | Ok [ issue ] ->
                Assert.That(issue.Labels, Is.EqualTo(Some [ "bug" ]), "GitLab issue labels → Some")
                Assert.That(issue.Assignees, Is.EqualTo(Some [ "steiza" ]), "GitLab issue assignees → Some")
            | Ok other -> Assert.Fail $"expected one issue, got {other.Length}"
            | Error e -> Assert.Fail $"issue list failed: {e.Message}"

            let emptyJson =
                """[{"iid":2,"title":"t","state":"opened","description":"b","web_url":"u","labels":[],"assignees":[]}]"""

            let emptyForge = glForge [ "issue"; "list" ] (Reply.Ok emptyJson)

            match! emptyForge.IssueList() with
            | Ok [ issue ] ->
                Assert.That(issue.Labels, Is.EqualTo(Some List.empty<string>), "empty GitLab issue labels → Some []")

                Assert.That(
                    issue.Assignees,
                    Is.EqualTo(Some List.empty<string>),
                    "empty GitLab issue assignees → Some []"
                )
            | Ok other -> Assert.Fail $"expected one issue, got {other.Length}"
            | Error e -> Assert.Fail $"issue list failed: {e.Message}"
        }

    [<Test>]
    member _.GiteaIssueLabelsAndAssigneesAreNone() : Task =
        task {
            // tea's issue table has no labels/assignees columns → honest None (unknown).
            let json = """[{"index":"1","title":"t","state":"open","url":"u"}]"""
            let forge = teaForge [ "issues"; "list"; "--fields" ] (Reply.Ok json)

            match! forge.IssueList() with
            | Ok [ issue ] ->
                Assert.That(issue.Labels, Is.EqualTo None, "Gitea issue labels unreported → None")
                Assert.That(issue.Assignees, Is.EqualTo None, "Gitea issue assignees unreported → None")
            | Ok other -> Assert.Fail $"expected one issue, got {other.Length}"
            | Error e -> Assert.Fail $"issue list failed: {e.Message}"
        }

// ---------------------------------------------------------------------------
// Version gate on mutating operations + version/kind in Capabilities
// ---------------------------------------------------------------------------

[<TestFixture>]
type VersionGateTests() =

    // A GitHub-backed forge that answers `gh --version` with `banner` and any *other*
    // command (the gated op itself) via `Fallback` — so a permitted op succeeds.
    let ghForgeVersioned (banner: string) =
        Forge.FromGitHub(
            ".",
            VcsToolkit.GitHub.GitHub.WithRunner(
                ScriptedRunner()
                    .On([ "--version" ], Reply.Ok banner)
                    .Fallback(Reply.Ok "https://github.com/o/r/pull/1\n")
            )
        )

    [<Test>]
    member _.GatedWriteRefusedBelowFloorWithoutSpawningTheOp() : Task =
        task {
            // ONLY `--version` is scripted (no fallback): an empty ScriptedRunner RAISES on any
            // other spawn, so reaching UnsupportedVersion proves the op itself never spawned.
            let forge =
                Forge.FromGitHub(
                    ".",
                    VcsToolkit.GitHub.GitHub.WithRunner(
                        ScriptedRunner().On([ "--version" ], Reply.Ok "gh version 1.14.0\n")
                    )
                )

            match! forge.PrCreate(PrCreate.Create("t", "b")) with
            | Error(ForgeError.UnsupportedVersion(kind, op, found, minimum)) ->
                Assert.That(kind, Is.EqualTo ForgeKind.GitHub)
                Assert.That(op, Is.EqualTo "prCreate")
                Assert.That(found.ToString(), Is.EqualTo "1.14.0")
                Assert.That(minimum.ToString(), Is.EqualTo "2.0.0")
            | Error e -> Assert.Fail $"expected UnsupportedVersion, got: {e.Message}"
            | Ok _ -> Assert.Fail "a below-floor CLI must be refused before spawning the op"
        }

    [<Test>]
    member _.GatedWriteAllowedOnSupportedVersion() : Task =
        task {
            // gh 2.40 meets the floor → the op dispatches, returning the CLI's output unchanged.
            let forge = ghForgeVersioned "gh version 2.40.0\n"

            match! forge.PrCreate(PrCreate.Create("t", "b")) with
            | Ok out -> Assert.That(out, Does.Contain "pull/1")
            | Error e -> Assert.Fail $"a supported CLI must dispatch the op: {e.Message}"
        }

    [<Test>]
    member _.GatedWriteFailsOpenOnUnrecognisedVersion() : Task =
        task {
            // An unparseable `--version` can't *confirm* the CLI is too old → the op still runs
            // (fail-open): the gate only ever blocks a confirmed below-floor version.
            let forge = ghForgeVersioned "gh (dev build, no version)\n"

            match! forge.PrCreate(PrCreate.Create("t", "b")) with
            | Ok out -> Assert.That(out, Does.Contain "pull/1", "an unrecognised version must not block the op")
            | Error e -> Assert.Fail $"fail-open expected, got: {e.Message}"
        }

    [<Test>]
    member _.UnknownHandleGatedWriteStaysUnsupportedWithoutProbing() : Task =
        task {
            // The inert Unknown handle has no CLI: a gated op returns Unsupported (not
            // UnsupportedVersion) and spawns nothing — no version probe on an absent CLI.
            let forge = Forge.FromUnknown "."

            match! forge.PrCreate(PrCreate.Create("t", "b")) with
            | Error e -> Assert.That(e.IsUnsupported, Is.True, "Unknown backend → Unsupported, never a version probe")
            | Ok _ -> Assert.Fail "Unknown prCreate must be Unsupported"
        }

    [<Test>]
    member _.UnknownCapabilitiesHaveNoVersion() : Task =
        task {
            let forge = Forge.FromUnknown "."

            match! forge.Capabilities() with
            | Ok caps ->
                Assert.That(caps.Version, Is.EqualTo None, "no CLI → no version")
                Assert.That(caps.Kind, Is.EqualTo ForgeKind.Unknown)
            | Error e -> Assert.Fail $"capabilities failed: {e.Message}"
        }

// ---------------------------------------------------------------------------
// Per-handle caching of the version probe (VersionGate.gated / ensureVersion): the CLI's
// `--version` is spawned at most once per handle, however many gated calls run on it, and
// two separately-built handles never share that cache. GitHub is the showcase below;
// GitLab/Gitea route through the identical `ensureVersion`/`GitHubVersionProbe`-shaped
// caching (`GitLabVersionProbe`/`GiteaVersionProbe`), so their behaviour is mirrored, not
// re-demonstrated per backend.
// ---------------------------------------------------------------------------

[<TestFixture>]
type VersionProbeCachingTests() =

    // A GitHub-backed forge whose runner counts `--version` spawns via a `When` predicate
    // (as a side effect) while still answering the gated op itself through `Fallback` — so
    // several gated calls on the same handle can all succeed while the count is observed.
    let countingGitHubForge (banner: string) =
        let versionSpawns = ref 0

        let forge =
            Forge.FromGitHub(
                ".",
                VcsToolkit.GitHub.GitHub.WithRunner(
                    ScriptedRunner()
                        .When(
                            (fun (cmd: Command) ->
                                if cmd.Arguments |> Seq.contains "--version" then
                                    versionSpawns.Value <- versionSpawns.Value + 1
                                    true
                                else
                                    false),
                            Reply.Ok banner
                        )
                        .Fallback(Reply.Ok "https://github.com/o/r/pull/1\n")
                )
            )

        forge, versionSpawns

    [<Test>]
    member _.SeveralGatedCallsOnOneHandleSpawnVersionOnlyOnce() : Task =
        task {
            let forge, versionSpawns = countingGitHubForge "gh version 2.40.0\n"

            match! forge.PrCreate(PrCreate.Create("t", "b")) with
            | Error e -> Assert.Fail $"prCreate failed: {e.Message}"
            | Ok _ -> ()

            match! forge.PrEdit(1UL, PrEdit.Create().WithTitle "t2") with
            | Error e -> Assert.Fail $"prEdit failed: {e.Message}"
            | Ok _ -> ()

            match! forge.IssueCreate("t", "b") with
            | Error e -> Assert.Fail $"issueCreate failed: {e.Message}"
            | Ok _ -> ()

            Assert.That(
                versionSpawns.Value,
                Is.EqualTo 1,
                "three gated calls on the same handle must probe `--version` only once"
            )
        }

    [<Test>]
    member _.CapabilitiesReusesTheSameCacheAsGatedCalls() : Task =
        task {
            // `Capabilities()` is documented to reuse the handle's cache rather than probing
            // independently — a gated call followed by `Capabilities()` still spawns once.
            let forge, versionSpawns = countingGitHubForge "gh version 2.40.0\n"

            match! forge.PrCreate(PrCreate.Create("t", "b")) with
            | Error e -> Assert.Fail $"prCreate failed: {e.Message}"
            | Ok _ -> ()

            match! forge.Capabilities() with
            | Error e -> Assert.Fail $"capabilities failed: {e.Message}"
            | Ok caps ->
                match caps.Version with
                | Some v -> Assert.That(v.ToString(), Is.EqualTo "2.40.0")
                | None -> Assert.Fail "expected a parsed version"

            Assert.That(versionSpawns.Value, Is.EqualTo 1, "Capabilities() must reuse the cached probe, not re-spawn")
        }

    [<Test>]
    member _.TwoSeparatelyBuiltHandlesDoNotShareTheCache() : Task =
        task {
            // Two independently-constructed handles (distinct clients/runners) each probe
            // `--version` on their own first gated call — no global/static sharing.
            let forgeA, spawnsA = countingGitHubForge "gh version 2.40.0\n"
            let forgeB, spawnsB = countingGitHubForge "gh version 2.40.0\n"

            match! forgeA.PrCreate(PrCreate.Create("t", "b")) with
            | Error e -> Assert.Fail $"forgeA prCreate failed: {e.Message}"
            | Ok _ -> ()

            match! forgeB.PrCreate(PrCreate.Create("t", "b")) with
            | Error e -> Assert.Fail $"forgeB prCreate failed: {e.Message}"
            | Ok _ -> ()

            Assert.That(spawnsA.Value, Is.EqualTo 1, "forgeA's own probe")
            Assert.That(spawnsB.Value, Is.EqualTo 1, "forgeB's own probe, independent of forgeA's")
        }

    [<Test>]
    member _.AtSiblingSharesTheParentHandlesCache() : Task =
        task {
            // `.At` rebinds the directory but reuses this handle's `Backend` (client + cache)
            // — a gated call on the sibling must not re-spawn `--version`.
            let forge, versionSpawns = countingGitHubForge "gh version 2.40.0\n"
            let sibling = forge.At "../other"

            match! forge.PrCreate(PrCreate.Create("t", "b")) with
            | Error e -> Assert.Fail $"prCreate on the original handle failed: {e.Message}"
            | Ok _ -> ()

            match! sibling.PrCreate(PrCreate.Create("t", "b")) with
            | Error e -> Assert.Fail $"prCreate on the `.At` sibling failed: {e.Message}"
            | Ok _ -> ()

            Assert.That(versionSpawns.Value, Is.EqualTo 1, "an `.At` sibling shares the parent's cached probe")
        }

// ---------------------------------------------------------------------------
// Unified merge-spec: auto/delete-branch reach gh flags on GitHub; are refused
// as Unsupported on GitLab/Gitea before spawning; plain strategies still merge.
// ---------------------------------------------------------------------------

[<TestFixture>]
type PrMergeSpecTests() =

    let isUnsupported (t: Task<Result<'T, ForgeError>>) =
        task {
            let! r = t

            return
                match r with
                | Error e -> e.IsUnsupported
                | Ok _ -> false
        }

    [<Test>]
    member _.GitHubAutoAndDeleteBranchReachGhFlags() : Task =
        task {
            // ONLY `--version` and the EXACT expected merge argv are scripted (no fallback): an
            // unmatched spawn raises, so an `Ok` proves `--auto` and `--delete-branch` both
            // reached the gh command line — not silently dropped.
            let forge =
                Forge.FromGitHub(
                    ".",
                    VcsToolkit.GitHub.GitHub.WithRunner(
                        ScriptedRunner()
                            .On([ "--version" ], Reply.Ok "gh version 2.40.0\n")
                            .On([ "pr"; "merge"; "1"; "--merge"; "--auto"; "--delete-branch" ], Reply.Exit 0)
                    )
                )

            match! forge.PrMerge(1UL, PrMerge.Merge.WithAuto().WithDeleteBranch()) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"auto/delete-branch must reach the gh flags: {e.Message}"
        }

    [<Test>]
    member _.GitHubPlainStrategyEmitsNoAutoOrDeleteBranchFlags() : Task =
        task {
            // Guard rules fail the run if either flag leaks in; a plain squash must match only
            // the flag-free rule, proving the defaults add nothing.
            let forge =
                Forge.FromGitHub(
                    ".",
                    VcsToolkit.GitHub.GitHub.WithRunner(
                        ScriptedRunner()
                            .On([ "--version" ], Reply.Ok "gh version 2.40.0\n")
                            .On([ "--auto" ], Reply.Exit 1)
                            .On([ "--delete-branch" ], Reply.Exit 1)
                            .On([ "pr"; "merge"; "1"; "--squash" ], Reply.Exit 0)
                    )
                )

            match! forge.PrMerge(1UL, PrMerge.Squash) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"a plain strategy must add no auto/delete-branch flag: {e.Message}"
        }

    [<Test>]
    member _.GitLabRefusesAutoAndDeleteBranchWithoutSpawning() : Task =
        task {
            // An empty ScriptedRunner RAISES on any spawn — so reaching Unsupported proves the
            // facade refuses auto/delete-branch structurally, before even the version probe.
            let forge =
                Forge.FromGitLab(".", VcsToolkit.GitLab.GitLab.WithRunner(ScriptedRunner()))

            let! auto = isUnsupported (forge.PrMerge(1UL, PrMerge.Merge.WithAuto()))
            let! del = isUnsupported (forge.PrMerge(1UL, PrMerge.Squash.WithDeleteBranch()))
            Assert.That(auto, Is.True, "GitLab auto-merge must be Unsupported without spawning")
            Assert.That(del, Is.True, "GitLab delete-branch must be Unsupported without spawning")
        }

    [<Test>]
    member _.GiteaRefusesAutoAndDeleteBranchWithoutSpawning() : Task =
        task {
            let forge =
                Forge.FromGitea(".", VcsToolkit.Gitea.Gitea.WithRunner(ScriptedRunner()))

            let! auto = isUnsupported (forge.PrMerge(1UL, PrMerge.Rebase.WithAuto()))
            let! del = isUnsupported (forge.PrMerge(1UL, PrMerge.Merge.WithDeleteBranch()))
            Assert.That(auto, Is.True, "Gitea auto-merge must be Unsupported without spawning")
            Assert.That(del, Is.True, "Gitea delete-branch must be Unsupported without spawning")
        }

    [<Test>]
    member _.GitLabPlainStrategyStillMerges() : Task =
        task {
            // A plain strategy (no auto/delete-branch) dispatches as before to `glab mr merge`.
            let forge =
                Forge.FromGitLab(
                    ".",
                    VcsToolkit.GitLab.GitLab.WithRunner(
                        ScriptedRunner()
                            .On([ "--version" ], Reply.Ok "glab 1.40.0\n")
                            .On([ "mr"; "merge"; "1"; "--squash" ], Reply.Exit 0)
                    )
                )

            match! forge.PrMerge(1UL, PrMerge.Squash) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"a plain GitLab strategy merge must still work: {e.Message}"
        }

    [<Test>]
    member _.GiteaPlainStrategyStillMerges() : Task =
        task {
            let forge =
                Forge.FromGitea(
                    ".",
                    VcsToolkit.Gitea.Gitea.WithRunner(
                        ScriptedRunner()
                            .On([ "--version" ], Reply.Ok "tea version 0.9.2\n")
                            .On([ "pr"; "merge"; "1"; "--style"; "rebase" ], Reply.Exit 0)
                    )
                )

            match! forge.PrMerge(1UL, PrMerge.Rebase) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"a plain Gitea strategy merge must still work: {e.Message}"
        }

// ---------------------------------------------------------------------------
// PR close support: GitHub implements delete-branch; GitLab/Gitea reject that request
// structurally before spawning, while branch-preserving closes remain available everywhere.
// ---------------------------------------------------------------------------

[<TestFixture>]
type PrCloseSupportTests() =

    let isCloseUnsupported (kind: ForgeKind) (t: Task<Result<'T, ForgeError>>) =
        task {
            let! r = t

            return
                match r with
                | Error(ForgeError.Unsupported(actualKind, "prClose delete-branch")) -> actualKind = kind
                | Error _
                | Ok _ -> false
        }

    [<Test>]
    member _.GitHubCloseWithDeleteBranchReachesGhFlag() : Task =
        task {
            let forge =
                Forge.FromGitHub(
                    ".",
                    VcsToolkit.GitHub.GitHub.WithRunner(
                        ScriptedRunner().On([ "pr"; "close"; "1"; "--delete-branch" ], Reply.Exit 0)
                    )
                )

            match! forge.PrClose(1UL, true) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"GitHub delete-branch must reach the gh flag: {e.Message}"
        }

    [<Test>]
    member _.GitHubCloseWithoutDeleteBranchKeepsExistingBehavior() : Task =
        task {
            let forge =
                Forge.FromGitHub(
                    ".",
                    VcsToolkit.GitHub.GitHub.WithRunner(ScriptedRunner().On([ "pr"; "close"; "2" ], Reply.Exit 0))
                )

            match! forge.PrClose(2UL, false) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"GitHub close without delete-branch must still work: {e.Message}"
        }

    [<Test>]
    member _.GitLabCloseWithDeleteBranchIsUnsupportedWithoutSpawning() : Task =
        task {
            let forge =
                Forge.FromGitLab(".", VcsToolkit.GitLab.GitLab.WithRunner(ScriptedRunner()))

            let! unsupported = isCloseUnsupported ForgeKind.GitLab (forge.PrClose(1UL, true))
            Assert.That(unsupported, Is.True, "GitLab delete-branch must be Unsupported without spawning")
        }

    [<Test>]
    member _.GitLabCloseWithoutDeleteBranchStillWorks() : Task =
        task {
            let forge =
                Forge.FromGitLab(
                    ".",
                    VcsToolkit.GitLab.GitLab.WithRunner(ScriptedRunner().On([ "mr"; "close"; "1" ], Reply.Exit 0))
                )

            match! forge.PrClose(1UL, false) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"GitLab close without delete-branch must still work: {e.Message}"
        }

    [<Test>]
    member _.GiteaCloseWithDeleteBranchIsUnsupportedWithoutSpawning() : Task =
        task {
            let forge =
                Forge.FromGitea(".", VcsToolkit.Gitea.Gitea.WithRunner(ScriptedRunner()))

            let! unsupported = isCloseUnsupported ForgeKind.Gitea (forge.PrClose(1UL, true))
            Assert.That(unsupported, Is.True, "Gitea delete-branch must be Unsupported without spawning")
        }

    [<Test>]
    member _.GiteaCloseWithoutDeleteBranchStillWorks() : Task =
        task {
            let forge =
                Forge.FromGitea(
                    ".",
                    VcsToolkit.Gitea.Gitea.WithRunner(ScriptedRunner().On([ "pr"; "close"; "1" ], Reply.Exit 0))
                )

            match! forge.PrClose(1UL, false) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Gitea close without delete-branch must still work: {e.Message}"
        }
// ---------------------------------------------------------------------------
// Local-worktree checkout: each backend dispatches to its native checkout subcommand;
// the CLI-less Unknown handle is Unsupported without spawning. (Checkout is supported on
// all three CLIs, so it is NOT a capability-varying ForgeOp — unlike prMarkReady.)
// ---------------------------------------------------------------------------

[<TestFixture>]
type PrCheckoutTests() =

    [<Test>]
    member _.GitHubDispatchesToPrCheckout() : Task =
        task {
            // No fallback: an empty ScriptedRunner RAISES on any unmatched spawn, so reaching
            // Ok proves `gh pr checkout 1` was the exact argv dispatched.
            let forge =
                Forge.FromGitHub(
                    ".",
                    VcsToolkit.GitHub.GitHub.WithRunner(ScriptedRunner().On([ "pr"; "checkout"; "1" ], Reply.Exit 0))
                )

            match! forge.PrCheckout 1UL with
            | Ok() -> ()
            | Error e -> Assert.Fail $"gh pr checkout must dispatch: {e.Message}"
        }

    [<Test>]
    member _.GitLabDispatchesToMrCheckout() : Task =
        task {
            let forge =
                Forge.FromGitLab(
                    ".",
                    VcsToolkit.GitLab.GitLab.WithRunner(ScriptedRunner().On([ "mr"; "checkout"; "2" ], Reply.Exit 0))
                )

            match! forge.PrCheckout 2UL with
            | Ok() -> ()
            | Error e -> Assert.Fail $"glab mr checkout must dispatch: {e.Message}"
        }

    [<Test>]
    member _.GiteaDispatchesToPrCheckout() : Task =
        task {
            // Gitea SUPPORTS checkout (unlike prMarkReady/prChecks) — it must dispatch, not
            // return Unsupported.
            let forge =
                Forge.FromGitea(
                    ".",
                    VcsToolkit.Gitea.Gitea.WithRunner(ScriptedRunner().On([ "pr"; "checkout"; "3" ], Reply.Exit 0))
                )

            match! forge.PrCheckout 3UL with
            | Ok() -> ()
            | Error e -> Assert.Fail $"tea pr checkout must dispatch: {e.Message}"
        }

    [<Test>]
    member _.UnknownHandleIsUnsupportedWithoutSpawning() : Task =
        task {
            // The inert Unknown handle has no CLI — checkout is Unsupported and spawns nothing.
            let forge = Forge.FromUnknown "."

            match! forge.PrCheckout 1UL with
            | Error e -> Assert.That(e.IsUnsupported, Is.True, "Unknown prCheckout must be Unsupported")
            | Ok() -> Assert.Fail "Unknown prCheckout must be Unsupported"
        }

// ---------------------------------------------------------------------------
// Issue lifecycle: close + comment dispatch to each backend's native subcommand, the
// empty-body refusal precedes any spawn, and both ops are version-gated like issueCreate.
// All three CLIs support both ops, so they are NOT capability-varying — only the CLI-less
// Unknown handle is Unsupported without spawning.
// ---------------------------------------------------------------------------

[<TestFixture>]
type IssueLifecycleTests() =

    // Each op is version-gated, so a happy-path handle must also answer `--version`; a
    // supported banner clears the gate and lets the op dispatch.
    let ghIssueForge (opTokens: string list) (opReply: Reply) =
        Forge.FromGitHub(
            ".",
            VcsToolkit.GitHub.GitHub.WithRunner(
                ScriptedRunner().On([ "--version" ], Reply.Ok "gh version 2.40.0\n").On(opTokens, opReply)
            )
        )

    let glIssueForge (opTokens: string list) (opReply: Reply) =
        Forge.FromGitLab(
            ".",
            VcsToolkit.GitLab.GitLab.WithRunner(
                ScriptedRunner().On([ "--version" ], Reply.Ok "glab 1.36.0\n").On(opTokens, opReply)
            )
        )

    let teaIssueForge (opTokens: string list) (opReply: Reply) =
        Forge.FromGitea(
            ".",
            VcsToolkit.Gitea.Gitea.WithRunner(
                ScriptedRunner().On([ "--version" ], Reply.Ok "tea version 0.9.2\n").On(opTokens, opReply)
            )
        )

    [<Test>]
    member _.GitHubDispatchesIssueClose() : Task =
        task {
            // No fallback: reaching Ok proves `gh issue close 1` was the dispatched argv.
            let forge = ghIssueForge [ "issue"; "close"; "1" ] (Reply.Exit 0)

            match! forge.IssueClose 1UL with
            | Ok() -> ()
            | Error e -> Assert.Fail $"gh issue close must dispatch: {e.Message}"
        }

    [<Test>]
    member _.GitLabDispatchesIssueClose() : Task =
        task {
            let forge = glIssueForge [ "issue"; "close"; "2" ] (Reply.Exit 0)

            match! forge.IssueClose 2UL with
            | Ok() -> ()
            | Error e -> Assert.Fail $"glab issue close must dispatch: {e.Message}"
        }

    [<Test>]
    member _.GiteaDispatchesIssueClose() : Task =
        task {
            let forge = teaIssueForge [ "issues"; "close"; "3" ] (Reply.Exit 0)

            match! forge.IssueClose 3UL with
            | Ok() -> ()
            | Error e -> Assert.Fail $"tea issues close must dispatch: {e.Message}"
        }

    [<Test>]
    member _.GitHubDispatchesIssueCommentReturningOutput() : Task =
        task {
            let forge =
                ghIssueForge [ "issue"; "comment"; "1"; "--body"; "thanks" ] (Reply.Ok "https://c/1\n")

            match! forge.IssueComment(1UL, "thanks") with
            | Ok out -> Assert.That(out, Does.Contain "https://c/1")
            | Error e -> Assert.Fail $"gh issue comment must dispatch: {e.Message}"
        }

    [<Test>]
    member _.GitLabDispatchesIssueComment() : Task =
        task {
            let forge =
                glIssueForge [ "issue"; "note"; "2"; "-m"; "hi" ] (Reply.Ok "https://gl/note/1\n")

            match! forge.IssueComment(2UL, "hi") with
            | Ok out -> Assert.That(out, Does.Contain "note/1")
            | Error e -> Assert.Fail $"glab issue note must dispatch: {e.Message}"
        }

    [<Test>]
    member _.GiteaDispatchesIssueComment() : Task =
        task {
            let forge = teaIssueForge [ "comment"; "3"; "nice" ] (Reply.Ok "commented\n")

            match! forge.IssueComment(3UL, "nice") with
            | Ok out -> Assert.That(out, Does.Contain "commented")
            | Error e -> Assert.Fail $"tea comment must dispatch: {e.Message}"
        }

    [<Test>]
    member _.IssueCommentRejectsEmptyBodyBeforeSpawning() : Task =
        task {
            // A whitespace-only body is refused with InvalidInput before any spawn — so a
            // Fallback that would fail loudly is never reached.
            let forge =
                Forge.FromGitHub(".", VcsToolkit.GitHub.GitHub.WithRunner(ScriptedRunner().Fallback(Reply.Ok "")))

            match! forge.IssueComment(1UL, "   ") with
            | Error e -> Assert.That(e.Message, Does.Contain "empty")
            | Ok _ -> Assert.Fail "a whitespace-only comment body must be refused before spawning"
        }

    [<Test>]
    member _.IssueCloseAndCommentRefusedBelowFloorWithoutSpawningTheOp() : Task =
        task {
            // ONLY `--version` is scripted (no fallback): an empty ScriptedRunner RAISES on any
            // other spawn, so reaching UnsupportedVersion proves the op itself never spawned.
            let below () =
                Forge.FromGitHub(
                    ".",
                    VcsToolkit.GitHub.GitHub.WithRunner(
                        ScriptedRunner().On([ "--version" ], Reply.Ok "gh version 1.14.0\n")
                    )
                )

            match! below().IssueClose 1UL with
            | Error(ForgeError.UnsupportedVersion(_, op, _, _)) -> Assert.That(op, Is.EqualTo "issueClose")
            | Error e -> Assert.Fail $"expected UnsupportedVersion for issueClose, got: {e.Message}"
            | Ok _ -> Assert.Fail "a below-floor CLI must refuse issueClose before spawning the op"

            match! below().IssueComment(1UL, "body") with
            | Error(ForgeError.UnsupportedVersion(_, op, _, _)) -> Assert.That(op, Is.EqualTo "issueComment")
            | Error e -> Assert.Fail $"expected UnsupportedVersion for issueComment, got: {e.Message}"
            | Ok _ -> Assert.Fail "a below-floor CLI must refuse issueComment before spawning the op"
        }

    [<Test>]
    member _.UnknownHandleIssueCloseAndCommentAreUnsupportedWithoutSpawning() : Task =
        task {
            // The inert Unknown handle has no CLI — both ops are Unsupported (never a version
            // probe) and spawn nothing.
            let forge = Forge.FromUnknown "."

            match! forge.IssueClose 1UL with
            | Error e -> Assert.That(e.IsUnsupported, Is.True, "Unknown issueClose must be Unsupported")
            | Ok() -> Assert.Fail "Unknown issueClose must be Unsupported"

            match! forge.IssueComment(1UL, "body") with
            | Error e -> Assert.That(e.IsUnsupported, Is.True, "Unknown issueComment must be Unsupported")
            | Ok _ -> Assert.Fail "Unknown issueComment must be Unsupported"
        }

// ---------------------------------------------------------------------------
// PR review: approve is supported on all three; request-changes on GitHub/Gitea
// (Unsupported on GitLab, where glab has no equivalent); a comment-review only on GitHub
// (Unsupported on GitLab/Gitea). Every unsupported combination is refused structurally,
// before any spawn — including the version probe; the supported paths are version-gated
// like the other mutations.
// ---------------------------------------------------------------------------

[<TestFixture>]
type PrReviewTests() =

    // Each op is version-gated, so a happy-path handle must also answer `--version`; a
    // supported banner clears the gate and lets the op dispatch.
    let ghReviewForge (opTokens: string list) (opReply: Reply) =
        Forge.FromGitHub(
            ".",
            VcsToolkit.GitHub.GitHub.WithRunner(
                ScriptedRunner().On([ "--version" ], Reply.Ok "gh version 2.40.0\n").On(opTokens, opReply)
            )
        )

    let glReviewForge (opTokens: string list) (opReply: Reply) =
        Forge.FromGitLab(
            ".",
            VcsToolkit.GitLab.GitLab.WithRunner(
                ScriptedRunner().On([ "--version" ], Reply.Ok "glab 1.36.0\n").On(opTokens, opReply)
            )
        )

    let teaReviewForge (opTokens: string list) (opReply: Reply) =
        Forge.FromGitea(
            ".",
            VcsToolkit.Gitea.Gitea.WithRunner(
                ScriptedRunner().On([ "--version" ], Reply.Ok "tea version 0.9.2\n").On(opTokens, opReply)
            )
        )

    let isUnsupported (kind: ForgeKind) (t: Task<Result<'T, ForgeError>>) =
        task {
            let! r = t

            return
                match r with
                | Error(ForgeError.Unsupported(actualKind, _)) -> actualKind = kind
                | Error _
                | Ok _ -> false
        }

    // --- Approve: a real verb on all three backends ---

    [<Test>]
    member _.GitHubApproveDispatchesReviewApprove() : Task =
        task {
            // No op fallback: reaching Ok proves `gh pr review 1 --approve` was the dispatched argv.
            let forge = ghReviewForge [ "pr"; "review"; "1"; "--approve" ] (Reply.Exit 0)

            match! forge.PrReview(1UL, ReviewAction.Approve) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"gh pr review --approve must dispatch: {e.Message}"
        }

    [<Test>]
    member _.GitLabApproveDispatchesMrApprove() : Task =
        task {
            let forge = glReviewForge [ "mr"; "approve"; "1" ] (Reply.Exit 0)

            match! forge.PrReview(1UL, ReviewAction.Approve) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"glab mr approve must dispatch: {e.Message}"
        }

    [<Test>]
    member _.GiteaApproveDispatchesPrApprove() : Task =
        task {
            let forge = teaReviewForge [ "pr"; "approve"; "1" ] (Reply.Exit 0)

            match! forge.PrReview(1UL, ReviewAction.Approve) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"tea pr approve must dispatch: {e.Message}"
        }

    // --- Request-changes: GitHub (--request-changes) + Gitea (pr reject); Unsupported on GitLab ---

    [<Test>]
    member _.GitHubRequestChangesDispatchesWithBody() : Task =
        task {
            let forge =
                ghReviewForge [ "pr"; "review"; "1"; "--request-changes"; "--body"; "fix" ] (Reply.Exit 0)

            match! forge.PrReview(1UL, ReviewAction.RequestChanges "fix") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"gh pr review --request-changes must dispatch: {e.Message}"
        }

    [<Test>]
    member _.GiteaRequestChangesDispatchesReject() : Task =
        task {
            let forge = teaReviewForge [ "pr"; "reject"; "1"; "fix" ] (Reply.Exit 0)

            match! forge.PrReview(1UL, ReviewAction.RequestChanges "fix") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"tea pr reject must dispatch: {e.Message}"
        }

    [<Test>]
    member _.GitLabRequestChangesIsUnsupportedWithoutSpawning() : Task =
        task {
            // An empty ScriptedRunner RAISES on any spawn — reaching Unsupported proves the facade
            // refuses request-changes structurally, before even the version probe.
            let forge =
                Forge.FromGitLab(".", VcsToolkit.GitLab.GitLab.WithRunner(ScriptedRunner()))

            let! unsupported = isUnsupported ForgeKind.GitLab (forge.PrReview(1UL, ReviewAction.RequestChanges "fix"))
            Assert.That(unsupported, Is.True, "GitLab request-changes must be Unsupported without spawning")
        }

    // --- Comment-review: GitHub only (--comment); Unsupported on GitLab/Gitea ---

    [<Test>]
    member _.GitHubCommentReviewDispatchesWithBody() : Task =
        task {
            let forge =
                ghReviewForge [ "pr"; "review"; "1"; "--comment"; "--body"; "note" ] (Reply.Exit 0)

            match! forge.PrReview(1UL, ReviewAction.Comment "note") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"gh pr review --comment must dispatch: {e.Message}"
        }

    [<Test>]
    member _.GitLabAndGiteaCommentReviewAreUnsupportedWithoutSpawning() : Task =
        task {
            let gl =
                Forge.FromGitLab(".", VcsToolkit.GitLab.GitLab.WithRunner(ScriptedRunner()))

            let tea = Forge.FromGitea(".", VcsToolkit.Gitea.Gitea.WithRunner(ScriptedRunner()))

            let! glUnsupported = isUnsupported ForgeKind.GitLab (gl.PrReview(1UL, ReviewAction.Comment "note"))
            let! teaUnsupported = isUnsupported ForgeKind.Gitea (tea.PrReview(1UL, ReviewAction.Comment "note"))
            Assert.That(glUnsupported, Is.True, "GitLab comment review must be Unsupported without spawning")
            Assert.That(teaUnsupported, Is.True, "Gitea comment review must be Unsupported without spawning")
        }

    // --- Unknown handle + version floor ---

    [<Test>]
    member _.UnknownHandleReviewIsUnsupportedWithoutSpawning() : Task =
        task {
            let forge = Forge.FromUnknown "."

            let! unsupported = isUnsupported ForgeKind.Unknown (forge.PrReview(1UL, ReviewAction.Approve))
            Assert.That(unsupported, Is.True, "Unknown prReview must be Unsupported")
        }

    [<Test>]
    member _.ReviewRefusedBelowFloorWithoutSpawningTheOp() : Task =
        task {
            // ONLY `--version` is scripted (no op rule): an empty ScriptedRunner RAISES on any
            // other spawn, so reaching UnsupportedVersion proves the op itself never spawned.
            let forge =
                Forge.FromGitHub(
                    ".",
                    VcsToolkit.GitHub.GitHub.WithRunner(
                        ScriptedRunner().On([ "--version" ], Reply.Ok "gh version 1.14.0\n")
                    )
                )

            match! forge.PrReview(1UL, ReviewAction.Approve) with
            | Error(ForgeError.UnsupportedVersion(_, op, _, _)) -> Assert.That(op, Is.EqualTo "prReview")
            | Error e -> Assert.Fail $"expected UnsupportedVersion, got: {e.Message}"
            | Ok() -> Assert.Fail "a below-floor CLI must refuse prReview before spawning the op"
        }
