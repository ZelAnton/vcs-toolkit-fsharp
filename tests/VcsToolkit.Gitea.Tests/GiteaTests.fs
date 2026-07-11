module VcsToolkit.Gitea.Tests

open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.Gitea

let private scripted (tokens: string list) (reply: Reply) =
    Gitea.WithRunner(ScriptedRunner().On(tokens, reply))

// A runner that answers any command with Ok "" — for verifying that a guard refuses
// BEFORE anything spawns (a refusal returns Error; a leak returns Ok).
let private permissive () =
    Gitea.WithRunner(ScriptedRunner().Fallback(Reply.Ok ""))

// A runner that records the exact argv (order + presence) of the command it answers,
// so a test can assert flag ABSENCE and exact values — which subset `.On` cannot.
let private capturing (reply: Reply) : Gitea * ResizeArray<string> =
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

    Gitea.WithRunner runner, args

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
// Pure parsers over `tea … --output json` (tea's all-strings print-table, plus the
// typed issue-detail object)
// ---------------------------------------------------------------------------

[<TestFixture>]
type ParseTests() =

    [<Test>]
    member _.PrListTableRowParsesStringIndex() =
        let json =
            """[{"index":"7","title":"Add X","state":"open","head":"feat/x","base":"main","url":"https://gitea/pr/7"}]"""

        match expectOk (GiteaParse.parsePrList json) with
        | [ pr ] ->
            Assert.That(pr.Number, Is.EqualTo 7UL)
            Assert.That(pr.State, Is.EqualTo "open")
            Assert.That(pr.Merged, Is.False)
            Assert.That(pr.HeadBranch, Is.EqualTo "feat/x")
            Assert.That(pr.BaseBranch, Is.EqualTo "main")
            Assert.That(pr.Url, Is.EqualTo "https://gitea/pr/7")
        | other -> Assert.Fail $"expected one PR, got {other.Length}"

    [<Test>]
    member _.PrMergedStateDerivesTheFlag() =
        let json =
            """[{"index":"9","title":"done","state":"merged","head":"f","base":"main","url":"u"}]"""

        match expectOk (GiteaParse.parsePrList json) with
        | [ pr ] ->
            Assert.That(pr.Number, Is.EqualTo 9UL)
            Assert.That(pr.Merged, Is.True)
            Assert.That(pr.State, Is.EqualTo "merged")
        | other -> Assert.Fail $"expected one PR, got {other.Length}"

    [<Test>]
    member _.PrListPreservesElementOrder() =
        // parseArrayResult must keep input order (a `List.rev` regression would flip it).
        let json =
            """[{"index":"6","title":"first","state":"open","head":"a","base":"main","url":"u6"},{"index":"7","title":"second","state":"open","head":"b","base":"main","url":"u7"}]"""

        match expectOk (GiteaParse.parsePrList json) with
        | [ a; b ] ->
            Assert.That(a.Number, Is.EqualTo 6UL)
            Assert.That(a.Title, Is.EqualTo "first")
            Assert.That(b.Number, Is.EqualTo 7UL)
            Assert.That(b.Title, Is.EqualTo "second")
        | other -> Assert.Fail $"expected two PRs in order, got {other.Length}"

    [<Test>]
    member _.PrNonNumericIndexIsError() =
        // A non-numeric index is a real parse failure, not a silent 0 that PrView could "find".
        match GiteaParse.parsePrList """[{"index":"x","title":"t","state":"open"}]""" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "a non-numeric index must be an Error"

    [<Test>]
    member _.PrMissingIndexIsError() =
        match GiteaParse.parsePrList """[{"title":"t","state":"open"}]""" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "a missing index must be an Error"

    [<Test>]
    member _.IssueListTableRowAndTrimmedColumns() =
        let full =
            """[{"index":"12","title":"Bug","state":"open","body":"broken","url":"https://gitea/issues/12"}]"""

        match expectOk (GiteaParse.parseIssueList full) with
        | [ issue ] ->
            Assert.That(issue.Number, Is.EqualTo 12UL)
            Assert.That(issue.Body, Is.EqualTo "broken")
            Assert.That(issue.Url, Is.EqualTo "https://gitea/issues/12")
        | other -> Assert.Fail $"expected one issue, got {other.Length}"

        // A column trim (body/url absent) still parses via the defaults.
        match expectOk (GiteaParse.parseIssueList """[{"index":"4","title":"wip","state":"open"}]""") with
        | [ issue ] ->
            Assert.That(issue.Number, Is.EqualTo 4UL)
            Assert.That(issue.Body, Is.EqualTo "")
        | other -> Assert.Fail $"expected one issue, got {other.Length}"

    [<Test>]
    member _.SingleIssueDetailIsTypedObject() =
        // The detail view is a typed object: `index` is a real JSON number, not a string.
        let json =
            """{"index":7,"title":"One","state":"closed","body":"b","url":"https://gitea/issues/7"}"""

        let issue = expectOk (GiteaParse.parseIssue json)
        Assert.That(issue.Number, Is.EqualTo 7UL)
        Assert.That(issue.Title, Is.EqualTo "One")
        Assert.That(issue.State, Is.EqualTo "closed")
        Assert.That(issue.Url, Is.EqualTo "https://gitea/issues/7")

    [<Test>]
    member _.IssueDetailToleratesNullBodyAndUrl() =
        // Gitea can send a present null body/url; tolerate it (null → empty).
        let json = """{"index":8,"title":"Empty","state":"open","body":null,"url":null}"""
        let issue = expectOk (GiteaParse.parseIssue json)
        Assert.That(issue.Number, Is.EqualTo 8UL)
        Assert.That(issue.Body, Is.EqualTo "")
        Assert.That(issue.Url, Is.EqualTo "")

    [<Test>]
    member _.ReleaseListParsesSnakeCasedHeaderKeys() =
        // tea's toSnakeCase inserts a stray `_`: the keys are `tag-_name`/`published _at`.
        let json =
            """[{"tag-_name":"0.1","title":"First","status":"released","published _at":"2023-07-26T13:02:36Z"}]"""

        match expectOk (GiteaParse.parseReleaseList json) with
        | [ rel ] ->
            Assert.That(rel.Tag, Is.EqualTo "0.1")
            Assert.That(rel.Title, Is.EqualTo "First")
            Assert.That(rel.PublishedAt, Is.EqualTo "2023-07-26T13:02:36Z")
            Assert.That(rel.Draft, Is.False)
            Assert.That(rel.Prerelease, Is.False)
            Assert.That(rel.Url, Is.EqualTo "", "tea exposes no release-page URL")
        | other -> Assert.Fail $"expected one release, got {other.Length}"

    [<Test>]
    member _.ReleaseStatusDrivesDraftAndPrereleaseFlags() =
        match expectOk (GiteaParse.parseReleaseList """[{"tag-_name":"v2","title":"Two","status":"draft"}]""") with
        | [ rel ] ->
            Assert.That(rel.Draft, Is.True)
            Assert.That(rel.Prerelease, Is.False)
        | _ -> Assert.Fail "expected one release"

        match expectOk (GiteaParse.parseReleaseList """[{"tag-_name":"v3","title":"RC","status":"prerelease"}]""") with
        | [ rel ] ->
            Assert.That(rel.Prerelease, Is.True)
            Assert.That(rel.Draft, Is.False)
        | _ -> Assert.Fail "expected one release"

    [<Test>]
    member _.ReleaseAcceptsPlainerAliasKeys() =
        // A future tea that fixes the snake-case quirk (plain `tag_name`) still parses.
        match expectOk (GiteaParse.parseReleaseList """[{"tag_name":"v4","Title":"Four","Status":"released"}]""") with
        | [ rel ] ->
            Assert.That(rel.Tag, Is.EqualTo "v4")
            Assert.That(rel.Title, Is.EqualTo "Four")
        | _ -> Assert.Fail "expected one release"

    [<Test>]
    member _.ReleaseSkipsNullPrimaryKeyForAlias() =
        // strFirst must skip a present-null primary key and fall through to the next
        // candidate (a null `tag-_name` with a real `tag_name` still yields the tag).
        match expectOk (GiteaParse.parseReleaseList """[{"tag-_name":null,"tag_name":"v9","status":"released"}]""") with
        | [ rel ] -> Assert.That(rel.Tag, Is.EqualTo "v9")
        | _ -> Assert.Fail "expected one release"

    [<Test>]
    member _.ReleaseMissingTagIsError() =
        match GiteaParse.parseReleaseList """[{"title":"no tag"}]""" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "a release row without a tag must be an Error"

    [<Test>]
    member _.HasLoginsCountsArray() =
        Assert.That(expectOk (GiteaParse.parseHasLogins """[{"name":"gitea"}]"""), Is.True)
        Assert.That(expectOk (GiteaParse.parseHasLogins "[]"), Is.False)

        match GiteaParse.parseHasLogins "{}" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "a non-array logins document must be an Error"

    [<Test>]
    member _.MalformedJsonIsError() =
        match GiteaParse.parsePrList "not json" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "malformed JSON must be an Error"

