module VcsToolkit.GitHub.Tests

open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.CliSupport
open VcsToolkit.GitHub
open VcsToolkit.Diff

let private scripted (tokens: string list) (reply: Reply) =
    GitHub.WithRunner(ScriptedRunner().On(tokens, reply))

// A runner that answers any command with Ok "" — for verifying that a guard
// refuses BEFORE anything spawns (a refusal returns Error; a leak returns Ok).
let private permissive () =
    GitHub.WithRunner(ScriptedRunner().Fallback(Reply.Ok ""))

// A runner that records the exact argv (order + presence) of the command it
// answers, so a test can assert flag ABSENCE and exact values — which the
// subset-matching `.On` cannot. Read `args` after the awaited call.
let private capturing (reply: Reply) : GitHub * ResizeArray<string> =
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

    GitHub.WithRunner runner, args

// Assert the captured argv equals `expected` exactly (order and absence included).
let private assertArgs (expected: string list) (actual: ResizeArray<string>) =
    let got = List.ofSeq actual
    Assert.That(got.Length, Is.EqualTo expected.Length, $"argv length: expected %A{expected}, got %A{got}")
    List.iter2 (fun (e: string) (a: string) -> Assert.That(a, Is.EqualTo e)) expected got

// Unwrap a parser result, failing the test loudly on an unexpected Error.
let private expectOk (r: Result<'T, string>) : 'T =
    match r with
    | Ok v -> v
    | Error e ->
        Assert.Fail $"unexpected parse error: {e}"
        failwith "unreachable"

// ---------------------------------------------------------------------------
// Pure parsers over `gh … --json` output
// ---------------------------------------------------------------------------

[<TestFixture>]
type ParseTests() =

    [<Test>]
    member _.PrListParsesFieldsAndNumberAsUInt64() =
        let json =
            """[{"number":42,"title":"Add feature","state":"OPEN","headRefName":"feat","baseRefName":"main","url":"https://github.com/o/r/pull/42"}]"""

        match expectOk (GitHubParse.parsePrList json) with
        | [ pr ] ->
            Assert.That(pr.Number, Is.EqualTo 42UL)
            Assert.That(pr.Title, Is.EqualTo "Add feature")
            Assert.That(pr.State, Is.EqualTo "OPEN")
            Assert.That(pr.HeadRefName, Is.EqualTo "feat")
            Assert.That(pr.BaseRefName, Is.EqualTo "main")
            Assert.That(pr.Url, Is.EqualTo "https://github.com/o/r/pull/42")
        | other -> Assert.Fail $"expected one PR, got {other.Length}"

    [<Test>]
    member _.PrReadsNullHeadRefNameAsEmpty() =
        // gh sends a present `null` for a deleted head branch — that must read as "".
        let json =
            """{"number":7,"title":"t","state":"MERGED","headRefName":null,"baseRefName":"main","url":"u"}"""

        let pr = expectOk (GitHubParse.parsePr json)
        Assert.That(pr.Number, Is.EqualTo 7UL)
        Assert.That(pr.HeadRefName, Is.EqualTo "", "a null head branch reads as empty, not a crash")

    [<Test>]
    member _.PrFlattensAuthorMilestoneAndReadsTimestamps() =
        // gh nests `author` (`{"login": …}`) and `milestone` (`{"title": …}`); both flatten to
        // the inner field, and `createdAt`/`updatedAt` are RFC 3339 strings.
        let json =
            """{"number":7,"title":"t","state":"OPEN","headRefName":"f","baseRefName":"main","url":"u","author":{"login":"octocat"},"createdAt":"2026-01-02T03:04:05Z","updatedAt":"2026-01-03T04:05:06Z","milestone":{"title":"v1.0"}}"""

        let pr = expectOk (GitHubParse.parsePr json)
        Assert.That(pr.Author, Is.EqualTo "octocat", "author.login flattened")
        Assert.That(pr.CreatedAt, Is.EqualTo "2026-01-02T03:04:05Z")
        Assert.That(pr.UpdatedAt, Is.EqualTo "2026-01-03T04:05:06Z")
        Assert.That(pr.Milestone, Is.EqualTo "v1.0", "milestone.title flattened")

    [<Test>]
    member _.PrReadsNullAuthorAndMilestoneAsEmpty() =
        // A deleted/ghost account → gh sends `author: null`; an unset milestone → `milestone: null`.
        // Both must flatten to "" (the wrapper's "absent" marker), never a crash.
        let json =
            """{"number":8,"title":"t","state":"MERGED","headRefName":"f","baseRefName":"main","url":"u","author":null,"createdAt":"t","updatedAt":"t","milestone":null}"""

        let pr = expectOk (GitHubParse.parsePr json)
        Assert.That(pr.Author, Is.EqualTo "", "null author (deleted account) reads as empty")
        Assert.That(pr.Milestone, Is.EqualTo "", "null milestone (unset) reads as empty")

    [<Test>]
    member _.IssueParsesBodyAndUrl() =
        let json = """{"number":5,"title":"bug","state":"OPEN","body":"broken","url":"u"}"""
        let issue = expectOk (GitHubParse.parseIssue json)
        Assert.That(issue.Number, Is.EqualTo 5UL)
        Assert.That(issue.Body, Is.EqualTo "broken")
        Assert.That(issue.Url, Is.EqualTo "u")

    [<Test>]
    member _.IssueFlattensAuthorMilestoneAndReadsNullAsEmpty() =
        // A populated issue flattens author/milestone; a null author/milestone reads as "".
        let populated =
            """{"number":5,"title":"bug","state":"OPEN","body":"b","url":"u","author":{"login":"alice"},"createdAt":"2026-01-02T03:04:05Z","updatedAt":"2026-01-03T04:05:06Z","milestone":{"title":"backlog"}}"""

        let issue = expectOk (GitHubParse.parseIssue populated)
        Assert.That(issue.Author, Is.EqualTo "alice", "author.login flattened")
        Assert.That(issue.CreatedAt, Is.EqualTo "2026-01-02T03:04:05Z")
        Assert.That(issue.UpdatedAt, Is.EqualTo "2026-01-03T04:05:06Z")
        Assert.That(issue.Milestone, Is.EqualTo "backlog", "milestone.title flattened")

        let nulls =
            """{"number":6,"title":"bug","state":"CLOSED","body":"b","url":"u","author":null,"milestone":null}"""

        let ghostIssue = expectOk (GitHubParse.parseIssue nulls)
        Assert.That(ghostIssue.Author, Is.EqualTo "", "null author reads as empty")
        Assert.That(ghostIssue.Milestone, Is.EqualTo "", "null milestone reads as empty")

    [<Test>]
    member _.RunListParsesDatabaseIdAndEmptyConclusion() =
        // A still-running run reports an empty-string conclusion (not null).
        let json =
            """[{"databaseId":123,"name":"CI","displayTitle":"fix stuff","status":"in_progress","conclusion":"","workflowName":"CI","headBranch":"main","event":"push","url":"u","createdAt":"t"}]"""

        match expectOk (GitHubParse.parseRunList json) with
        | [ run ] ->
            Assert.That(run.DatabaseId, Is.EqualTo 123UL)
            Assert.That(run.Status, Is.EqualTo "in_progress")
            Assert.That(run.Conclusion, Is.EqualTo "")
            Assert.That(run.Event, Is.EqualTo "push")
        | other -> Assert.Fail $"expected one run, got {other.Length}"

    [<Test>]
    member _.ChecksMapEveryBucketAndCatchAll() =
        let json =
            """[{"name":"build","state":"SUCCESS","bucket":"pass","workflow":"CI","link":"l","startedAt":"t1","completedAt":"t2"},
                {"name":"test","state":"FAILURE","bucket":"fail","workflow":"CI","link":"l","startedAt":"t1","completedAt":"t2"},
                {"name":"lint","state":"IN_PROGRESS","bucket":"pending","workflow":"CI","link":"","startedAt":"t1","completedAt":""},
                {"name":"opt","state":"SKIPPED","bucket":"skipping","workflow":"","link":"","startedAt":"","completedAt":""},
                {"name":"stop","state":"CANCELLED","bucket":"cancel","workflow":"","link":"","startedAt":"","completedAt":""},
                {"name":"weird","state":"NEW","bucket":"mystery","workflow":"","link":"","startedAt":"","completedAt":""}]"""

        let checks = expectOk (GitHubParse.parseChecks json)
        Assert.That(checks.Length, Is.EqualTo 6)
        Assert.That(checks.[0].Bucket = CheckBucket.Pass)
        Assert.That(checks.[0].Bucket.IsPassing, Is.True)
        Assert.That(checks.[1].Bucket = CheckBucket.Fail)
        Assert.That(checks.[1].Bucket.IsFailing, Is.True)
        Assert.That(checks.[2].Bucket = CheckBucket.Pending)
        Assert.That(checks.[2].Bucket.IsPending, Is.True)
        Assert.That(checks.[3].Bucket = CheckBucket.Skipping)
        Assert.That(checks.[4].Bucket = CheckBucket.Cancel)
        Assert.That(checks.[4].Bucket.IsFailing, Is.True, "a cancelled check fails the aggregate verdict")
        // An unmodelled bucket string is the forward-compatible catch-all, not a parse failure.
        Assert.That(checks.[5].Bucket = CheckBucket.Unknown)
        Assert.That(checks.[5].Bucket.IsUnknown, Is.True)

    [<Test>]
    member _.ChecksAbsentBucketReadsUnknown() =
        // No `bucket` key at all → Unknown, never a crash.
        let json = """[{"name":"legacy","state":"SUCCESS"}]"""
        let checks = expectOk (GitHubParse.parseChecks json)
        Assert.That(checks.[0].Bucket = CheckBucket.Unknown)
        Assert.That(checks.[0].Workflow, Is.EqualTo "", "an absent string field reads as empty")

    [<Test>]
    member _.ReleaseListParsesBooleans() =
        let json =
            """[{"tagName":"v1.0.0","name":"1.0.0","isLatest":true,"isDraft":false,"isPrerelease":false,"publishedAt":"t"}]"""

        match expectOk (GitHubParse.parseReleaseList json) with
        | [ rel ] ->
            Assert.That(rel.TagName, Is.EqualTo "v1.0.0")
            Assert.That(rel.IsLatest, Is.True)
            Assert.That(rel.IsDraft, Is.False)
            Assert.That(rel.Body, Is.EqualTo "", "release list does not fetch the body")
        | other -> Assert.Fail $"expected one release, got {other.Length}"

    [<Test>]
    member _.ReleaseViewFillsBodyAndDefaultsIsLatestFalse() =
        let json =
            """{"tagName":"v1.0.0","name":"1.0.0","body":"notes","url":"u","publishedAt":"t","isDraft":false,"isPrerelease":true}"""

        let rel = expectOk (GitHubParse.parseRelease json)
        Assert.That(rel.Body, Is.EqualTo "notes")
        Assert.That(rel.IsPrerelease, Is.True)
        Assert.That(rel.IsLatest, Is.False, "release view does not report isLatest → defaults false")

    [<Test>]
    member _.ReleaseViewFlattensAuthorAndListLeavesItEmpty() =
        // `release view` fetches `author` (nested `{"login": …}`); the lean `release list`
        // doesn't, so a list release reads an empty author (like its empty body/url).
        let viewJson =
            """{"tagName":"v1.0.0","name":"1.0.0","body":"n","url":"u","publishedAt":"t","isDraft":false,"isPrerelease":false,"author":{"login":"releaser"}}"""

        let viewed = expectOk (GitHubParse.parseRelease viewJson)
        Assert.That(viewed.Author, Is.EqualTo "releaser", "author.login flattened from release view")

        let listJson =
            """[{"tagName":"v1.0.0","name":"1.0.0","isLatest":true,"isDraft":false,"isPrerelease":false,"publishedAt":"t"}]"""

        match expectOk (GitHubParse.parseReleaseList listJson) with
        | [ rel ] -> Assert.That(rel.Author, Is.EqualTo "", "release list does not fetch the author")
        | other -> Assert.Fail $"expected one release, got {other.Length}"

    [<Test>]
    member _.RepoFlattensNestedObjectsAndDescriptionSome() =
        let json =
            """{"name":"r","owner":{"login":"octocat"},"description":"desc","url":"u","isPrivate":true,"defaultBranchRef":{"name":"main"}}"""

        let repo = expectOk (GitHubParse.parseRepo json)
        Assert.That(repo.Name, Is.EqualTo "r")
        Assert.That(repo.Owner, Is.EqualTo "octocat", "owner.login flattened")
        Assert.That(repo.DefaultBranch, Is.EqualTo "main", "defaultBranchRef.name flattened")
        Assert.That(repo.IsPrivate, Is.True)
        Assert.That(repo.Description, Is.EqualTo(Some "desc"))

    [<Test>]
    member _.RepoNullDescriptionIsNoneAndNullRefIsEmpty() =
        // description: null → None; defaultBranchRef: null (empty repo) → "".
        let json =
            """{"name":"r","owner":{"login":"o"},"description":null,"url":"u","isPrivate":false,"defaultBranchRef":null}"""

        let repo = expectOk (GitHubParse.parseRepo json)
        Assert.That(repo.Description.IsNone, Is.True, "a null description is None, distinct from an empty string")
        Assert.That(repo.DefaultBranch, Is.EqualTo "", "a null defaultBranchRef reads as empty")

    [<Test>]
    member _.FeedbackFlattensReviewAndCommentAuthors() =
        let json =
            """{"reviews":[{"author":{"login":"alice"},"state":"APPROVED","body":"lgtm","submittedAt":"t"}],
                "comments":[{"author":{"login":"bob"},"body":"hi","url":"u","createdAt":"t"}]}"""

        let fb = expectOk (GitHubParse.parseFeedback json)
        Assert.That(fb.Reviews.Length, Is.EqualTo 1)
        Assert.That(fb.Reviews.[0].Author, Is.EqualTo "alice", "review author.login flattened")
        Assert.That(fb.Reviews.[0].State, Is.EqualTo "APPROVED")
        Assert.That(fb.Comments.Length, Is.EqualTo 1)
        Assert.That(fb.Comments.[0].Author, Is.EqualTo "bob", "comment author.login flattened")

    [<Test>]
    member _.FeedbackAbsentArraysAreEmpty() =
        // gh omits the arrays entirely when there are none — that must be [], not a crash.
        let fb = expectOk (GitHubParse.parseFeedback "{}")
        Assert.That(fb.Reviews, Is.Empty)
        Assert.That(fb.Comments, Is.Empty)

    [<Test>]
    member _.MalformedJsonIsErrorNotException() =
        match GitHubParse.parsePrList "{not json" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "malformed JSON must be an Error, not parse as Ok"

    [<Test>]
    member _.ArrayParserRejectsAnObject() =
        // An object where an array is expected is a shape error, surfaced as Error.
        match GitHubParse.parsePrList """{"number":1}""" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "an object is not a valid PR array"

    [<Test>]
    member _.EmptyArrayParsesToEmptyList() =
        match GitHubParse.parseChecks "[]" with
        | Ok [] -> ()
        | Ok xs -> Assert.Fail $"expected empty list, got {xs.Length}"
        | Error e -> Assert.Fail $"empty array should parse: {e}"

    [<Test>]
    member _.IssueListParsesArray() =
        let json =
            """[{"number":3,"title":"Docs","state":"OPEN","body":"","url":""},{"number":4,"title":"Bug","state":"CLOSED","body":"b","url":"u"}]"""

        match expectOk (GitHubParse.parseIssueList json) with
        | [ a; b ] ->
            Assert.That(a.Number, Is.EqualTo 3UL)
            Assert.That(b.Number, Is.EqualTo 4UL)
            Assert.That(b.State, Is.EqualTo "CLOSED")
        | other -> Assert.Fail $"expected two issues, got {other.Length}"

    [<Test>]
    member _.NonObjectRootIsErrorNotException() =
        // Well-formed JSON whose root is the wrong kind must be an Error, never a
        // crash — `JsonElement.TryGetProperty` throws on a non-object, and that must
        // be caught (regression guard for the totality contract).
        match GitHubParse.parsePr "null" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "a null root must be an Error"

        match GitHubParse.parseRepo "[]" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "an array where an object is expected must be an Error"

        match GitHubParse.parseIssue "42" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "a bare number root must be an Error"

    [<Test>]
    member _.ArrayOfNonObjectsIsErrorNotException() =
        // A non-object element can't populate a record — that must be an Error, not
        // an exception escaping the parser (regression guard for the totality contract).
        match GitHubParse.parsePrList "[1,2,3]" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "an array of numbers must be an Error"

        match GitHubParse.parseChecks "[null]" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "an array with a null element must be an Error"

    [<Test>]
    member _.FeedbackWithNonObjectArrayElementIsError() =
        // A non-object inside the nested reviews/comments arrays must surface as an
        // Error, not crash the flatten (backstop for the wrong-kind element path).
        match GitHubParse.parseFeedback """{"reviews":[1],"comments":[]}""" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "a non-object review element must be an Error"

// ---------------------------------------------------------------------------
// Client: hermetic argv-building + parsing via ScriptedRunner
// ---------------------------------------------------------------------------

[<TestFixture>]
type ClientTests() =

    [<Test>]
    member _.VersionRunsBareFlag() : Task =
        task {
            let gh = scripted [ "--version" ] (Reply.Ok "gh version 2.40.0\n")

            match! gh.Version() with
            | Ok v -> Assert.That(v, Is.EqualTo "gh version 2.40.0")
            | Error e -> Assert.Fail $"version failed: {e}"
        }

    [<Test>]
    member _.AuthStatusReadsExitCode() : Task =
        task {
            let yes = scripted [ "auth"; "status" ] (Reply.Exit 0)

            match! yes.AuthStatus() with
            | Ok v -> Assert.That(v, Is.True)
            | Error e -> Assert.Fail $"auth status (yes) failed: {e}"

            let no = scripted [ "auth"; "status" ] (Reply.Exit 1)

            match! no.AuthStatus() with
            | Ok v -> Assert.That(v, Is.False, "a non-zero exit reads as not-authenticated")
            | Error e -> Assert.Fail $"auth status (no) failed: {e}"
        }

    [<Test>]
    member _.RepoViewBuildsJsonQueryAndFlattens() : Task =
        task {
            let json =
                """{"name":"r","owner":{"login":"o"},"description":"d","url":"u","isPrivate":false,"defaultBranchRef":{"name":"main"}}"""

            let gh =
                scripted
                    [ "repo"
                      "view"
                      "--json"
                      "name,owner,description,url,isPrivate,defaultBranchRef" ]
                    (Reply.Ok json)

            match! gh.RepoView "." with
            | Ok repo ->
                Assert.That(repo.Owner, Is.EqualTo "o")
                Assert.That(repo.DefaultBranch, Is.EqualTo "main")
            | Error e -> Assert.Fail $"repo view failed: {e}"
        }

    [<Test>]
    member _.PrListLimitsTo100() : Task =
        task {
            let json =
                """[{"number":1,"title":"t","state":"OPEN","headRefName":"h","baseRefName":"main","url":"u"}]"""

            let gh = scripted [ "pr"; "list"; "--limit"; "100"; "--json" ] (Reply.Ok json)

            match! gh.PrList "." with
            | Ok [ pr ] -> Assert.That(pr.Number, Is.EqualTo 1UL)
            | Ok xs -> Assert.Fail $"expected one PR, got {xs.Length}"
            | Error e -> Assert.Fail $"pr list failed: {e}"
        }

    [<Test>]
    member _.PrListForBranchBuildsHeadBaseStateAll() : Task =
        task {
            let gh =
                scripted [ "pr"; "list"; "--head"; "feat"; "--base"; "main"; "--state"; "all" ] (Reply.Ok "[]")

            match! gh.PrListForBranch(".", "feat", "main") with
            | Ok xs -> Assert.That(xs, Is.Empty)
            | Error e -> Assert.Fail $"pr list for branch failed: {e}"
        }

    [<Test>]
    member _.PrListForBranchTwoArgBuildsHeadStateAllWithoutBase() : Task =
        task {
            let json =
                """[{"number":1,"title":"t","state":"OPEN","headRefName":"feat","baseRefName":"main","url":"u"}]"""

            let gh, args = capturing (Reply.Ok json)

            match! gh.PrListForBranch(".", "feat") with
            | Ok [ pr ] -> Assert.That(pr.Number, Is.EqualTo 1UL)
            | Ok xs -> Assert.Fail $"expected one PR, got {xs.Length}"
            | Error e -> Assert.Fail $"pr list for branch failed: {e}"

            assertArgs
                [ "pr"
                  "list"
                  "--head"
                  "feat"
                  "--state"
                  "all"
                  "--limit"
                  "100"
                  "--json"
                  PR_FIELDS ]
                args
        }

    [<Test>]
    member _.PrListForBranchTwoArgEmptyIsNotAnError() : Task =
        task {
            let gh =
                scripted [ "pr"; "list"; "--head"; "feat"; "--state"; "all" ] (Reply.Ok "[]")

            match! gh.PrListForBranch(".", "feat") with
            | Ok xs -> Assert.That(xs, Is.Empty)
            | Error e -> Assert.Fail $"pr list for branch failed: {e}"
        }

    [<Test>]
    member _.PrListForBranchRejectsFlagLikeHeadAndBaseBeforeSpawning() : Task =
        task {
            let gh = permissive ()

            let isErr (t: Task<Result<'T, ProcessError>>) =
                task {
                    let! r = t
                    return Result.isError r
                }

            let! a = isErr (gh.PrListForBranch(".", "--evil-head", "main"))
            let! b = isErr (gh.PrListForBranch(".", "feat", "--evil-base"))
            let! c = isErr (gh.PrListForBranch(".", "--evil-head"))
            let! d = isErr (gh.PrListForBranch(".", ""))

            for flag, name in
                [ a, "3-arg dash head"
                  b, "3-arg dash base"
                  c, "2-arg dash head"
                  d, "2-arg empty head" ] do
                Assert.That(flag, Is.True, $"{name} must be refused")
        }

    [<Test>]
    member _.PrViewBuildsNumberedQuery() : Task =
        task {
            let json =
                """{"number":42,"title":"t","state":"OPEN","headRefName":"h","baseRefName":"main","url":"u"}"""

            let gh = scripted [ "pr"; "view"; "42"; "--json" ] (Reply.Ok json)

            match! gh.PrView(".", 42UL) with
            | Ok pr -> Assert.That(pr.Number, Is.EqualTo 42UL)
            | Error e -> Assert.Fail $"pr view failed: {e}"
        }

    [<Test>]
    member _.PrDiffParsesUnifiedDiffIntoFileDiffs() : Task =
        task {
            let raw =
                "diff --git a/foo.txt b/foo.txt\n"
                + "index e69de29..4b825dc 100644\n"
                + "--- a/foo.txt\n"
                + "+++ b/foo.txt\n"
                + "@@ -1,2 +1,2 @@\n"
                + "-old line\n"
                + "+new line\n"
                + " \n"

            let gh = scripted [ "pr"; "diff"; "42" ] (Reply.Ok raw)

            match! gh.PrDiff(".", 42UL) with
            | Ok [ file ] ->
                Assert.That(file.Path, Is.EqualTo "foo.txt")
                Assert.That(file.Change, Is.EqualTo ChangeKind.Modified)
                Assert.That(file.Hunks.Length, Is.EqualTo 1)

                Assert.That(
                    file.Hunks.[0].Lines =
                        [ DiffLine.Removed "old line"; DiffLine.Added "new line"; DiffLine.Context "" ]
                )

                Assert.That(file.Raw, Is.EqualTo raw)
            | Ok other -> Assert.Fail $"expected one file diff, got {other.Length}"
            | Error e -> Assert.Fail $"pr diff failed: {e}"
        }

    [<Test>]
    member _.PrCreateBuildsAllFlagsAndReturnsUrl() : Task =
        task {
            let gh =
                scripted
                    [ "pr"; "create"; "--title"; "--body"; "--head"; "feat"; "--base"; "main" ]
                    (Reply.Ok "https://github.com/o/r/pull/9\n")

            let spec = PrCreate.Create("My title", "My body").WithHead("feat").WithBase("main")

            match! gh.PrCreate(".", spec) with
            | Ok url -> Assert.That(url, Is.EqualTo "https://github.com/o/r/pull/9", "trimmed URL")
            | Error e -> Assert.Fail $"pr create failed: {e}"
        }

    [<Test>]
    member _.PrMergeBuildsStrategyAutoAndDeleteBranch() : Task =
        task {
            let gh =
                scripted [ "pr"; "merge"; "42"; "--squash"; "--auto"; "--delete-branch" ] (Reply.Ok "")

            match! gh.PrMerge(".", 42UL, PrMerge.Squash.WithAuto().WithDeleteBranch()) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"pr merge failed: {e}"

            // The bare rebase strategy carries neither --auto nor --delete-branch.
            let plain = scripted [ "pr"; "merge"; "7"; "--rebase" ] (Reply.Ok "")

            match! plain.PrMerge(".", 7UL, PrMerge.Rebase) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"pr merge (rebase) failed: {e}"
        }

    [<Test>]
    member _.PrMarkReadyAndCloseBuildArgs() : Task =
        task {
            let ready = scripted [ "pr"; "ready"; "3" ] (Reply.Ok "")

            match! ready.PrMarkReady(".", 3UL) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"pr ready failed: {e}"

            let close = scripted [ "pr"; "close"; "3"; "--delete-branch" ] (Reply.Ok "")

            match! close.PrClose(".", 3UL, true) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"pr close failed: {e}"

            // Without delete-branch the flag must NOT appear (no fallback → the rule
            // only matches an argv that has no --delete-branch after `close 4`).
            let keep = scripted [ "pr"; "close"; "4" ] (Reply.Ok "")

            match! keep.PrClose(".", 4UL, false) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"pr close (keep branch) failed: {e}"
        }

    [<Test>]
    member _.PrReviewBuildsKindAndBody() : Task =
        task {
            let approve = scripted [ "pr"; "review"; "1"; "--approve" ] (Reply.Ok "")

            match! approve.PrReview(".", 1UL, ReviewAction.Approve) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"pr review approve failed: {e}"

            let changes =
                scripted [ "pr"; "review"; "1"; "--request-changes"; "--body"; "fix this" ] (Reply.Ok "")

            match! changes.PrReview(".", 1UL, ReviewAction.RequestChanges "fix this") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"pr review request-changes failed: {e}"

            let comment =
                scripted [ "pr"; "review"; "1"; "--comment"; "--body"; "nit" ] (Reply.Ok "")

            match! comment.PrReview(".", 1UL, ReviewAction.Comment "nit") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"pr review comment failed: {e}"
        }

    [<Test>]
    member _.PrCommentReturnsUrl() : Task =
        task {
            let gh =
                scripted [ "pr"; "comment"; "1"; "--body"; "thanks" ] (Reply.Ok "https://c/1\n")

            match! gh.PrComment(".", 1UL, "thanks") with
            | Ok url -> Assert.That(url, Is.EqualTo "https://c/1")
            | Error e -> Assert.Fail $"pr comment failed: {e}"
        }

    [<Test>]
    member _.PrEditRejectsBothNoneBeforeSpawning() : Task =
        task {
            // Permissive runner: a leak would return Ok. A refusal must return Error.
            let gh = permissive ()

            match! gh.PrEdit(".", 1UL, PrEdit.Create()) with
            | Error _ -> ()
            | Ok() -> Assert.Fail "an edit with nothing to change must be refused before spawning"

            let ok = scripted [ "pr"; "edit"; "1"; "--title"; "New" ] (Reply.Ok "")

            match! ok.PrEdit(".", 1UL, PrEdit.Create().WithTitle "New") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"pr edit (title) failed: {e}"
        }

    [<Test>]
    member _.PrFeedbackQueriesReviewsAndComments() : Task =
        task {
            let json =
                """{"reviews":[{"author":{"login":"a"},"state":"APPROVED","body":"","submittedAt":"t"}],"comments":[]}"""

            let gh =
                scripted [ "pr"; "view"; "1"; "--json"; "reviews,comments" ] (Reply.Ok json)

            match! gh.PrFeedback(".", 1UL) with
            | Ok fb ->
                Assert.That(fb.Reviews.Length, Is.EqualTo 1)
                Assert.That(fb.Reviews.[0].Author, Is.EqualTo "a")
                Assert.That(fb.Comments, Is.Empty)
            | Error e -> Assert.Fail $"pr feedback failed: {e}"
        }

    [<Test>]
    member _.RunListWithAndWithoutBranch() : Task =
        task {
            let branched =
                scripted [ "run"; "list"; "--limit"; "5"; "--branch"; "main"; "--json" ] (Reply.Ok "[]")

            match! branched.RunList(".", 5, Some "main") with
            | Ok xs -> Assert.That(xs, Is.Empty)
            | Error e -> Assert.Fail $"run list (branch) failed: {e}"

            let all = scripted [ "run"; "list"; "--limit"; "5"; "--json" ] (Reply.Ok "[]")

            match! all.RunList(".", 5, None) with
            | Ok xs -> Assert.That(xs, Is.Empty)
            | Error e -> Assert.Fail $"run list (all) failed: {e}"
        }

    [<Test>]
    member _.RunWatchWaitsThenViewsFinalState() : Task =
        task {
            let json =
                """{"databaseId":99,"name":"CI","displayTitle":"d","status":"completed","conclusion":"success","workflowName":"CI","headBranch":"main","event":"push","url":"u","createdAt":"t"}"""

            let runner =
                ScriptedRunner()
                    .On([ "run"; "watch"; "99" ], Reply.Ok "run completed\n")
                    .On([ "run"; "view"; "99"; "--json" ], Reply.Ok json)

            let gh = GitHub.WithRunner runner

            match! gh.RunWatch(".", 99UL) with
            | Ok run ->
                Assert.That(run.DatabaseId, Is.EqualTo 99UL)
                Assert.That(run.Conclusion, Is.EqualTo "success", "the follow-up run view carries the outcome")
            | Error e -> Assert.Fail $"run watch failed: {e}"
        }

    [<Test>]
    member _.IssueCreateAndViewBuildArgs() : Task =
        task {
            let create =
                scripted [ "issue"; "create"; "--title"; "bug"; "--body"; "broken" ] (Reply.Ok "https://i/1\n")

            match! create.IssueCreate(".", "bug", "broken") with
            | Ok url -> Assert.That(url, Is.EqualTo "https://i/1")
            | Error e -> Assert.Fail $"issue create failed: {e}"

            let view =
                scripted
                    [ "issue"; "view"; "1"; "--json" ]
                    (Reply.Ok """{"number":1,"title":"t","state":"OPEN","body":"b","url":"u"}""")

            match! view.IssueView(".", 1UL) with
            | Ok issue -> Assert.That(issue.Body, Is.EqualTo "b")
            | Error e -> Assert.Fail $"issue view failed: {e}"
        }

    [<Test>]
    member _.IssueCloseAndCommentBuildArgs() : Task =
        task {
            let close = scripted [ "issue"; "close"; "3" ] (Reply.Ok "")

            match! close.IssueClose(".", 3UL) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"issue close failed: {e}"

            let comment =
                scripted [ "issue"; "comment"; "1"; "--body"; "thanks" ] (Reply.Ok "https://c/1\n")

            match! comment.IssueComment(".", 1UL, "thanks") with
            | Ok url -> Assert.That(url, Is.EqualTo "https://c/1")
            | Error e -> Assert.Fail $"issue comment failed: {e}"
        }

    [<Test>]
    member _.ReleaseViewBuildsTaggedQuery() : Task =
        task {
            let json =
                """{"tagName":"v1.0.0","name":"1.0.0","body":"notes","url":"u","publishedAt":"t","isDraft":false,"isPrerelease":false}"""

            let gh = scripted [ "release"; "view"; "v1.0.0"; "--json" ] (Reply.Ok json)

            match! gh.ReleaseView(".", "v1.0.0") with
            | Ok rel -> Assert.That(rel.Body, Is.EqualTo "notes")
            | Error e -> Assert.Fail $"release view failed: {e}"
        }

// ---------------------------------------------------------------------------
// pr checks exit-code branching, and the argv injection guard
// ---------------------------------------------------------------------------

[<TestFixture>]
type SemanticsTests() =

    [<Test>]
    member _.PrChecksParsesOnExitZero() : Task =
        task {
            let json =
                """[{"name":"build","state":"SUCCESS","bucket":"pass","workflow":"CI","link":"l","startedAt":"","completedAt":""}]"""

            let gh = scripted [ "pr"; "checks"; "1"; "--json" ] (Reply.Ok json)

            match! gh.PrChecks(".", 1UL) with
            | Ok [ check ] -> Assert.That(check.Bucket = CheckBucket.Pass)
            | Ok xs -> Assert.Fail $"expected one check, got {xs.Length}"
            | Error e -> Assert.Fail $"pr checks (exit 0) failed: {e}"
        }

    [<Test>]
    member _.PrChecksParsesOnNonZeroExitWithJson() : Task =
        task {
            // gh exits 8 while checks are pending but still prints the JSON — that must
            // parse, not error. (Exit 1 for "some failed" takes the same branch.)
            let json =
                """[{"name":"test","state":"IN_PROGRESS","bucket":"pending","workflow":"CI","link":"","startedAt":"","completedAt":""}]"""

            let gh = scripted [ "pr"; "checks"; "1"; "--json" ] ((Reply.Exit 8).WithStdout json)

            match! gh.PrChecks(".", 1UL) with
            | Ok [ check ] -> Assert.That(check.Bucket.IsPending, Is.True)
            | Ok xs -> Assert.Fail $"expected one check, got {xs.Length}"
            | Error e -> Assert.Fail $"pr checks (exit 8 + json) failed: {e}"
        }

    [<Test>]
    member _.PrChecksNoChecksReportedIsEmptyList() : Task =
        task {
            // gh exits non-zero with NO JSON for a PR that simply has no checks — the
            // wrapper reads that one bare non-zero (matched on stderr) as an empty list.
            let gh =
                scripted [ "pr"; "checks"; "1"; "--json" ] (Reply.Fail(1, "no checks reported on the 'feat' branch"))

            match! gh.PrChecks(".", 1UL) with
            | Ok xs -> Assert.That(xs, Is.Empty)
            | Error e -> Assert.Fail $"pr checks (no checks) failed: {e}"
        }

    [<Test>]
    member _.PrChecksMatchesNoChecksCaseInsensitively() : Task =
        task {
            // A capitalisation tweak in gh's wording must not turn "no checks" into an error.
            let gh =
                scripted [ "pr"; "checks"; "1"; "--json" ] (Reply.Fail(1, "No Checks Reported on this branch"))

            match! gh.PrChecks(".", 1UL) with
            | Ok xs -> Assert.That(xs, Is.Empty)
            | Error e -> Assert.Fail $"pr checks (case-insensitive) failed: {e}"
        }

    [<Test>]
    member _.PrChecksSurfacesGenuineFailure() : Task =
        task {
            // A real failure (no JSON, unrelated stderr) must surface as Error, not read
            // as "no checks".
            let gh =
                scripted [ "pr"; "checks"; "1"; "--json" ] (Reply.Fail(2, "could not find pull request"))

            let! r = gh.PrChecks(".", 1UL)
            Assert.That(Result.isError r, Is.True, "an unrelated non-zero exit must error")
        }

    [<Test>]
    member _.FlagLikePositionalsAreRejectedBeforeSpawning() : Task =
        task {
            let gh = permissive ()

            let isErr (t: Task<Result<'T, ProcessError>>) =
                task {
                    let! r = t
                    return Result.isError r
                }

            // The `api` endpoint and release `tag` land in bare positional slots — a
            // `-`-leading or empty value must be refused before anything spawns.
            let! a = isErr (gh.Api(".", "-X"))
            let! b = isErr (gh.Api(".", ""))
            let! c = isErr (gh.ReleaseView(".", "--cleanup-tag"))
            let! d = isErr (gh.ReleaseView(".", ""))
            let! e = isErr (gh.ReleaseCreate(".", ReleaseCreate.Create "--draft"))
            let! f = isErr (gh.ReleaseCreate(".", ReleaseCreate.Create ""))
            let! g = isErr (gh.WorkflowDispatch(".", WorkflowDispatch.Create "--ref"))
            let! h = isErr (gh.WorkflowDispatch(".", WorkflowDispatch.Create ""))

            for flag, name in
                [ a, "api dash"
                  b, "api empty"
                  c, "release view dash"
                  d, "release view empty"
                  e, "release create dash tag"
                  f, "release create empty tag"
                  g, "workflow dispatch dash-leading workflow name"
                  h, "workflow dispatch empty workflow name" ] do
                Assert.That(flag, Is.True, $"{name} must be refused")

            // …and a legitimate endpoint still passes through.
            let ok = scripted [ "api"; "repos/o/r" ] (Reply.Ok "{}\n")

            match! ok.Api(".", "repos/o/r") with
            | Ok body -> Assert.That(body, Is.EqualTo "{}")
            | Error e -> Assert.Fail $"a valid endpoint must pass: {e}"
        }

// ---------------------------------------------------------------------------
// Exact-argv (flag absence + values), remaining exit-code branches, and the
// token-never-in-argv guarantee — things subset `.On` matching cannot verify.
// ---------------------------------------------------------------------------

[<TestFixture>]
type HardeningTests() =

    [<Test>]
    member _.PrCreateOmitsHeadAndBaseWhenNone() : Task =
        task {
            let gh, args = capturing (Reply.Ok "https://gh/pr/2\n")

            match! gh.PrCreate(".", PrCreate.Create("T", "B")) with
            | Ok url ->
                Assert.That(url, Is.EqualTo "https://gh/pr/2")
                // No --head / --base when neither was set (subset matching can't prove this).
                assertArgs [ "pr"; "create"; "--title"; "T"; "--body"; "B" ] args
            | Error e -> Assert.Fail $"pr create failed: {e}"
        }

    [<Test>]
    member _.ReleaseCreateBuildsAllFlagsWithExactValues() : Task =
        task {
            let gh, args = capturing (Reply.Ok "https://gh/releases/v1\n")

            let spec =
                ReleaseCreate.Create("v1.0.0").WithTitle("1.0.0").WithNotes("the notes").WithDraft().WithPrerelease()

            match! gh.ReleaseCreate(".", spec) with
            | Ok url ->
                Assert.That(url, Is.EqualTo "https://gh/releases/v1")

                assertArgs
                    [ "release"
                      "create"
                      "v1.0.0"
                      "--title"
                      "1.0.0"
                      "--notes"
                      "the notes"
                      "--draft"
                      "--prerelease" ]
                    args
            | Error e -> Assert.Fail $"release create failed: {e}"
        }

    [<Test>]
    member _.ReleaseCreateOmitsTitleAndAlwaysEmitsNotesWhenNone() : Task =
        task {
            // Title is omitted when unset (gh defaults it to the tag), but --notes is ALWAYS
            // emitted (empty here) so gh never falls back to its interactive editor prompt.
            let gh, args = capturing (Reply.Ok "u\n")

            match! gh.ReleaseCreate(".", ReleaseCreate.Create "v2") with
            | Ok _ -> assertArgs [ "release"; "create"; "v2"; "--notes"; "" ] args
            | Error e -> Assert.Fail $"release create failed: {e}"
        }

    [<Test>]
    member _.PrCreateEmitsHeadThenBaseWithExactValues() : Task =
        task {
            let gh, args = capturing (Reply.Ok "u\n")

            match! gh.PrCreate(".", PrCreate.Create("My title", "My body").WithHead("feat").WithBase("main")) with
            | Ok _ ->
                assertArgs
                    [ "pr"
                      "create"
                      "--title"
                      "My title"
                      "--body"
                      "My body"
                      "--head"
                      "feat"
                      "--base"
                      "main" ]
                    args
            | Error e -> Assert.Fail $"pr create failed: {e}"
        }

    [<Test>]
    member _.PrMergeBareStrategyCarriesNoExtraFlags() : Task =
        task {
            let gh, args = capturing (Reply.Ok "")

            match! gh.PrMerge(".", 7UL, PrMerge.Rebase) with
            | Ok() -> assertArgs [ "pr"; "merge"; "7"; "--rebase" ] args
            | Error e -> Assert.Fail $"pr merge failed: {e}"
        }

    [<Test>]
    member _.PrCloseWithoutDeleteBranchOmitsTheFlag() : Task =
        task {
            let gh, args = capturing (Reply.Ok "")

            match! gh.PrClose(".", 4UL, false) with
            | Ok() -> assertArgs [ "pr"; "close"; "4" ] args
            | Error e -> Assert.Fail $"pr close failed: {e}"
        }

    [<Test>]
    member _.PrCheckoutBuildsExactArgv() : Task =
        task {
            // `gh pr checkout <n>` — the number is the sole positional, no extra flags.
            let gh, args = capturing (Reply.Ok "")

            match! gh.PrCheckout(".", 42UL) with
            | Ok() -> assertArgs [ "pr"; "checkout"; "42" ] args
            | Error e -> Assert.Fail $"pr checkout failed: {e}"
        }

    [<Test>]
    member _.PrReviewApproveCarriesNoBodyByDefault() : Task =
        task {
            let gh, args = capturing (Reply.Ok "")

            match! gh.PrReview(".", 7UL, ReviewAction.Approve) with
            | Ok() -> assertArgs [ "pr"; "review"; "7"; "--approve" ] args
            | Error e -> Assert.Fail $"pr review approve failed: {e}"
        }

    [<Test>]
    member _.PrReviewApproveWithBodyEmitsBody() : Task =
        task {
            let gh, args = capturing (Reply.Ok "")

            match! gh.PrReview(".", 7UL, ReviewAction.Approve.WithBody "LGTM") with
            | Ok() -> assertArgs [ "pr"; "review"; "7"; "--approve"; "--body"; "LGTM" ] args
            | Error e -> Assert.Fail $"pr review approve+body failed: {e}"
        }

    [<Test>]
    member _.PrEditBodyOnlyEmitsOnlyBody() : Task =
        task {
            let gh, args = capturing (Reply.Ok "")

            match! gh.PrEdit(".", 7UL, PrEdit.Create().WithBody "New body") with
            | Ok() -> assertArgs [ "pr"; "edit"; "7"; "--body"; "New body" ] args
            | Error e -> Assert.Fail $"pr edit (body only) failed: {e}"
        }

    [<Test>]
    member _.PrEditEmptyStringClearsField() : Task =
        task {
            // An empty string is a real value (gh clears the field on `--title ""`); it
            // must reach the CLI verbatim, not be silently dropped as if it were None.
            let gh, args = capturing (Reply.Ok "")

            match! gh.PrEdit(".", 7UL, PrEdit.Create().WithTitle "") with
            | Ok() -> assertArgs [ "pr"; "edit"; "7"; "--title"; "" ] args
            | Error e -> Assert.Fail $"pr edit (empty title) failed: {e}"
        }

    [<Test>]
    member _.WorkflowDispatchWithRefAndInputsUsesRawFieldNotField() : Task =
        task {
            // The whole point of this method: input values MUST ride on --raw-field, never
            // --field — --field's value is subject to gh's @-syntax (file read), --raw-field
            // is always literal.
            let gh, args = capturing (Reply.Ok "")

            let spec =
                WorkflowDispatch
                    .Create("triage.yml")
                    .WithRef("my-branch")
                    .WithInput("name", "scully")
                    .WithInput("greeting", "hello")

            match! gh.WorkflowDispatch(".", spec) with
            | Ok() ->
                assertArgs
                    [ "workflow"
                      "run"
                      "triage.yml"
                      "--ref"
                      "my-branch"
                      "--raw-field"
                      "name=scully"
                      "--raw-field"
                      "greeting=hello" ]
                    args

                Assert.That(args |> Seq.contains "--field", Is.False, "must never emit --field")
            | Error e -> Assert.Fail $"workflow dispatch failed: {e}"
        }

    [<Test>]
    member _.WorkflowDispatchWithoutRefOrInputsOmitsThem() : Task =
        task {
            let gh, args = capturing (Reply.Ok "")

            match! gh.WorkflowDispatch(".", WorkflowDispatch.Create "triage.yml") with
            | Ok() -> assertArgs [ "workflow"; "run"; "triage.yml" ] args
            | Error e -> Assert.Fail $"workflow dispatch (bare) failed: {e}"
        }

    [<Test>]
    member _.RunRerunAllScopeOmitsFailedFlag() : Task =
        task {
            let gh, args = capturing (Reply.Ok "")

            match! gh.RunRerun(".", 42UL, RerunScope.All) with
            | Ok() -> assertArgs [ "run"; "rerun"; "42" ] args
            | Error e -> Assert.Fail $"run rerun (all) failed: {e}"
        }

    [<Test>]
    member _.RunRerunFailedOnlyScopeEmitsFailedFlag() : Task =
        task {
            let gh, args = capturing (Reply.Ok "")

            match! gh.RunRerun(".", 42UL, RerunScope.FailedOnly) with
            | Ok() -> assertArgs [ "run"; "rerun"; "42"; "--failed" ] args
            | Error e -> Assert.Fail $"run rerun (failed only) failed: {e}"
        }

    [<Test>]
    member _.RunCancelBuildsExactArgv() : Task =
        task {
            let gh, args = capturing (Reply.Ok "")

            match! gh.RunCancel(".", 7UL) with
            | Ok() -> assertArgs [ "run"; "cancel"; "7" ] args
            | Error e -> Assert.Fail $"run cancel failed: {e}"
        }

    [<Test>]
    member _.IssueListLimitsTo100AndParses() : Task =
        task {
            let json = """[{"number":3,"title":"Docs","state":"OPEN","body":"","url":""}]"""

            let gh =
                scripted
                    [ "issue"
                      "list"
                      "--limit"
                      "100"
                      "--json"
                      "number,title,state,body,url,labels,assignees,author,createdAt,updatedAt,milestone" ]
                    (Reply.Ok json)

            match! gh.IssueList "." with
            | Ok [ issue ] -> Assert.That(issue.Number, Is.EqualTo 3UL)
            | Ok xs -> Assert.Fail $"expected one issue, got {xs.Length}"
            | Error e -> Assert.Fail $"issue list failed: {e}"
        }

    [<Test>]
    member _.ReleaseListLimitsTo100AndParses() : Task =
        task {
            let json =
                """[{"tagName":"v1.0.0","name":"1.0.0","isLatest":true,"isDraft":false,"isPrerelease":false,"publishedAt":"t"}]"""

            let gh =
                scripted
                    [ "release"
                      "list"
                      "--limit"
                      "100"
                      "--json"
                      "tagName,name,isLatest,isDraft,isPrerelease,publishedAt" ]
                    (Reply.Ok json)

            match! gh.ReleaseList "." with
            | Ok [ rel ] -> Assert.That(rel.TagName, Is.EqualTo "v1.0.0")
            | Ok xs -> Assert.Fail $"expected one release, got {xs.Length}"
            | Error e -> Assert.Fail $"release list failed: {e}"
        }

    [<Test>]
    member _.PrListPinsFullFieldSet() : Task =
        task {
            // The full PR field set must be requested — a short/wrong field set
            // would silently drop columns the DTO expects.
            let json = """[]"""

            let gh =
                scripted
                    [ "pr"
                      "list"
                      "--limit"
                      "100"
                      "--json"
                      "number,title,state,headRefName,baseRefName,url,labels,assignees,author,createdAt,updatedAt,milestone" ]
                    (Reply.Ok json)

            match! gh.PrList "." with
            | Ok xs -> Assert.That(xs, Is.Empty)
            | Error e -> Assert.Fail $"pr list failed: {e}"
        }

    [<Test>]
    member _.AuthStatusUnusualExitCodeReadsFalse() : Task =
        task {
            // Any non-zero exit — not just 1 — reads as "not authenticated", never an error.
            let gh = scripted [ "auth"; "status" ] (Reply.Exit 2)

            match! gh.AuthStatus() with
            | Ok v -> Assert.That(v, Is.False)
            | Error e -> Assert.Fail $"auth status (exit 2) failed: {e}"
        }

    [<Test>]
    member _.AuthStatusErrorsOnAbnormalTermination() : Task =
        task {
            // A signal kill has no faithful exit code — it must surface as an Error,
            // NOT be silently read as "not authenticated" (regression guard).
            let gh = scripted [ "auth"; "status" ] (Reply.Signalled 9)

            let! r = gh.AuthStatus()
            Assert.That(Result.isError r, Is.True, "an abnormal termination must error, not read false")
        }

    [<Test>]
    member _.PrChecksParsesOnExit1WithJson() : Task =
        task {
            // Exit 1 ("some failed") also prints JSON — it must take the same parse
            // branch as exit 8 (guards the `Some 1` arm of the or-pattern guard).
            let json =
                """[{"name":"test","state":"FAILURE","bucket":"fail","workflow":"CI","link":"","startedAt":"","completedAt":""}]"""

            let gh = scripted [ "pr"; "checks"; "1"; "--json" ] ((Reply.Exit 1).WithStdout json)

            match! gh.PrChecks(".", 1UL) with
            | Ok [ check ] -> Assert.That(check.Bucket.IsFailing, Is.True)
            | Ok xs -> Assert.Fail $"expected one check, got {xs.Length}"
            | Error e -> Assert.Fail $"pr checks (exit 1 + json) failed: {e}"
        }

    [<Test>]
    member _.PrChecksGenuineExit1WithoutNoChecksErrors() : Task =
        task {
            // Exit 1 shares its code with the benign "no checks" case; a *different*
            // exit-1 reason (no JSON, unrelated stderr) must still surface as an error,
            // not be mistaken for the empty-checks case.
            let gh =
                scripted [ "pr"; "checks"; "1"; "--json" ] (Reply.Fail(1, "no pull requests found for branch 'x'"))

            let! r = gh.PrChecks(".", 1UL)
            Assert.That(Result.isError r, Is.True, "a non-'no-checks' exit 1 must error")
        }

    [<Test>]
    member _.PrChecksAuthRequiredExit4Errors() : Task =
        task {
            let gh = scripted [ "pr"; "checks"; "1"; "--json" ] (Reply.Fail(4, "auth required"))
            let! r = gh.PrChecks(".", 1UL)
            Assert.That(Result.isError r, Is.True, "exit 4 (auth) is a real failure, not an outcome")
        }

    [<Test>]
    member _.RunWatchFailedWatchErrorsWithoutViewing() : Task =
        task {
            // A failing/killed watch must error via ensureSuccess and NOT proceed to a
            // `run view` (which would read a half-finished run). No `run view` rule is
            // scripted, so if RunWatch wrongly viewed, the ScriptedRunner would raise.
            let gh = scripted [ "run"; "watch"; "42" ] (Reply.Fail(1, "no such run"))

            let! r = gh.RunWatch(".", 42UL)
            Assert.That(Result.isError r, Is.True, "a failed watch must error, not read a stale run")
        }

    [<Test>]
    member _.RunWatchAbnormalWatchErrorsWithoutViewing() : Task =
        task {
            // Same safety property for an abnormal (signal) termination.
            let gh = scripted [ "run"; "watch"; "42" ] (Reply.Signalled 9)

            let! r = gh.RunWatch(".", 42UL)
            Assert.That(Result.isError r, Is.True, "an abnormally-terminated watch must error, not view")
        }

    [<Test>]
    member _.TokenIsNeverPlacedInArgv() : Task =
        task {
            // A supplied token is injected as GH_TOKEN (an env var), never into argv —
            // so it can't leak through `ps`. Assert the secret appears nowhere in the args.
            let secret = "ghp_supersecrettoken"
            let baseGh, args = capturing (Reply.Ok "[]")
            let gh = baseGh.WithToken secret

            match! gh.PrList "." with
            | Ok _ ->
                Assert.That(args |> Seq.exists (fun a -> a.Contains secret), Is.False, "secret must never be in argv")
            | Error e -> Assert.Fail $"pr list (with token) failed: {e}"
        }

[<TestFixture>]
type AtViewTests() =

    let capturingCmd (reply: Reply) : (Command option ref) * ScriptedRunner =
        let captured = ref (None: Command option)

        let runner =
            ScriptedRunner()
                .When(
                    (fun (c: Command) ->
                        captured.Value <- Some c
                        true),
                    reply
                )

        captured, runner

    [<Test>]
    member _.GitHubAtBindsDirForModelledMethodsAndRawRun() : Task =
        task {
            // `Api` is a MODELLED (dir-bound) method — `at.Api(x)` binds `dir` as the cwd and
            // produces byte-identical argv.
            let captured, runner = capturingCmd (Reply.Ok "{}")
            let gh = GitHub.WithRunner runner

            let! _ = gh.At("/bound/dir").Api "repos/o/r"

            match captured.Value with
            | Some cmd ->
                Assert.That(cmd.WorkingDirectory, Is.EqualTo(Some "/bound/dir"), "Api is bound to dir")
                Assert.That(String.concat " " cmd.Arguments, Is.EqualTo "api repos/o/r")
            | None -> Assert.Fail "no command captured for Api"

            // The raw `Run` hatch on the bound view now runs in the bound `dir` too.
            let captured2, runner2 = capturingCmd (Reply.Ok "")
            let gh2 = GitHub.WithRunner runner2

            let! _ = gh2.At("/bound/dir").Run [ "auth"; "status" ]

            match captured2.Value with
            | Some cmd ->
                Assert.That(cmd.WorkingDirectory, Is.EqualTo(Some "/bound/dir"), "the raw Run hatch binds dir")
            | None -> Assert.Fail "no command captured for Run"

            // The unbound client's raw `Run` still runs in the process cwd.
            let captured3, runner3 = capturingCmd (Reply.Ok "")
            let gh3 = GitHub.WithRunner runner3

            let! _ = gh3.Run [ "auth"; "status" ]

            match captured3.Value with
            | Some cmd -> Assert.That(cmd.WorkingDirectory, Is.EqualTo None, "the unbound raw Run is NOT bound to dir")
            | None -> Assert.Fail "no command captured for unbound Run"
        }

// ---------------------------------------------------------------------------
// CLI version parsing + Capabilities floor
// ---------------------------------------------------------------------------

[<TestFixture>]
type VersionTests() =

    [<Test>]
    member _.ParsesLeadingSemverTokenFromBanner() =
        // A real `gh --version` banner → the leading semver token; trailers are tolerated.
        match GitHubParse.parseVersion "gh version 2.40.0 (2023-11-30)\nhttps://github.com/cli/cli" with
        | Some v ->
            Assert.That(v.Major, Is.EqualTo 2UL)
            Assert.That(v.Minor, Is.EqualTo 40UL)
            Assert.That(v.Patch, Is.EqualTo 0UL)
        | None -> Assert.Fail "a standard gh version banner must parse"

    [<Test>]
    member _.UnrecognisedVersionDegradesToNone() =
        // No `N.N[.N]` token → explicit None ("unknown"), never a throw.
        Assert.That(GitHubParse.parseVersion "gh (dev build, no version)", Is.EqualTo None)
        Assert.That(GitHubParse.parseVersion "", Is.EqualTo None)

    [<Test>]
    member _.CapabilitiesReportsSupportedVersion() : Task =
        task {
            let gh = scripted [ "--version" ] (Reply.Ok "gh version 2.40.0\n")

            match! gh.Capabilities() with
            | Ok caps ->
                Assert.That(caps.Version.ToString(), Is.EqualTo "2.40.0")
                Assert.That(caps.IsSupported, Is.True, "gh 2.40 meets the 2.0 floor")

                match caps.EnsureSupported() with
                | Ok() -> ()
                | Error e -> Assert.Fail $"EnsureSupported must pass at/above the floor: {e}"
            | Error e -> Assert.Fail $"capabilities failed: {e}"
        }

    [<Test>]
    member _.CapabilitiesFlagsBelowFloor() : Task =
        task {
            // A pre-2.0 gh is below the floor → not supported, EnsureSupported errors.
            let gh = scripted [ "--version" ] (Reply.Ok "gh version 1.14.0\n")

            match! gh.Capabilities() with
            | Ok caps ->
                Assert.That(caps.IsSupported, Is.False, "gh 1.14 is below the 2.0 floor")

                match caps.EnsureSupported() with
                | Error _ -> ()
                | Ok() -> Assert.Fail "EnsureSupported must error below the floor"
            | Error e -> Assert.Fail $"capabilities failed: {e}"
        }

    [<Test>]
    member _.CapabilitiesErrorsOnUnrecognisedBanner() : Task =
        task {
            // An unrecognisable banner is a predictable Parse error, not a throw.
            let gh = scripted [ "--version" ] (Reply.Ok "gh (dev build)\n")

            match! gh.Capabilities() with
            | Error _ -> ()
            | Ok _ -> Assert.Fail "an unrecognisable version banner must be an Error"
        }

// ---------------------------------------------------------------------------
// GitHub Enterprise host: GitHubHost classification, host-scoped token env, and
// the per-host `auth status --hostname` probe.
// ---------------------------------------------------------------------------

[<TestFixture>]
type EnterpriseHostTests() =

    // Unwrap a GitHubHost, failing the test loudly on an unexpected classification error.
    let hostOf (name: string) : GitHubHost =
        match GitHubHost.New name with
        | Ok h -> h
        | Error e ->
            Assert.Fail $"host {name} should classify: {e}"
            failwith "unreachable"

    [<Test>]
    member _.GitHubHostClassifiesSaasAndEnterprise() =
        // github.com (any case) is SaaS, canonicalised to lower-case; every other valid
        // host is a GHES host.
        let saas = GitHubHost.GitHubCom
        Assert.That(saas.IsGitHubCom, Is.True)
        Assert.That(saas.IsEnterprise, Is.False)
        Assert.That(saas.Host, Is.EqualTo "github.com")

        for h in [ "github.com"; "GitHub.com"; "GITHUB.COM" ] do
            let host = hostOf h
            Assert.That(host.IsGitHubCom, Is.True, $"{h} should classify as SaaS")
            Assert.That(host.Host, Is.EqualTo "github.com", "canonicalised to lower-case")

        let ghes = hostOf "GHE.Example.COM"
        Assert.That(ghes.IsEnterprise, Is.True)
        Assert.That(ghes.Host, Is.EqualTo "ghe.example.com", "canonicalised to lower-case")

    [<Test>]
    member _.GitHubHostTokenEnvVarSelectsPerHost() =
        // The exact per-host credential-env SELECTION — github.com reads GH_TOKEN, a GHES
        // host reads GH_ENTERPRISE_TOKEN (the internal seam `WithHost` binds `WithTokenEnv`
        // to). ProcessKit keeps a command's child env off the public test surface (secret
        // hygiene), so this decision point is asserted directly rather than by reading the
        // injected env value back off a captured command.
        Assert.That(GitHubHost.GitHubCom.TokenEnvVar, Is.EqualTo "GH_TOKEN")
        Assert.That((hostOf "ghe.example.com").TokenEnvVar, Is.EqualTo "GH_ENTERPRISE_TOKEN")

    [<Test>]
    member _.GitHubHostNewRejectsMalformedHosts() =
        // A malformed host is a diagnosable error, never a silent github.com guess — so a
        // bad host can't quietly authenticate as the SaaS default.
        for bad in
            [ ""
              "  "
              "-evil"
              "has space"
              "https://github.com"
              "github.com/owner"
              "ghe.example.com:8443"
              "user@github.com"
              ".leading"
              "trailing." ] do
            match GitHubHost.New bad with
            | Error _ -> ()
            | Ok h -> Assert.Fail $"\"{bad}\" must be rejected as invalid, not become {h.Host}"

    [<Test>]
    member _.GitHubHostFromRemoteUrlParsesAndClassifies() =
        // Derive + classify the host across HTTPS / SSH / scp-like remotes, dropping any
        // userinfo and port.
        let cases =
            [ "https://github.com/o/r.git", "github.com", false
              "https://x-access-token:tok@ghe.example.com:8443/o/r", "ghe.example.com", true
              "http://ghe.internal.corp/o/r", "ghe.internal.corp", true
              "ssh://git@github.com/o/r", "github.com", false
              "ssh://git@ghe.example.com:22/o/r", "ghe.example.com", true
              "git@github.com:o/r.git", "github.com", false
              "git@ghe.example.com:o/r.git", "ghe.example.com", true ]

        for url, host, enterprise in cases do
            match GitHubHost.OfRemoteUrl url with
            | Ok parsed ->
                Assert.That(parsed.Host, Is.EqualTo host, $"host for {url}")
                Assert.That(parsed.IsEnterprise, Is.EqualTo enterprise, $"class for {url}")
            | Error e -> Assert.Fail $"parse {url}: {e}"

    [<Test>]
    member _.GitHubHostFromRemoteUrlRejectsAmbiguous() =
        // An unparseable / hostless / ambiguous remote is a diagnosable error, never a
        // silent github.com fallback (which would authenticate the wrong host).
        for url in
            [ ""
              "   "
              "not-a-url"
              "https://"
              "ssh://"
              "git@internalhost:repo.git"
              "C:\\repo\\path"
              "https://[::1]:8443/x" ] do
            match GitHubHost.OfRemoteUrl url with
            | Error _ -> ()
            | Ok h -> Assert.Fail $"\"{url}\" must be a diagnosable error, not classify as {h.Host}"

    [<Test>]
    member _.AuthStatusForScopesToHostname() : Task =
        task {
            // `--hostname <host>` scopes the probe to one host, for both github.com and a
            // GHES host — the exact argv is observable (unlike the injected token env).
            let ghes, ghesArgs = capturing (Reply.Ok "")

            match! ghes.AuthStatusFor(hostOf "ghe.example.com") with
            | Ok authed ->
                Assert.That(authed, Is.True, "a zero exit reads as authenticated")
                assertArgs [ "auth"; "status"; "--hostname"; "ghe.example.com" ] ghesArgs
            | Error e -> Assert.Fail $"auth status for (GHES) failed: {e}"

            let saas, saasArgs = capturing (Reply.Ok "")

            match! saas.AuthStatusFor GitHubHost.GitHubCom with
            | Ok _ -> assertArgs [ "auth"; "status"; "--hostname"; "github.com" ] saasArgs
            | Error e -> Assert.Fail $"auth status for (github.com) failed: {e}"
        }

    [<Test>]
    member _.AuthStatusForReadsNonZeroAsUnauthenticated() : Task =
        task {
            // A non-zero exit for the scoped host reads as "not authenticated", never an error.
            let gh =
                scripted [ "auth"; "status"; "--hostname"; "ghe.example.com" ] (Reply.Exit 1)

            match! gh.AuthStatusFor(hostOf "ghe.example.com") with
            | Ok v -> Assert.That(v, Is.False)
            | Error e -> Assert.Fail $"auth status for (exit 1) failed: {e}"
        }

    [<Test>]
    member _.WithHostEnterpriseScopesRequestHostAndKeepsSecretOutOfArgv() : Task =
        task {
            // A host-keyed provider serves a per-host secret. A GHES-bound client must ask
            // it with the BOUND host (so it draws the enterprise secret, not github.com's),
            // and the secret must never reach argv. (The token-env VALUE isn't observable —
            // ProcessKit keeps child env off the public test surface — so host scoping is
            // verified through the CredentialRequest, exactly as the CliSupport suite does;
            // the per-host env-var name is covered by GitHubHostTokenEnvVarSelectsPerHost.)
            let seenHost = ref (None: string option option)

            let provider =
                Credentials.providerFn (fun r ->
                    seenHost.Value <- Some r.Host

                    match r.Host with
                    | Some "ghe.example.com" -> Ok(Some(Credential.Token "ent-secret"))
                    | Some "github.com" -> Ok(Some(Credential.Token "saas-secret"))
                    | _ -> Ok None)

            let baseGh, args = capturing (Reply.Ok "[]")
            let gh = baseGh.WithHost(hostOf "ghe.example.com").WithCredentials provider

            match! gh.PrList "." with
            | Ok _ ->
                Assert.That(
                    seenHost.Value,
                    Is.EqualTo(Some(Some "ghe.example.com")),
                    "the bound GHES host reaches the CredentialRequest"
                )

                Assert.That(
                    args |> Seq.exists (fun a -> a.Contains "ent-secret"),
                    Is.False,
                    "secret must never be in argv"
                )

                Assert.That(
                    args |> Seq.exists (fun a -> a.Contains "saas-secret"),
                    Is.False,
                    "the github.com secret must never leak into a GHES command"
                )
            | Error e -> Assert.Fail $"pr list (GHES host) failed: {e}"
        }

    [<Test>]
    member _.WithHostGitHubComScopesRequestHost() : Task =
        task {
            // The github.com binding scopes the CredentialRequest to github.com too, so a
            // host-keyed provider draws the SaaS secret (never the enterprise one).
            let seenHost = ref (None: string option option)

            let provider =
                Credentials.providerFn (fun r ->
                    seenHost.Value <- Some r.Host
                    Ok(Some(Credential.Token "saas-secret")))

            let baseGh, args = capturing (Reply.Ok "[]")
            let gh = baseGh.WithHost(GitHubHost.GitHubCom).WithCredentials provider

            match! gh.PrList "." with
            | Ok _ ->
                Assert.That(
                    seenHost.Value,
                    Is.EqualTo(Some(Some "github.com")),
                    "the bound github.com host reaches the CredentialRequest"
                )

                Assert.That(
                    args |> Seq.exists (fun a -> a.Contains "saas-secret"),
                    Is.False,
                    "secret must never be in argv"
                )
            | Error e -> Assert.Fail $"pr list (github.com host) failed: {e}"
        }

[<TestFixture>]
type ObserverWiringTests() =

    [<Test>]
    member _.WithObserverThreadsThroughTheGitHubClient() : Task =
        task {
            let events = ResizeArray<CommandEvent>()

            let observer =
                { new ICommandObserver with
                    member _.OnStarted(ev) = events.Add ev
                    member _.OnFinished(_, _, _) = () }

            let gh =
                GitHub.WithRunner(ScriptedRunner().Fallback(Reply.Ok "gh version 2.62.0")).WithObserver observer

            match! gh.Run [ "--version" ] with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"{e}"

            Assert.That(events.Count, Is.EqualTo 1, "the observer is threaded through the GitHub client")
            Assert.That(events[0].Program, Is.EqualTo "gh")
        }
