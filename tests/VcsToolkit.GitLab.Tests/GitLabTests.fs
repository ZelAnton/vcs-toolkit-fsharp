module VcsToolkit.GitLab.Tests

open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.GitLab

let private scripted (tokens: string list) (reply: Reply) =
    GitLab.WithRunner(ScriptedRunner().On(tokens, reply))

// A runner that answers any command with Ok "" — for verifying that a guard refuses
// BEFORE anything spawns (a refusal returns Error; a leak returns Ok).
let private permissive () =
    GitLab.WithRunner(ScriptedRunner().Fallback(Reply.Ok ""))

// A runner that records the exact argv (order + presence) of the command it answers,
// so a test can assert flag ABSENCE and exact values — which subset `.On` cannot.
let private capturing (reply: Reply) : GitLab * ResizeArray<string> =
    let args = ResizeArray<string>()

    let runner =
        ScriptedRunner()
            .When(
                (fun (cmd: Command) ->
                    args.Clear()
                    args.AddRange cmd.Arguments
                    true),
                reply
            )

    GitLab.WithRunner runner, args

let private assertArgs (expected: string list) (actual: ResizeArray<string>) =
    let got = List.ofSeq actual
    Assert.That(got.Length, Is.EqualTo expected.Length, $"argv length: expected %A{expected}, got %A{got}")
    List.iter2 (fun (e: string) (a: string) -> Assert.That(a, Is.EqualTo e)) expected got