// ---------------------------------------------------------------------------
// Client: hermetic argv-building + parsing via ScriptedRunner
// ---------------------------------------------------------------------------

[<TestFixture>]
type ClientTests() =

    [<Test>]
    member _.PrListRequestsFieldsAndLimit() : Task =
        task {
            let json =
                """[{"index":"1","title":"t","state":"open","head":"h","base":"main","url":"u"}]"""

            let tea =
                scripted
                    [ "pr"
                      "list"
                      "--limit"
                      "100"
                      "--fields"
                      "index,title,state,head,base,url"
                      "--output"
                      "json" ]
                    (Reply.Ok json)

            match! tea.PrList "." with
            | Ok [ pr ] -> Assert.That(pr.Number, Is.EqualTo 1UL)
            | Ok xs -> Assert.Fail $"expected one PR, got {xs.Length}"
            | Error e -> Assert.Fail $"pr list failed: {e}"
        }

    [<Test>]
    member _.PrViewFindsOnFirstPage() : Task =
        task {
            let json =
                """[{"index":"6","title":"other","state":"open","head":"a","base":"main","url":"u6"},{"index":"7","title":"mine","state":"open","head":"b","base":"main","url":"u7"}]"""

            let tea =
                scripted
                    [ "pr"
                      "list"
                      "--state"
                      "all"
                      "--page"
                      "1"
                      "--fields"
                      "index,title,state,head,base,url" ]
                    (Reply.Ok json)

            match! tea.PrView(".", 7UL) with
            | Ok pr ->
                Assert.That(pr.Number, Is.EqualTo 7UL)
                Assert.That(pr.Title, Is.EqualTo "mine")
            | Error e -> Assert.Fail $"pr view failed: {e}"
        }

    [<Test>]
    member _.PrViewBuildsExactPagedArgv() : Task =
        task {
            // Lock the synthesized-view page-1 argv exactly (--state all, --limit 50, --page 1,
            // --fields, --output json) — subset matching can't prove the paging flags.
            let json =
                """[{"index":"7","title":"mine","state":"open","head":"b","base":"main","url":"u"}]"""

            let tea, args = capturing (Reply.Ok json)

            match! tea.PrView(".", 7UL) with
            | Ok pr ->
                Assert.That(pr.Number, Is.EqualTo 7UL)

                assertArgs
                    [ "pr"
                      "list"
                      "--state"
                      "all"
                      "--limit"
                      "50"
                      "--page"
                      "1"
                      "--fields"
                      "index,title,state,head,base,url"
                      "--output"
                      "json" ]
                    args
            | Error e -> Assert.Fail $"pr view failed: {e}"
        }

    [<Test>]
    member _.PrViewPagesToFindLaterPr() : Task =
        task {
            // A PR past the first page (the Gitea server caps a page at ~50) must still be
            // found — `PrView` pages through with `--page N`, not a single large `--limit`.
            let page1 =
                """[{"index":"1","title":"a","state":"open","head":"x","base":"main","url":"u1"}]"""

            let page2 =
                """[{"index":"55","title":"target","state":"open","head":"y","base":"main","url":"u55"}]"""

            let runner =
                ScriptedRunner()
                    .On([ "pr"; "list"; "--page"; "1" ], Reply.Ok page1)
                    .On([ "pr"; "list"; "--page"; "2" ], Reply.Ok page2)

            let tea = Gitea.WithRunner runner

            match! tea.PrView(".", 55UL) with
            | Ok pr -> Assert.That(pr.Title, Is.EqualTo "target", "found #55 on page 2")
            | Error e -> Assert.Fail $"pr view failed: {e}"
        }

    [<Test>]
    member _.PrViewMissingNumberIsError() : Task =
        task {
            // An empty page (past the last PR) → a genuine absence error, not a hang or crash.
            let tea = scripted [ "pr"; "list"; "--state"; "all" ] (Reply.Ok "[]")

            let! r = tea.PrView(".", 99UL)
            Assert.That(Result.isError r, Is.True, "a PR number not in the listing is an error")
        }

    [<Test>]
    member _.ListParsersTreatEmptyOutputAsEmptyList() : Task =
        task {
            // Some `tea` builds print NOTHING (not `[]`) for an empty result — an empty repo is
            // a normal state, not a parse error.
            let tea = scripted [ "pr"; "list" ] (Reply.Ok "")

            match! tea.PrList "." with
            | Ok prs -> Assert.That(prs, Is.Empty, "empty output → empty list")
            | Error e -> Assert.Fail $"empty output should parse as an empty list: {e}"
        }

    [<Test>]
    member _.PrCreateOmitsHeadAndBaseWhenNone() : Task =
        task {
            let tea, args = capturing (Reply.Ok "Created pull request\n")

            match! tea.PrCreate(".", PrCreate.Create("T", "B")) with
            | Ok _ -> assertArgs [ "pr"; "create"; "--title"; "T"; "--description"; "B" ] args
            | Error e -> Assert.Fail $"pr create failed: {e}"
        }

    [<Test>]
    member _.PrCreateEmitsHeadThenBase() : Task =
        task {
            let tea, args = capturing (Reply.Ok "ok\n")

            match! tea.PrCreate(".", PrCreate.Create("T", "B").WithHead("feat").WithBase("main")) with
            | Ok _ ->
                assertArgs
                    [ "pr"
                      "create"
                      "--title"
                      "T"
                      "--description"
                      "B"
                      "--head"
                      "feat"
                      "--base"
                      "main" ]
                    args
            | Error e -> Assert.Fail $"pr create failed: {e}"
        }

    [<Test>]
    member _.PrMergeBuildsStyle() : Task =
        task {
            let tea, args = capturing (Reply.Ok "")

            match! tea.PrMerge(".", 7UL, MergeStrategy.Squash) with
            | Ok() -> assertArgs [ "pr"; "merge"; "7"; "--style"; "squash" ] args
            | Error e -> Assert.Fail $"pr merge failed: {e}"
        }

    [<Test>]
    member _.PrCloseBuildsArgs() : Task =
        task {
            let tea, args = capturing (Reply.Ok "")

            match! tea.PrClose(".", 4UL) with
            | Ok() -> assertArgs [ "pr"; "close"; "4" ] args
            | Error e -> Assert.Fail $"pr close failed: {e}"
        }

    [<Test>]
    member _.PrCheckoutBuildsExactArgv() : Task =
        task {
            // `tea pr checkout <index>` — the number is the sole positional, no extra flags.
            let tea, args = capturing (Reply.Ok "")

            match! tea.PrCheckout(".", 7UL) with
            | Ok() -> assertArgs [ "pr"; "checkout"; "7" ] args
            | Error e -> Assert.Fail $"pr checkout failed: {e}"
        }

    [<Test>]
    member _.PrEditRejectsBothNoneAndBuildsDescription() : Task =
        task {
            let refuse = permissive ()

            match! refuse.PrEdit(".", 7UL, PrEdit.Create()) with
            | Error _ -> ()
            | Ok() -> Assert.Fail "an edit with nothing to change must be refused before spawning"

            let tea, args = capturing (Reply.Ok "")

            match! tea.PrEdit(".", 7UL, PrEdit.Create().WithBody("New body")) with
            | Ok() -> assertArgs [ "pr"; "edit"; "7"; "--description"; "New body" ] args
            | Error e -> Assert.Fail $"pr edit failed: {e}"
        }

    [<Test>]
    member _.IssueViewIsFirstClassDetail() : Task =
        task {
            let json =
                """{"index":3,"title":"Docs","state":"open","body":"write them","url":"u"}"""

            let tea = scripted [ "issues"; "3"; "--output"; "json" ] (Reply.Ok json)

            match! tea.IssueView(".", 3UL) with
            | Ok issue ->
                Assert.That(issue.Number, Is.EqualTo 3UL)
                Assert.That(issue.Body, Is.EqualTo "write them")
            | Error e -> Assert.Fail $"issue view failed: {e}"
        }

    [<Test>]
    member _.IssueListAndCreate() : Task =
        task {
            let list =
                scripted
                    [ "issues"
                      "list"
                      "--limit"
                      "100"
                      "--fields"
                      "index,title,state,body,url"
                      "--output"
                      "json" ]
                    (Reply.Ok """[{"index":"12","title":"Bug","state":"open","body":"b","url":"u"}]""")

            match! list.IssueList "." with
            | Ok [ issue ] -> Assert.That(issue.Number, Is.EqualTo 12UL)
            | Ok xs -> Assert.Fail $"expected one issue, got {xs.Length}"
            | Error e -> Assert.Fail $"issue list failed: {e}"

            let create, args = capturing (Reply.Ok "Created issue #5\n")

            match! create.IssueCreate(".", "Title", "Body") with
            | Ok _ -> assertArgs [ "issues"; "create"; "--title"; "Title"; "--description"; "Body" ] args
            | Error e -> Assert.Fail $"issue create failed: {e}"
        }

    [<Test>]
    member _.ReleaseListRequestsLimit() : Task =
        task {
            let tea =
                scripted
                    [ "releases"; "list"; "--limit"; "100"; "--output"; "json" ]
                    (Reply.Ok """[{"tag-_name":"0.1","title":"First","status":"released"}]""")

            match! tea.ReleaseList "." with
            | Ok [ rel ] -> Assert.That(rel.Tag, Is.EqualTo "0.1")
            | Ok xs -> Assert.Fail $"expected one release, got {xs.Length}"
            | Error e -> Assert.Fail $"release list failed: {e}"
        }

