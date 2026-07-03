module VcsToolkit.Forge.Tests

open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.Forge

// Forge handles over a scripted runner, per backend.
let private ghForge (tokens: string list) (reply: Reply) =
    Forge.FromGitHub(".", VcsToolkit.GitHub.GitHub.WithRunner(ScriptedRunner().On(tokens, reply)))

let private glForge (tokens: string list) (reply: Reply) =
    Forge.FromGitLab(".", VcsToolkit.GitLab.GitLab.WithRunner(ScriptedRunner().On(tokens, reply)))

let private teaForge (tokens: string list) (reply: Reply) =
    Forge.FromGitea(".", VcsToolkit.Gitea.Gitea.WithRunner(ScriptedRunner().On(tokens, reply)))

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
        Assert.That(ForgeOp.All.Length, Is.EqualTo 4)
        Assert.That(List.contains ForgeOp.PrChecks ForgeOp.All, Is.True)

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

        let gh = ghForge [ "pr"; "list" ] (Reply.Ok "[]")
        Assert.That(gh.Kind, Is.EqualTo ForgeKind.GitHub)
        Assert.That(gh.Supports ForgeOp.PrChecks, Is.True)
        Assert.That(gh.Cwd, Is.EqualTo ".")

        let tea = teaForge [ "pr"; "list" ] (Reply.Ok "[]")
        // Gitea supports NONE of the varying ops.
        Assert.That(tea.Supports ForgeOp.RepoView, Is.False)
        Assert.That(tea.Supports ForgeOp.PrChecks, Is.False)
        Assert.That(tea.Supports ForgeOp.ReleaseView, Is.False)

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
                Assert.That(pr.Draft, Is.True, "GitLab reports draft on the lean surface")
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
            let! d = isUnsupported (forge.PrMerge(1UL, MergeStrategy.Merge))
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
                Assert.That(repo.Private, Is.False, "absent visibility → not private (conservative)")
            | Error err -> Assert.Fail $"repo view failed: {err.Message}"
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
            // Authed GitHub → the full ships-the-command map, all true.
            let authed = ghForge [ "auth"; "status" ] (Reply.Exit 0)

            match! authed.Capabilities() with
            | Ok caps ->
                Assert.That(caps.Authed, Is.True)
                Assert.That(caps.PrCreate, Is.True)
                Assert.That(caps.PrChecks, Is.True)
            | Error e -> Assert.Fail $"capabilities failed: {e.Message}"

            // Unauthenticated → every per-op flag zeroed.
            let unauthed = ghForge [ "auth"; "status" ] (Reply.Exit 1)

            match! unauthed.Capabilities() with
            | Ok caps ->
                Assert.That(caps.Authed, Is.False)
                Assert.That(caps.PrCreate, Is.False)
            | Error e -> Assert.Fail $"capabilities failed: {e.Message}"

            // Gitea's static map has no checks command.
            let tea = teaForge [ "login"; "list" ] (Reply.Ok """[{"name":"g"}]""")

            match! tea.Capabilities() with
            | Ok caps ->
                Assert.That(caps.Authed, Is.True)
                Assert.That(caps.PrChecks, Is.False, "tea has no checks command")
                Assert.That(caps.PrCreate, Is.True)
            | Error e -> Assert.Fail $"capabilities failed: {e.Message}"
        }