let private expectOk (r: Result<'T, string>) : 'T =
    match r with
    | Ok v -> v
    | Error e ->
        Assert.Fail $"unexpected parse error: {e}"
        failwith "unreachable"

// ---------------------------------------------------------------------------
// Pure parsers over `glab … --output json` (GitLab's REST JSON)
// ---------------------------------------------------------------------------

[<TestFixture>]
type ParseTests() =

    [<Test>]
    member _.MrListParsesFieldsAndDraft() =
        let json =
            """[{"iid":12,"title":"Add feature","state":"opened","source_branch":"feat/x","target_branch":"main","web_url":"https://gl/mr/12","draft":false}]"""

        match expectOk (GitLabParse.parseMrList json) with
        | [ mr ] ->
            Assert.That(mr.Iid, Is.EqualTo 12UL)
            Assert.That(mr.State, Is.EqualTo "opened")
            Assert.That(mr.SourceBranch, Is.EqualTo "feat/x")
            Assert.That(mr.TargetBranch, Is.EqualTo "main")
            Assert.That(mr.Url, Is.EqualTo "https://gl/mr/12")
            Assert.That(mr.Draft, Is.False)
        | other -> Assert.Fail $"expected one MR, got {other.Length}"

    [<Test>]
    member _.MrToleratesMissingOptionalFields() =
        let json = """{"iid":5,"title":"wip","state":"opened","draft":true}"""
        let mr = expectOk (GitLabParse.parseMr json)
        Assert.That(mr.SourceBranch, Is.EqualTo "")
        Assert.That(mr.Url, Is.EqualTo "")
        Assert.That(mr.Draft, Is.True)

    [<Test>]
    member _.MrReadsNullBranchesAsEmpty() =
        // GitLab sends a present `null` for an empty optional field.
        let json =
            """{"iid":3,"title":"t","state":"opened","source_branch":null,"target_branch":null,"web_url":null}"""

        let mr = expectOk (GitLabParse.parseMr json)
        Assert.That(mr.SourceBranch, Is.EqualTo "")
        Assert.That(mr.TargetBranch, Is.EqualTo "")

    [<Test>]
    member _.IssueParsesIidAsNumberAndDescriptionAsBody() =
        let json =
            """[{"iid":1,"title":"Fix bug","state":"opened","description":"the body","web_url":"https://gl/i/1"}]"""

        match expectOk (GitLabParse.parseIssueList json) with
        | [ issue ] ->
            Assert.That(issue.Number, Is.EqualTo 1UL)
            Assert.That(issue.Body, Is.EqualTo "the body")
            Assert.That(issue.Url, Is.EqualTo "https://gl/i/1")
        | other -> Assert.Fail $"expected one issue, got {other.Length}"

    [<Test>]
    member _.ProjectFlattensAndVisibilityIsSome() =
        let json =
            """{"name":"cli","path_with_namespace":"gitlab-org/cli","default_branch":"main","web_url":"https://gl/p","visibility":"public"}"""

        let p = expectOk (GitLabParse.parseRepoView json)
        Assert.That(p.Name, Is.EqualTo "cli")
        Assert.That(p.PathWithNamespace, Is.EqualTo "gitlab-org/cli")
        Assert.That(p.DefaultBranch, Is.EqualTo "main")
        Assert.That(p.Visibility, Is.EqualTo(Some "public"))

    [<Test>]
    member _.ProjectMissingVisibilityIsNone() =
        // glab omits `visibility` for some responses; it must read as None (unknown),
        // never a default a consumer could mistake for private.
        let json =
            """{"name":"cli","path_with_namespace":"o/cli","default_branch":"main"}"""

        let p = expectOk (GitLabParse.parseRepoView json)
        Assert.That(p.Visibility.IsNone, Is.True)

    [<Test>]
    member _.ReleaseReadsUrlFromNestedLinksSelf() =
        let json =
            """{"tag_name":"v1.0","name":"Release 1.0","released_at":"2026-01-02T03:04:05.000Z","description":"the notes","_links":{"self":"https://gl/-/releases/v1.0"}}"""

        let rel = expectOk (GitLabParse.parseRelease json)
        Assert.That(rel.TagName, Is.EqualTo "v1.0")
        Assert.That(rel.Url, Is.EqualTo "https://gl/-/releases/v1.0", "url flattened from _links.self")
        Assert.That(rel.PublishedAt, Is.EqualTo "2026-01-02T03:04:05.000Z")
        Assert.That(rel.Description, Is.EqualTo "the notes")

    [<Test>]
    member _.ReleaseToleratesMissingLinksAndDate() =
        let json = """{"tag_name":"v2.0"}"""
        let rel = expectOk (GitLabParse.parseRelease json)
        Assert.That(rel.Name, Is.EqualTo "")
        Assert.That(rel.Url, Is.EqualTo "", "no _links → empty url")
        Assert.That(rel.PublishedAt, Is.EqualTo "")

    [<Test>]
    member _.CiStatusBucketsPipelineStates() =
        Assert.That(CiStatus.OfGitLab "success" = CiStatus.Passing)
        Assert.That(CiStatus.OfGitLab "failed" = CiStatus.Failing)
        Assert.That(CiStatus.OfGitLab "canceled" = CiStatus.Failing)
        Assert.That(CiStatus.OfGitLab "cancelled" = CiStatus.Failing)
        Assert.That(CiStatus.OfGitLab "running" = CiStatus.Pending)
        Assert.That(CiStatus.OfGitLab "manual" = CiStatus.Pending, "a blocked pipeline is Pending, not done")
        Assert.That(CiStatus.OfGitLab "skipped" = CiStatus.None)
        Assert.That(CiStatus.OfGitLab "" = CiStatus.None)
        // An unknown future state reads as Pending, not a crash.
        Assert.That(CiStatus.OfGitLab "brand_new" = CiStatus.Pending)

    [<Test>]
    member _.ParseCiStatusPrefersHeadPipelineThenFallsBack() =
        // head_pipeline wins even over a differing pipeline.
        Assert.That(
            expectOk (
                GitLabParse.parseCiStatus
                    """{"iid":1,"head_pipeline":{"status":"success"},"pipeline":{"status":"failed"}}"""
            ) =
                CiStatus.Passing
        )
        // Falls back to the deprecated `pipeline` when there's no head_pipeline.
        Assert.That(
            expectOk (GitLabParse.parseCiStatus """{"iid":1,"pipeline":{"status":"failed"}}""") = CiStatus.Failing
        )
        // No pipeline at all → None.
        Assert.That(expectOk (GitLabParse.parseCiStatus """{"iid":1}""") = CiStatus.None)

    [<Test>]
    member _.ParseCiStatusPresentHeadPipelineWinsEvenWhenEmpty() =
        // A present head_pipeline with an empty status must NOT fall through to
        // `pipeline` — the `.or()` semantics are on the objects, not the strings.
        Assert.That(
            expectOk (
                GitLabParse.parseCiStatus """{"iid":1,"head_pipeline":{"status":""},"pipeline":{"status":"success"}}"""
            ) =
                CiStatus.None
        )
        // A JSON-null head_pipeline (not an object) DOES fall back to `pipeline`.
        Assert.That(
            expectOk (GitLabParse.parseCiStatus """{"iid":1,"head_pipeline":null,"pipeline":{"status":"failed"}}""") =
                CiStatus.Failing
        )

    [<Test>]
    member _.MalformedJsonIsError() =
        match GitLabParse.parseMrList "not json" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "malformed JSON must be an Error"

// ---------------------------------------------------------------------------
// Client: hermetic argv-building + parsing via ScriptedRunner
// ---------------------------------------------------------------------------

[<TestFixture>]
type ClientTests() =

    [<Test>]
    member _.RepoViewBuildsOutputJson() : Task =
        task {
            let json = """{"name":"r","path_with_namespace":"o/r","default_branch":"main"}"""
            let glab = scripted [ "repo"; "view"; "--output"; "json" ] (Reply.Ok json)

            match! glab.RepoView "." with
            | Ok p -> Assert.That(p.PathWithNamespace, Is.EqualTo "o/r")
            | Error e -> Assert.Fail $"repo view failed: {e}"
        }

    [<Test>]
    member _.MrListPinsPerPage100() : Task =
        task {
            let json = """[{"iid":1,"title":"t","state":"opened"}]"""

            let glab =
                scripted [ "mr"; "list"; "--per-page"; "100"; "--output"; "json" ] (Reply.Ok json)

            match! glab.MrList "." with
            | Ok [ mr ] -> Assert.That(mr.Iid, Is.EqualTo 1UL)
            | Ok xs -> Assert.Fail $"expected one MR, got {xs.Length}"
            | Error e -> Assert.Fail $"mr list failed: {e}"
        }

    [<Test>]
    member _.MrViewBuildsNumberedQuery() : Task =
        task {
            let json = """{"iid":12,"title":"t","state":"opened"}"""
            let glab = scripted [ "mr"; "view"; "12"; "--output"; "json" ] (Reply.Ok json)

            match! glab.MrView(".", 12UL) with
            | Ok mr -> Assert.That(mr.Iid, Is.EqualTo 12UL)
            | Error e -> Assert.Fail $"mr view failed: {e}"
        }

    [<Test>]
    member _.MrCreateOmitsSourceAndTargetWhenNone() : Task =
        task {
            let glab, args = capturing (Reply.Ok "https://gl/mr/1\n")

            match! glab.MrCreate(".", MrCreate.Create("T", "B")) with
            | Ok url ->
                Assert.That(url, Is.EqualTo "https://gl/mr/1")
                assertArgs [ "mr"; "create"; "--title"; "T"; "--description"; "B"; "--yes" ] args
            | Error e -> Assert.Fail $"mr create failed: {e}"
        }

    [<Test>]
    member _.MrCreateEmitsSourceThenTarget() : Task =
        task {
            let glab, args = capturing (Reply.Ok "u\n")

            match! glab.MrCreate(".", MrCreate.Create("T", "B").WithSource("feat").WithTarget("main")) with
            | Ok _ ->
                assertArgs
                    [ "mr"
                      "create"
                      "--title"
                      "T"
                      "--description"
                      "B"
                      "--yes"
                      "--source-branch"
                      "feat"
                      "--target-branch"
                      "main" ]
                    args
            | Error e -> Assert.Fail $"mr create failed: {e}"
        }

    [<Test>]
    member _.MrMergeImmediateWithoutStrategyFlag() : Task =
        task {
            // The default Merge strategy adds NO extra flag, but keeps --auto-merge=false.
            let glab, args = capturing (Reply.Ok "")

            match! glab.MrMerge(".", 7UL, MergeStrategy.Merge) with
            | Ok() -> assertArgs [ "mr"; "merge"; "7"; "--yes"; "--auto-merge=false" ] args
            | Error e -> Assert.Fail $"mr merge failed: {e}"
        }

    [<Test>]
    member _.MrMergeSquashAddsFlag() : Task =
        task {
            let glab, args = capturing (Reply.Ok "")

            match! glab.MrMerge(".", 7UL, MergeStrategy.Squash) with
            | Ok() -> assertArgs [ "mr"; "merge"; "7"; "--yes"; "--auto-merge=false"; "--squash" ] args
            | Error e -> Assert.Fail $"mr merge squash failed: {e}"
        }

    [<Test>]
    member _.MrMarkReadyAndCloseBuildArgs() : Task =
        task {
            let ready = scripted [ "mr"; "update"; "3"; "--ready" ] (Reply.Ok "")

            match! ready.MrMarkReady(".", 3UL) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"mr ready failed: {e}"

            let close, args = capturing (Reply.Ok "")

            match! close.MrClose(".", 4UL) with
            | Ok() -> assertArgs [ "mr"; "close"; "4" ] args
            | Error e -> Assert.Fail $"mr close failed: {e}"
        }

    [<Test>]
    member _.MrCommentBuildsNoteArgs() : Task =
        task {
            let glab =
                scripted [ "mr"; "note"; "7"; "-m"; "hello" ] (Reply.Ok "https://gl/note/1\n")

            match! glab.MrComment(".", 7UL, "hello") with
            | Ok out -> Assert.That(out, Is.EqualTo "https://gl/note/1")
            | Error e -> Assert.Fail $"mr comment failed: {e}"
        }

    [<Test>]
    member _.MrEditRejectsBothNoneAndBuildsYesLast() : Task =
        task {
            let refuse = permissive ()

            match! refuse.MrEdit(".", 7UL, MrEdit.Create()) with
            | Error _ -> ()
            | Ok() -> Assert.Fail "an edit with nothing to change must be refused before spawning"

            let glab, args = capturing (Reply.Ok "")

            match! glab.MrEdit(".", 7UL, MrEdit.Create().WithTitle("New")) with
            | Ok() -> assertArgs [ "mr"; "update"; "7"; "--title"; "New"; "--yes" ] args
            | Error e -> Assert.Fail $"mr edit failed: {e}"
        }

    [<Test>]
    member _.MrChecksParsesPipelineStatus() : Task =
        task {
            let json = """{"iid":7,"head_pipeline":{"status":"failed"}}"""
            let glab = scripted [ "mr"; "view"; "7"; "--output"; "json" ] (Reply.Ok json)

            match! glab.MrChecks(".", 7UL) with
            | Ok status -> Assert.That((status = CiStatus.Failing))
            | Error e -> Assert.Fail $"mr checks failed: {e}"
        }

    [<Test>]
    member _.IssueListViewAndCreate() : Task =
        task {
            let list =
                scripted
                    [ "issue"; "list"; "--per-page"; "100"; "--output"; "json" ]
                    (Reply.Ok """[{"iid":1,"title":"t","state":"opened"}]""")

            match! list.IssueList "." with
            | Ok [ issue ] -> Assert.That(issue.Number, Is.EqualTo 1UL)
            | Ok xs -> Assert.Fail $"expected one issue, got {xs.Length}"
            | Error e -> Assert.Fail $"issue list failed: {e}"

            let view =
                scripted
                    [ "issue"; "view"; "9"; "--output"; "json" ]
                    (Reply.Ok """{"iid":9,"title":"t","state":"closed","description":"b"}""")

            match! view.IssueView(".", 9UL) with
            | Ok issue -> Assert.That(issue.Body, Is.EqualTo "b")
            | Error e -> Assert.Fail $"issue view failed: {e}"

            let create, args = capturing (Reply.Ok "https://gl/i/2\n")

            match! create.IssueCreate(".", "Title", "Body") with
            | Ok url ->
                Assert.That(url, Is.EqualTo "https://gl/i/2")
                assertArgs [ "issue"; "create"; "--title"; "Title"; "--description"; "Body"; "--yes" ] args
            | Error e -> Assert.Fail $"issue create failed: {e}"
        }

    [<Test>]
    member _.ReleaseListAndView() : Task =
        task {
            let list =
                scripted
                    [ "release"; "list"; "--per-page"; "100"; "--output"; "json" ]
                    (Reply.Ok """[{"tag_name":"v1","name":"One"}]""")

            match! list.ReleaseList "." with
            | Ok [ rel ] -> Assert.That(rel.TagName, Is.EqualTo "v1")
            | Ok xs -> Assert.Fail $"expected one release, got {xs.Length}"
            | Error e -> Assert.Fail $"release list failed: {e}"

            let view =
                scripted
                    [ "release"; "view"; "v1"; "--output"; "json" ]
                    (Reply.Ok """{"tag_name":"v1","name":"One","description":"notes"}""")

            match! view.ReleaseView(".", "v1") with
            | Ok rel -> Assert.That(rel.Description, Is.EqualTo "notes")
            | Error e -> Assert.Fail $"release view failed: {e}"
        }

// ---------------------------------------------------------------------------
// Auth, the injection guard, and the token-never-in-argv guarantee
// ---------------------------------------------------------------------------

[<TestFixture>]
type SemanticsTests() =

    [<Test>]
    member _.AuthStatusReadsExitCode() : Task =
        task {
            let yes = scripted [ "auth"; "status" ] (Reply.Exit 0)

            match! yes.AuthStatus() with
            | Ok v -> Assert.That(v, Is.True)
            | Error e -> Assert.Fail $"auth status (yes) failed: {e}"

            let no = scripted [ "auth"; "status" ] (Reply.Exit 1)

            match! no.AuthStatus() with
            | Ok v -> Assert.That(v, Is.False)
            | Error e -> Assert.Fail $"auth status (no) failed: {e}"
        }

    [<Test>]
    member _.FlagLikePositionalsAreRejectedBeforeSpawning() : Task =
        task {
            let glab = permissive ()

            let isErr (t: Task<Result<'T, ProcessError>>) =
                task {
                    let! r = t
                    return Result.isError r
                }

            let! a = isErr (glab.Api(".", "-X"))
            let! b = isErr (glab.Api(".", ""))
            let! c = isErr (glab.ReleaseView(".", "--cleanup-tag"))
            let! d = isErr (glab.ReleaseView(".", ""))

            for flag, name in
                [ a, "api dash"
                  b, "api empty"
                  c, "release view dash"
                  d, "release view empty" ] do
                Assert.That(flag, Is.True, $"{name} must be refused")

            let ok = scripted [ "api"; "projects/1" ] (Reply.Ok "{}\n")

            match! ok.Api(".", "projects/1") with
            | Ok body -> Assert.That(body, Is.EqualTo "{}")
            | Error e -> Assert.Fail $"a valid endpoint must pass: {e}"
        }

    [<Test>]
    member _.DashSentinelBodyIsRejectedBeforeSpawning() : Task =
        task {
            // glab treats a body/description of exactly "-" as its own
            // stdin/$EDITOR sentinel; a headless call must refuse it before
            // spawning rather than hang.
            let glab = permissive ()

            let isErr (t: Task<Result<'T, ProcessError>>) =
                task {
                    let! r = t
                    return Result.isError r
                }

            let! a = isErr (glab.MrCreate(".", MrCreate.Create("T", "-")))
            let! b = isErr (glab.MrEdit(".", 7UL, MrEdit.Create().WithBody("-")))
            let! c = isErr (glab.IssueCreate(".", "Title", "-"))
            let! d = isErr (glab.MrComment(".", 7UL, "-"))

            for flag, name in [ a, "mr create"; b, "mr edit"; c, "issue create"; d, "mr comment" ] do
                Assert.That(flag, Is.True, $"{name} with body \"-\" must be refused before spawning")

            // MrEdit with a title but no body must NOT be rejected by the dash check.
            let glab2, args = capturing (Reply.Ok "")

            match! glab2.MrEdit(".", 7UL, MrEdit.Create().WithTitle("New")) with
            | Ok() -> assertArgs [ "mr"; "update"; "7"; "--title"; "New"; "--yes" ] args
            | Error e -> Assert.Fail $"mr edit with title-only must not be affected by the dash check: {e}"
        }

    [<Test>]
    member _.DashContainingOrEmptyBodyPassesThroughUnchanged() : Task =
        task {
            // A value that merely contains a dash, or is empty, is not the glab
            // sentinel and must pass through byte-for-byte, unaffected argv.
            let create, createArgs = capturing (Reply.Ok "u\n")

            match! create.MrCreate(".", MrCreate.Create("T", "-x")) with
            | Ok _ -> assertArgs [ "mr"; "create"; "--title"; "T"; "--description"; "-x"; "--yes" ] createArgs
            | Error e -> Assert.Fail $"mr create with \"-x\" body must pass through: {e}"

            let edit, editArgs = capturing (Reply.Ok "")

            match! edit.MrEdit(".", 7UL, MrEdit.Create().WithBody("a-b")) with
            | Ok() -> assertArgs [ "mr"; "update"; "7"; "--description"; "a-b"; "--yes" ] editArgs
            | Error e -> Assert.Fail $"mr edit with \"a-b\" body must pass through: {e}"

            let issue, issueArgs = capturing (Reply.Ok "u\n")

            match! issue.IssueCreate(".", "Title", "") with
            | Ok _ -> assertArgs [ "issue"; "create"; "--title"; "Title"; "--description"; ""; "--yes" ] issueArgs
            | Error e -> Assert.Fail $"issue create with an empty body must pass through: {e}"

            let comment, commentArgs = capturing (Reply.Ok "u\n")

            match! comment.MrComment(".", 7UL, "a-b") with
            | Ok _ -> assertArgs [ "mr"; "note"; "7"; "-m"; "a-b" ] commentArgs
            | Error e -> Assert.Fail $"mr comment with \"a-b\" body must pass through: {e}"
        }

    [<Test>]
    member _.TokenIsNeverPlacedInArgv() : Task =
        task {
            let secret = "glpat-supersecrettoken"
            let baseGl, args = capturing (Reply.Ok "[]")
            let glab = baseGl.WithToken secret

            match! glab.MrList "." with
            | Ok _ ->
                Assert.That(args |> Seq.exists (fun a -> a.Contains secret), Is.False, "secret must never be in argv")
            | Error e -> Assert.Fail $"mr list (with token) failed: {e}"
        }

// ---------------------------------------------------------------------------
// CLI version parsing + Capabilities floor
// ---------------------------------------------------------------------------

[<TestFixture>]
type VersionTests() =

    [<Test>]
    member _.ParsesLeadingSemverTokenFromBanner() =
        // A real `glab --version` banner → the leading semver token.
        match GitLabParse.parseVersion "glab 1.36.0 (2024-01-15)" with
        | Some v ->
            Assert.That(v.Major, Is.EqualTo 1UL)
            Assert.That(v.Minor, Is.EqualTo 36UL)
            Assert.That(v.Patch, Is.EqualTo 0UL)
        | None -> Assert.Fail "a standard glab version banner must parse"

    [<Test>]
    member _.UnrecognisedVersionDegradesToNone() =
        // No `N.N[.N]` token → explicit None ("unknown"), never a throw.
        Assert.That(GitLabParse.parseVersion "glab (dev build, no version)", Is.EqualTo None)
        Assert.That(GitLabParse.parseVersion "", Is.EqualTo None)

    [<Test>]
    member _.CapabilitiesReportsSupportedVersion() : Task =
        task {
            let glab = scripted [ "--version" ] (Reply.Ok "glab 1.36.0\n")

            match! glab.Capabilities() with
            | Ok caps ->
                Assert.That(caps.Version.ToString(), Is.EqualTo "1.36.0")
                Assert.That(caps.IsSupported, Is.True, "glab 1.36 meets the 1.0 floor")

                match caps.EnsureSupported() with
                | Ok() -> ()
                | Error e -> Assert.Fail $"EnsureSupported must pass at/above the floor: {e}"
            | Error e -> Assert.Fail $"capabilities failed: {e}"
        }

    [<Test>]
    member _.CapabilitiesFlagsBelowFloor() : Task =
        task {
            // A pre-1.0 glab is below the floor → not supported, EnsureSupported errors.
            let glab = scripted [ "--version" ] (Reply.Ok "glab 0.9.0\n")

            match! glab.Capabilities() with
            | Ok caps ->
                Assert.That(caps.IsSupported, Is.False, "glab 0.9 is below the 1.0 floor")

                match caps.EnsureSupported() with
                | Error _ -> ()
                | Ok() -> Assert.Fail "EnsureSupported must error below the floor"
            | Error e -> Assert.Fail $"capabilities failed: {e}"
        }

    [<Test>]
    member _.CapabilitiesErrorsOnUnrecognisedBanner() : Task =
        task {
            // An unrecognisable banner is a predictable Parse error, not a throw.
            let glab = scripted [ "--version" ] (Reply.Ok "glab (dev build)\n")

            match! glab.Capabilities() with
            | Error _ -> ()
            | Ok _ -> Assert.Fail "an unrecognisable version banner must be an Error"
        }