// ---------------------------------------------------------------------------
// auth_status (login-list based) and the injection guard on the bare comment body
// ---------------------------------------------------------------------------

[<TestFixture>]
type SemanticsTests() =

    [<Test>]
    member _.AuthStatusCountsLogins() : Task =
        task {
            let some =
                scripted [ "login"; "list"; "--output"; "json" ] (Reply.Ok """[{"name":"gitea"}]""")

            match! some.AuthStatus() with
            | Ok v -> Assert.That(v, Is.True)
            | Error e -> Assert.Fail $"auth status (some) failed: {e}"

            let empty = scripted [ "login"; "list"; "--output"; "json" ] (Reply.Ok "[]")

            match! empty.AuthStatus() with
            | Ok v -> Assert.That(v, Is.False, "an empty logins array means not logged in")
            | Error e -> Assert.Fail $"auth status (empty) failed: {e}"
        }

    [<Test>]
    member _.AuthStatusEmptyOutputIsFalse() : Task =
        task {
            // Some tea builds print nothing (not `[]`) when none are configured.
            let tea = scripted [ "login"; "list"; "--output"; "json" ] (Reply.Ok "")

            match! tea.AuthStatus() with
            | Ok v -> Assert.That(v, Is.False)
            | Error e -> Assert.Fail $"auth status (empty output) failed: {e}"
        }

    [<Test>]
    member _.AuthStatusNonZeroExitIsFalse() : Task =
        task {
            // A non-zero exit (e.g. no config file yet) reads as "not logged in", not an error.
            let tea =
                scripted [ "login"; "list"; "--output"; "json" ] (Reply.Fail(1, "no config"))

            match! tea.AuthStatus() with
            | Ok v -> Assert.That(v, Is.False)
            | Error e -> Assert.Fail $"auth status (non-zero) failed: {e}"
        }

    [<Test>]
    member _.AuthStatusErrorsOnAbnormalTermination() : Task =
        task {
            // A signal kill has no exit code — it must surface as an error, not "false".
            let tea = scripted [ "login"; "list"; "--output"; "json" ] (Reply.Signalled 9)

            let! r = tea.AuthStatus()
            Assert.That(Result.isError r, Is.True, "an abnormal termination must error, not read false")
        }

    [<Test>]
    member _.CommentBodyIsRejectedIfFlagLike() : Task =
        task {
            let refuse = permissive ()

            let isErr (t: Task<Result<'T, ProcessError>>) =
                task {
                    let! r = t
                    return Result.isError r
                }

            let! a = isErr (refuse.PrComment(".", 7UL, "-evil"))
            let! b = isErr (refuse.PrComment(".", 7UL, ""))
            Assert.That(a, Is.True, "a dash-leading comment body must be refused")
            Assert.That(b, Is.True, "an empty comment body must be refused")

            // …and a legitimate body still passes, as a bare positional after the index.
            let tea, args = capturing (Reply.Ok "commented\n")

            match! tea.PrComment(".", 7UL, "nice work") with
            | Ok _ -> assertArgs [ "comment"; "7"; "nice work" ] args
            | Error e -> Assert.Fail $"pr comment failed: {e}"
        }

// ---------------------------------------------------------------------------
// CLI version parsing + Capabilities floor
// ---------------------------------------------------------------------------

[<TestFixture>]
type VersionTests() =

    [<Test>]
    member _.ParsesLeadingSemverTokenFromBanner() =
        // A real `tea --version` banner → the leading semver token.
        match GiteaParse.parseVersion "tea version 0.9.2\ncommit abc123" with
        | Some v ->
            Assert.That(v.Major, Is.EqualTo 0UL)
            Assert.That(v.Minor, Is.EqualTo 9UL)
            Assert.That(v.Patch, Is.EqualTo 2UL)
        | None -> Assert.Fail "a standard tea version banner must parse"

    [<Test>]
    member _.UnrecognisedVersionDegradesToNone() =
        // No `N.N[.N]` token → explicit None ("unknown"), never a throw.
        Assert.That(GiteaParse.parseVersion "tea (dev build, no version)", Is.EqualTo None)
        Assert.That(GiteaParse.parseVersion "", Is.EqualTo None)

    [<Test>]
    member _.CapabilitiesReportsSupportedVersion() : Task =
        task {
            let tea = scripted [ "--version" ] (Reply.Ok "tea version 0.9.2\n")

            match! tea.Capabilities() with
            | Ok caps ->
                Assert.That(caps.Version.ToString(), Is.EqualTo "0.9.2")
                Assert.That(caps.IsSupported, Is.True, "tea 0.9.2 meets the 0.9 floor")

                match caps.EnsureSupported() with
                | Ok() -> ()
                | Error e -> Assert.Fail $"EnsureSupported must pass at/above the floor: {e}"
            | Error e -> Assert.Fail $"capabilities failed: {e}"
        }

    [<Test>]
    member _.CapabilitiesFlagsBelowFloor() : Task =
        task {
            // A pre-0.9 tea is below the floor → not supported, EnsureSupported errors.
            let tea = scripted [ "--version" ] (Reply.Ok "tea version 0.8.0\n")

            match! tea.Capabilities() with
            | Ok caps ->
                Assert.That(caps.IsSupported, Is.False, "tea 0.8 is below the 0.9 floor")

                match caps.EnsureSupported() with
                | Error _ -> ()
                | Ok() -> Assert.Fail "EnsureSupported must error below the floor"
            | Error e -> Assert.Fail $"capabilities failed: {e}"
        }

    [<Test>]
    member _.CapabilitiesErrorsOnUnrecognisedBanner() : Task =
        task {
            // An unrecognisable banner is a predictable Parse error, not a throw.
            let tea = scripted [ "--version" ] (Reply.Ok "tea (dev build)\n")

            match! tea.Capabilities() with
            | Error _ -> ()
            | Ok _ -> Assert.Fail "an unrecognisable version banner must be an Error"
        }
