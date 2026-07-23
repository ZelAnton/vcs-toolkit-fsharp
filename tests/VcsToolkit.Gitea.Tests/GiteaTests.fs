module VcsToolkit.Gitea.Tests

open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.CliSupport
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

// Build tea 0.9.2's `--output csv` (`outputdsv`) text from rows of cells: each row is printed
// as `"c1","c2",...` — every cell wrapped in double quotes, joined by the literal three-char
// delimiter `","`, one row per line, header row first. This mirrors exactly what the real
// `tea` writes (and is the format `GiteaParse` is contracted against), so the fixtures below
// document the contract instead of hand-escaping quotes.
let private teaCsv (rows: string list list) : string =
    rows
    |> List.map (fun cells -> "\"" + String.concat "\",\"" cells + "\"")
    |> String.concat "\n"

// The column headers tea emits for each listing (pr/issue columns are pinned by this
// wrapper's `--fields`; release/login use tea's fixed default columns).
let private prHeader = [ "index"; "title"; "state"; "head"; "base"; "url" ]
let private issueHeader = [ "index"; "title"; "state"; "body"; "url" ]

let private releaseHeader =
    [ "Tag-Name"; "Title"; "Published At"; "Status"; "Tar URL" ]

let private loginHeader = [ "Name"; "URL"; "SSHHost"; "User"; "Default" ]

// tea prints this to stdout at exit 0 when asked for an unsupported `--output` (e.g. `json`,
// K-049) — it must never be read as a successful empty result.
let private unknownOutputDiagnostic =
    "unknown output type 'json', available types are:\n- csv\n- tsv\n- yaml"

// ---------------------------------------------------------------------------
// Pure parsers over `tea … --output csv` (tea 0.9.2's quoted `outputdsv` tables)
// ---------------------------------------------------------------------------

[<TestFixture>]
type ParseTests() =

    [<Test>]
    member _.PrListRowParsesIndexAndColumns() =
        let csv =
            teaCsv [ prHeader; [ "7"; "Add X"; "open"; "feat/x"; "main"; "https://gitea/pr/7" ] ]

        match expectOk (GiteaParse.parsePrList csv) with
        | [ pr ] ->
            Assert.That(pr.Number, Is.EqualTo 7UL)
            Assert.That(pr.Title, Is.EqualTo "Add X")
            Assert.That(pr.State, Is.EqualTo "open")
            Assert.That(pr.Merged, Is.False)
            Assert.That(pr.HeadBranch, Is.EqualTo "feat/x")
            Assert.That(pr.BaseBranch, Is.EqualTo "main")
            Assert.That(pr.Url, Is.EqualTo "https://gitea/pr/7")
        | other -> Assert.Fail $"expected one PR, got {other.Length}"

    [<Test>]
    member _.PrMergedStateDerivesTheFlag() =
        let csv = teaCsv [ prHeader; [ "9"; "done"; "merged"; "f"; "main"; "u" ] ]

        match expectOk (GiteaParse.parsePrList csv) with
        | [ pr ] ->
            Assert.That(pr.Number, Is.EqualTo 9UL)
            Assert.That(pr.Merged, Is.True)
            Assert.That(pr.State, Is.EqualTo "merged")
        | other -> Assert.Fail $"expected one PR, got {other.Length}"

    [<Test>]
    member _.PrListPreservesElementOrder() =
        // The parser must keep input row order (a `List.rev` regression would flip it).
        let csv =
            teaCsv
                [ prHeader
                  [ "6"; "first"; "open"; "a"; "main"; "u6" ]
                  [ "7"; "second"; "open"; "b"; "main"; "u7" ] ]

        match expectOk (GiteaParse.parsePrList csv) with
        | [ a; b ] ->
            Assert.That(a.Number, Is.EqualTo 6UL)
            Assert.That(a.Title, Is.EqualTo "first")
            Assert.That(b.Number, Is.EqualTo 7UL)
            Assert.That(b.Title, Is.EqualTo "second")
        | other -> Assert.Fail $"expected two PRs in order, got {other.Length}"

    [<Test>]
    member _.PrNonNumericIndexIsError() =
        // A non-numeric index is a real parse failure, not a silent 0 that PrView could "find".
        match GiteaParse.parsePrList (teaCsv [ prHeader; [ "x"; "t"; "open"; "h"; "main"; "u" ] ]) with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "a non-numeric index must be an Error"

    [<Test>]
    member _.PrEmptyIndexIsError() =
        // An empty `index` cell is a real parse failure, not a silent 0.
        match GiteaParse.parsePrList (teaCsv [ prHeader; [ ""; "t"; "open"; "h"; "main"; "u" ] ]) with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "a blank index must be an Error"

    [<Test>]
    member _.PrWrongColumnCountIsError() =
        // A row whose cell count differs from the pinned columns (an ambiguous embedded `","`
        // in a value, or unexpected tea output) is an Error, never a silent field shift.
        match GiteaParse.parsePrList (teaCsv [ prHeader; [ "7"; "only three"; "open" ] ]) with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "a short row must be an Error, not a defaulted record"

    [<Test>]
    member _.IssueListRowAndEmptyColumns() =
        let full =
            teaCsv [ issueHeader; [ "12"; "Bug"; "open"; "broken"; "https://gitea/issues/12" ] ]

        match expectOk (GiteaParse.parseIssueList full) with
        | [ issue ] ->
            Assert.That(issue.Number, Is.EqualTo 12UL)
            Assert.That(issue.Body, Is.EqualTo "broken")
            Assert.That(issue.Url, Is.EqualTo "https://gitea/issues/12")
        | other -> Assert.Fail $"expected one issue, got {other.Length}"

        // Empty body/url cells still parse (positional columns, empty string values).
        match expectOk (GiteaParse.parseIssueList (teaCsv [ issueHeader; [ "4"; "wip"; "open"; ""; "" ] ])) with
        | [ issue ] ->
            Assert.That(issue.Number, Is.EqualTo 4UL)
            Assert.That(issue.Body, Is.EqualTo "")
            Assert.That(issue.Url, Is.EqualTo "")
        | other -> Assert.Fail $"expected one issue, got {other.Length}"

    [<Test>]
    member _.ReleaseListParsesFixedColumns() =
        let csv =
            teaCsv
                [ releaseHeader
                  [ "0.1"
                    "First"
                    "2023-07-26T13:02:36Z"
                    "released"
                    "http://gitea/archive/0.1.tar.gz" ] ]

        match expectOk (GiteaParse.parseReleaseList csv) with
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
        match expectOk (GiteaParse.parseReleaseList (teaCsv [ releaseHeader; [ "v2"; "Two"; ""; "draft"; "" ] ])) with
        | [ rel ] ->
            Assert.That(rel.Draft, Is.True)
            Assert.That(rel.Prerelease, Is.False)
        | _ -> Assert.Fail "expected one release"

        match
            expectOk (GiteaParse.parseReleaseList (teaCsv [ releaseHeader; [ "v3"; "RC"; ""; "prerelease"; "" ] ]))
        with
        | [ rel ] ->
            Assert.That(rel.Prerelease, Is.True)
            Assert.That(rel.Draft, Is.False)
        | _ -> Assert.Fail "expected one release"

    [<Test>]
    member _.ReleaseEmptyTagIsError() =
        match GiteaParse.parseReleaseList (teaCsv [ releaseHeader; [ ""; "no tag"; ""; "released"; "" ] ]) with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "a release row with a blank tag must be an Error"

    [<Test>]
    member _.HasLoginsCountsDataRows() =
        Assert.That(
            expectOk (GiteaParse.parseHasLogins (teaCsv [ loginHeader; [ "gitea"; "u"; ""; "vcs"; "*" ] ])),
            Is.True
        )

        // A header-only table (no logins configured) reads as false.
        Assert.That(expectOk (GiteaParse.parseHasLogins (teaCsv [ loginHeader ])), Is.False)

        // A non-tabular first line (e.g. an unsupported-output diagnostic) is an Error.
        match GiteaParse.parseHasLogins "not a table" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "a non-tabular logins document must be an Error"

    [<Test>]
    member _.NonTabularOutputIsError() =
        match GiteaParse.parsePrList "not csv output" with
        | Error _ -> ()
        | Ok _ -> Assert.Fail "non-tabular output must be an Error"

    [<Test>]
    member _.EmptyOutputParsesAsEmptyList() =
        // Some tea builds print NOTHING for an empty listing (an empty repo is a normal state).
        match GiteaParse.parsePrList "" with
        | Ok [] -> ()
        | other -> Assert.Fail $"empty output should parse as an empty list, got {other}"

        match GiteaParse.parseIssueList "  \n " with
        | Ok [] -> ()
        | other -> Assert.Fail $"whitespace output should parse as an empty list, got {other}"

        match GiteaParse.parseReleaseList "" with
        | Ok [] -> ()
        | other -> Assert.Fail $"empty output should parse as an empty list, got {other}"

    [<Test>]
    member _.HeaderOnlyParsesAsEmptyList() =
        // Other tea builds print only the header row for an empty listing — also an empty list.
        match GiteaParse.parsePrList (teaCsv [ prHeader ]) with
        | Ok [] -> ()
        | other -> Assert.Fail $"a header-only PR table should be an empty list, got {other}"

        match GiteaParse.parseIssueList (teaCsv [ issueHeader ]) with
        | Ok [] -> ()
        | other -> Assert.Fail $"a header-only issue table should be an empty list, got {other}"

        match GiteaParse.parseReleaseList (teaCsv [ releaseHeader ]) with
        | Ok [] -> ()
        | other -> Assert.Fail $"a header-only release table should be an empty list, got {other}"

    [<Test>]
    member _.UnicodeAndCommaInTextFieldsSurviveParsing() =
        // A title with Unicode and an ordinary comma (not the `","` cell delimiter) round-trips
        // intact — tea's `outputdsv` only shifts fields on a literal `","` inside a cell.
        let title = "Ремонт, пожалуйста — fix the café build"

        let csv = teaCsv [ prHeader; [ "7"; title; "open"; "feat/x"; "main"; "u" ] ]

        match expectOk (GiteaParse.parsePrList csv) with
        | [ pr ] ->
            Assert.That(pr.Title, Is.EqualTo title)
            Assert.That(pr.State, Is.EqualTo "open", "the comma in the title must not shift the state column")
        | other -> Assert.Fail $"expected one PR, got {other.Length}"

    [<Test>]
    member _.UnsupportedOutputDiagnosticIsErrorNotEmptyList() =
        // K-049 regression barrier: tea prints `unknown output type 'json' …` at exit 0 for an
        // unsupported `--output`. Every list parser must reject that as an Error — NEVER read it
        // as a successful empty result.
        for name, parse in
            [ "parsePrList", (GiteaParse.parsePrList >> Result.map (fun _ -> ()))
              "parseIssueList", (GiteaParse.parseIssueList >> Result.map (fun _ -> ()))
              "parseReleaseList", (GiteaParse.parseReleaseList >> Result.map (fun _ -> ()))
              "parseHasLogins", (GiteaParse.parseHasLogins >> Result.map (fun _ -> ())) ] do
            match parse unknownOutputDiagnostic with
            | Error _ -> ()
            | Ok _ -> Assert.Fail $"{name} must reject the `unknown output type` diagnostic, not read it as empty"

// ---------------------------------------------------------------------------
// Client: hermetic argv-building + parsing via ScriptedRunner
// ---------------------------------------------------------------------------

[<TestFixture>]
type ClientTests() =

    [<Test>]
    member _.PrListRequestsFieldsAndLimit() : Task =
        task {
            let csv = teaCsv [ prHeader; [ "1"; "t"; "open"; "h"; "main"; "u" ] ]

            let tea =
                scripted
                    [ "pr"
                      "list"
                      "--limit"
                      "100"
                      "--fields"
                      "index,title,state,head,base,url"
                      "--output"
                      "csv" ]
                    (Reply.Ok csv)

            match! tea.PrList "." with
            | Ok [ pr ] -> Assert.That(pr.Number, Is.EqualTo 1UL)
            | Ok xs -> Assert.Fail $"expected one PR, got {xs.Length}"
            | Error e -> Assert.Fail $"pr list failed: {e}"
        }

    [<Test>]
    member _.PrViewFindsOnFirstPage() : Task =
        task {
            let csv =
                teaCsv
                    [ prHeader
                      [ "6"; "other"; "open"; "a"; "main"; "u6" ]
                      [ "7"; "mine"; "open"; "b"; "main"; "u7" ] ]

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
                    (Reply.Ok csv)

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
            // --fields, --output csv) — subset matching can't prove the paging flags.
            let csv = teaCsv [ prHeader; [ "7"; "mine"; "open"; "b"; "main"; "u" ] ]
            let tea, args = capturing (Reply.Ok csv)

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
                      "csv" ]
                    args
            | Error e -> Assert.Fail $"pr view failed: {e}"
        }

    [<Test>]
    member _.PrViewPagesToFindLaterPr() : Task =
        task {
            // A PR past the first page (the Gitea server caps a page at ~50) must still be
            // found — `PrView` pages through with `--page N`, not a single large `--limit`.
            let page1 = teaCsv [ prHeader; [ "1"; "a"; "open"; "x"; "main"; "u1" ] ]
            let page2 = teaCsv [ prHeader; [ "55"; "target"; "open"; "y"; "main"; "u55" ] ]

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
            // A header-only page (past the last PR) → a genuine absence error, not a hang or crash.
            let tea =
                scripted [ "pr"; "list"; "--state"; "all" ] (Reply.Ok(teaCsv [ prHeader ]))

            let! r = tea.PrView(".", 99UL)
            Assert.That(Result.isError r, Is.True, "a PR number not in the listing is an error")
        }

    [<Test>]
    member _.ListParsersTreatEmptyOutputAsEmptyList() : Task =
        task {
            // Some `tea` builds print NOTHING (not even a header) for an empty result — an empty
            // repo is a normal state, not a parse error.
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
            // `--style` MUST precede the positional index — confirmed live against real tea
            // 0.9.2 (K-061): its argv parser (urfave/cli v2 over Go's `flag` package) stops
            // recognising flags after the first bare positional, so `<index> --style <style>`
            // leaves `--style`/`<style>` as extra positionals and tea fails with `Error: Must
            // specify a PR index` — the exact live-CI failure this test now guards against.
            let tea, args = capturing (Reply.Ok "")

            match! tea.PrMerge(".", 7UL, MergeStrategy.Squash) with
            | Ok() -> assertArgs [ "pr"; "merge"; "--style"; "squash"; "7" ] args
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
    member _.PrApproveBuildsArgsWithAndWithoutComment() : Task =
        task {
            // `tea pr approve <index>` — number-only when there is no comment.
            let bare, bareArgs = capturing (Reply.Ok "")

            match! bare.PrApprove(".", 7UL, None) with
            | Ok() -> assertArgs [ "pr"; "approve"; "7" ] bareArgs
            | Error e -> Assert.Fail $"pr approve failed: {e}"

            // …and the optional comment lands as a bare positional after the index.
            let commented, commentedArgs = capturing (Reply.Ok "")

            match! commented.PrApprove(".", 7UL, Some "lgtm") with
            | Ok() -> assertArgs [ "pr"; "approve"; "7"; "lgtm" ] commentedArgs
            | Error e -> Assert.Fail $"pr approve with comment failed: {e}"
        }

    [<Test>]
    member _.PrRejectBuildsArgs() : Task =
        task {
            // `tea pr reject <index> <reason>` — the reason is a bare positional after the index.
            let tea, args = capturing (Reply.Ok "")

            match! tea.PrReject(".", 7UL, "please fix") with
            | Ok() -> assertArgs [ "pr"; "reject"; "7"; "please fix" ] args
            | Error e -> Assert.Fail $"pr reject failed: {e}"
        }

    [<Test>]
    member _.PrEditRefusesStructurallyBecauseTeaHasNoPrEditCommand() : Task =
        task {
            // tea 0.9.2 has no `pr edit` command at all — an unrecognised `pr edit` silently
            // falls through to a plain `pr list` instead of editing (K-063; confirmed against the
            // real tea 0.9.2 binary and its Go source). `Gitea.PrEdit` therefore refuses BEFORE
            // any spawn for every input. `permissive` answers any spawn with `Ok ""`, so an
            // `Error` here proves the refusal happened without ever reaching the runner.
            let tea = permissive ()

            match! tea.PrEdit(".", 7UL, PrEdit.Create()) with
            | Error _ -> ()
            | Ok() -> Assert.Fail "PrEdit must be refused (tea has no `pr edit` command), even with nothing to change"

            match! tea.PrEdit(".", 7UL, PrEdit.Create().WithTitle("New title").WithBody("New body")) with
            | Error e -> Assert.That($"%A{e}", Does.Contain "pr edit")
            | Ok() ->
                Assert.Fail
                    "PrEdit must be refused before spawning even when title/body are set (tea has no `pr edit` command)"
        }

    [<Test>]
    member _.IssueViewPagesAndFilters() : Task =
        task {
            // tea 0.9.2's bare-index view renders Markdown and ignores `--output`, so `IssueView`
            // synthesizes the read by paging `issues list --state all … --output csv`.
            let csv = teaCsv [ issueHeader; [ "3"; "Docs"; "open"; "write them"; "u" ] ]

            let tea =
                scripted
                    [ "issues"
                      "list"
                      "--state"
                      "all"
                      "--page"
                      "1"
                      "--fields"
                      "index,title,state,body,url" ]
                    (Reply.Ok csv)

            match! tea.IssueView(".", 3UL) with
            | Ok issue ->
                Assert.That(issue.Number, Is.EqualTo 3UL)
                Assert.That(issue.Body, Is.EqualTo "write them")
            | Error e -> Assert.Fail $"issue view failed: {e}"
        }

    [<Test>]
    member _.IssueViewBuildsExactPagedArgv() : Task =
        task {
            // Lock the synthesized issue-view page-1 argv exactly (mirrors PrView).
            let csv = teaCsv [ issueHeader; [ "3"; "Docs"; "open"; "write them"; "u" ] ]
            let tea, args = capturing (Reply.Ok csv)

            match! tea.IssueView(".", 3UL) with
            | Ok issue ->
                Assert.That(issue.Number, Is.EqualTo 3UL)

                assertArgs
                    [ "issues"
                      "list"
                      "--state"
                      "all"
                      "--limit"
                      "50"
                      "--page"
                      "1"
                      "--fields"
                      "index,title,state,body,url"
                      "--output"
                      "csv" ]
                    args
            | Error e -> Assert.Fail $"issue view failed: {e}"
        }

    [<Test>]
    member _.IssueViewPagesToFindLaterIssue() : Task =
        task {
            let page1 = teaCsv [ issueHeader; [ "1"; "a"; "open"; "b"; "u1" ] ]
            let page2 = teaCsv [ issueHeader; [ "55"; "target"; "open"; "b"; "u55" ] ]

            let runner =
                ScriptedRunner()
                    .On([ "issues"; "list"; "--page"; "1" ], Reply.Ok page1)
                    .On([ "issues"; "list"; "--page"; "2" ], Reply.Ok page2)

            let tea = Gitea.WithRunner runner

            match! tea.IssueView(".", 55UL) with
            | Ok issue -> Assert.That(issue.Title, Is.EqualTo "target", "found #55 on page 2")
            | Error e -> Assert.Fail $"issue view failed: {e}"
        }

    [<Test>]
    member _.IssueViewMissingNumberIsError() : Task =
        task {
            let tea =
                scripted [ "issues"; "list"; "--state"; "all" ] (Reply.Ok(teaCsv [ issueHeader ]))

            let! r = tea.IssueView(".", 99UL)
            Assert.That(Result.isError r, Is.True, "an issue number not in the listing is an error")
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
                      "csv" ]
                    (Reply.Ok(teaCsv [ issueHeader; [ "12"; "Bug"; "open"; "b"; "u" ] ]))

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
    member _.IssueCloseAndCommentBuildArgs() : Task =
        task {
            let close, closeArgs = capturing (Reply.Ok "")

            match! close.IssueClose(".", 4UL) with
            | Ok() -> assertArgs [ "issues"; "close"; "4" ] closeArgs
            | Error e -> Assert.Fail $"issue close failed: {e}"

            // Issues and PRs share `tea comment <index> <body>` (a shared index space).
            let comment, commentArgs = capturing (Reply.Ok "commented\n")

            match! comment.IssueComment(".", 7UL, "nice work") with
            | Ok _ -> assertArgs [ "comment"; "7"; "nice work" ] commentArgs
            | Error e -> Assert.Fail $"issue comment failed: {e}"
        }

    [<Test>]
    member _.IssueReopenIsUnsupportedBeforeSpawning() : Task =
        task {
            let tea = Gitea.WithRunner(ScriptedRunner())

            match! tea.IssueReopen(".", 4UL) with
            | Error e -> Assert.That(e.Message, Does.Contain "issues reopen")
            | Ok() -> Assert.Fail "tea 0.9.2 must refuse issue reopen before spawning"
        }

    [<Test>]
    member _.ReleaseListRequestsLimit() : Task =
        task {
            let tea =
                scripted
                    [ "releases"; "list"; "--limit"; "100"; "--output"; "csv" ]
                    (Reply.Ok(teaCsv [ releaseHeader; [ "0.1"; "First"; ""; "released"; "" ] ]))

            match! tea.ReleaseList "." with
            | Ok [ rel ] -> Assert.That(rel.Tag, Is.EqualTo "0.1")
            | Ok xs -> Assert.Fail $"expected one release, got {xs.Length}"
            | Error e -> Assert.Fail $"release list failed: {e}"
        }

    [<Test>]
    member _.ReleaseCreateBuildsAllFlagsWithExactValues() : Task =
        task {
            let tea, args = capturing (Reply.Ok "Created release\n")

            let spec =
                ReleaseCreate.Create("v1.0.0").WithTitle("1.0.0").WithNotes("the notes").WithDraft().WithPrerelease()

            match! tea.ReleaseCreate(".", spec) with
            | Ok out ->
                Assert.That(out, Is.EqualTo "Created release")

                assertArgs
                    [ "release"
                      "create"
                      "--tag"
                      "v1.0.0"
                      "--title"
                      "1.0.0"
                      "--note"
                      "the notes"
                      "--draft"
                      "--prerelease" ]
                    args
            | Error e -> Assert.Fail $"release create failed: {e}"
        }

    [<Test>]
    member _.ReleaseCreateOmitsTitleNotesAndFlagsWhenNone() : Task =
        task {
            // A tag-only release emits only `--tag`; title/note and the draft/prerelease flags
            // are absent when unset.
            let tea, args = capturing (Reply.Ok "ok\n")

            match! tea.ReleaseCreate(".", ReleaseCreate.Create "v2") with
            | Ok _ -> assertArgs [ "release"; "create"; "--tag"; "v2" ] args
            | Error e -> Assert.Fail $"release create failed: {e}"
        }

    [<Test>]
    member _.ReleaseDeleteIsUnsupportedBeforeSpawning() : Task =
        task {
            let tea = Gitea.WithRunner(ScriptedRunner())

            match! tea.ReleaseDelete(".", "v1") with
            | Error e -> Assert.That(e.Message, Does.Contain "release delete")
            | Ok() -> Assert.Fail "tea 0.9.2 must refuse release delete before spawning"
        }

// ---------------------------------------------------------------------------
// K-049 regression barrier: the fixed operations must drive `--output csv`, never `json`
// ---------------------------------------------------------------------------

[<TestFixture>]
type OutputFormatContractTests() =

    /// The value following `--output` in a captured argv (`None` if the flag is absent).
    let outputValue (args: ResizeArray<string>) : string option =
        let got = List.ofSeq args

        got
        |> List.tryFindIndex (fun a -> a = "--output")
        |> Option.bind (fun i -> List.tryItem (i + 1) got)

    [<Test>]
    member _.EveryListingOperationRequestsCsvNeverJson() : Task =
        task {
            // A live-CLI contract pinned hermetically: tea 0.9.2 rejects `--output json` on these
            // commands (K-049). If any operation is ever changed back to `json`, this fails.
            let prTea, prArgs = capturing (Reply.Ok(teaCsv [ prHeader ]))
            let! _ = prTea.PrList "."

            let issueTea, issueArgs = capturing (Reply.Ok(teaCsv [ issueHeader ]))
            let! _ = issueTea.IssueList "."

            let relTea, relArgs = capturing (Reply.Ok(teaCsv [ releaseHeader ]))
            let! _ = relTea.ReleaseList "."

            let prViewTea, prViewArgs = capturing (Reply.Ok(teaCsv [ prHeader ]))
            let! _ = prViewTea.PrView(".", 1UL)

            let issueViewTea, issueViewArgs = capturing (Reply.Ok(teaCsv [ issueHeader ]))
            let! _ = issueViewTea.IssueView(".", 1UL)

            let authTea, authArgs = capturing (Reply.Ok(teaCsv [ loginHeader ]))
            let! _ = authTea.AuthStatus()

            for name, args in
                [ "pr list", prArgs
                  "issues list", issueArgs
                  "releases list", relArgs
                  "pr view", prViewArgs
                  "issue view", issueViewArgs
                  "login list", authArgs ] do
                Assert.That(outputValue args, Is.EqualTo(Some "csv"), $"{name} must request --output csv")

                Assert.That(
                    List.contains "json" (List.ofSeq args),
                    Is.False,
                    $"{name} must never send a `json` token (K-049)"
                )
        }

    [<Test>]
    member _.ReintroducingJsonOutputSurfacesAsErrorNotEmptyList() : Task =
        task {
            // If the `--output json` path is ever restored, the real tea replies with its
            // `unknown output type` diagnostic at exit 0 — the operation must surface an error,
            // never read it as a successful empty listing.
            let tea = scripted [ "pr"; "list" ] (Reply.Ok unknownOutputDiagnostic)
            let! r = tea.PrList "."
            Assert.That(Result.isError r, Is.True, "the `unknown output type` diagnostic must be an error, not empty")
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
                scripted
                    [ "login"; "list"; "--output"; "csv" ]
                    (Reply.Ok(teaCsv [ loginHeader; [ "gitea"; "https://gitea"; ""; "vcs"; "*" ] ]))

            match! some.AuthStatus() with
            | Ok v -> Assert.That(v, Is.True)
            | Error e -> Assert.Fail $"auth status (some) failed: {e}"

            let empty =
                scripted [ "login"; "list"; "--output"; "csv" ] (Reply.Ok(teaCsv [ loginHeader ]))

            match! empty.AuthStatus() with
            | Ok v -> Assert.That(v, Is.False, "a header-only logins table means not logged in")
            | Error e -> Assert.Fail $"auth status (empty) failed: {e}"
        }

    [<Test>]
    member _.AuthStatusEmptyOutputIsFalse() : Task =
        task {
            // Some tea builds print nothing (not even a header) when none are configured.
            let tea = scripted [ "login"; "list"; "--output"; "csv" ] (Reply.Ok "")

            match! tea.AuthStatus() with
            | Ok v -> Assert.That(v, Is.False)
            | Error e -> Assert.Fail $"auth status (empty output) failed: {e}"
        }

    [<Test>]
    member _.AuthStatusNonZeroExitIsFalse() : Task =
        task {
            // A non-zero exit (e.g. no config file yet) reads as "not logged in", not an error.
            let tea =
                scripted [ "login"; "list"; "--output"; "csv" ] (Reply.Fail(1, "no config"))

            match! tea.AuthStatus() with
            | Ok v -> Assert.That(v, Is.False)
            | Error e -> Assert.Fail $"auth status (non-zero) failed: {e}"
        }

    [<Test>]
    member _.AuthStatusErrorsOnAbnormalTermination() : Task =
        task {
            // A signal kill has no exit code — it must surface as an error, not "false".
            let tea = scripted [ "login"; "list"; "--output"; "csv" ] (Reply.Signalled 9)

            let! r = tea.AuthStatus()
            Assert.That(Result.isError r, Is.True, "an abnormal termination must error, not read false")
        }

    [<Test>]
    member _.AuthStatusDiagnosticOutputIsError() : Task =
        task {
            // tea printing `unknown output type` at exit 0 must surface as an error, not a silent
            // "not logged in" false (K-049).
            let tea =
                scripted [ "login"; "list"; "--output"; "csv" ] (Reply.Ok unknownOutputDiagnostic)

            let! r = tea.AuthStatus()
            Assert.That(Result.isError r, Is.True, "an `unknown output type` diagnostic must error, not read false")
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
            let! c = isErr (refuse.IssueComment(".", 7UL, "-evil"))
            let! d = isErr (refuse.IssueComment(".", 7UL, ""))
            Assert.That(a, Is.True, "a dash-leading PR comment body must be refused")
            Assert.That(b, Is.True, "an empty PR comment body must be refused")
            Assert.That(c, Is.True, "a dash-leading issue comment body must be refused")
            Assert.That(d, Is.True, "an empty issue comment body must be refused")

            // …and a legitimate body still passes, as a bare positional after the index.
            let tea, args = capturing (Reply.Ok "commented\n")

            match! tea.PrComment(".", 7UL, "nice work") with
            | Ok _ -> assertArgs [ "comment"; "7"; "nice work" ] args
            | Error e -> Assert.Fail $"pr comment failed: {e}"
        }

    [<Test>]
    member _.ReviewReasonAndApproveCommentAreRejectedIfFlagLike() : Task =
        task {
            // `tea pr reject`'s reason and `tea pr approve`'s optional comment are bare
            // positionals, so a dash-leading or empty value must be refused before spawning.
            let refuse = permissive ()

            let isErr (t: Task<Result<'T, ProcessError>>) =
                task {
                    let! r = t
                    return Result.isError r
                }

            let! a = isErr (refuse.PrReject(".", 7UL, "-evil"))
            let! b = isErr (refuse.PrReject(".", 7UL, ""))
            let! c = isErr (refuse.PrApprove(".", 7UL, Some "-evil"))
            let! d = isErr (refuse.PrApprove(".", 7UL, Some ""))
            Assert.That(a, Is.True, "a dash-leading reject reason must be refused")
            Assert.That(b, Is.True, "an empty reject reason must be refused")
            Assert.That(c, Is.True, "a dash-leading approve comment must be refused")
            Assert.That(d, Is.True, "an empty approve comment must be refused")

            // A None approve comment has no positional to guard and must pass through.
            let tea, args = capturing (Reply.Ok "")

            match! tea.PrApprove(".", 7UL, None) with
            | Ok() -> assertArgs [ "pr"; "approve"; "7" ] args
            | Error e -> Assert.Fail $"pr approve without a comment must pass: {e}"
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

[<TestFixture>]
type AtViewTests() =

    // Records the full Command (argv + working directory), for asserting the `at(dir)` view's
    // cwd binding — the plain `capturing` above only keeps argv.
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
    member _.GiteaAtRawRunBindsDir() : Task =
        task {
            // The raw `Run`/`RunRaw` hatches on the bound view run in the bound `dir`
            // (WorkingDirectory = Some dir), like the modelled methods — not the process cwd.
            let captured, runner = capturingCmd (Reply.Ok "ok\n")
            let tea = Gitea.WithRunner runner

            let! _ = tea.At("/bound/dir").Run [ "login"; "list" ]

            match captured.Value with
            | Some cmd ->
                Assert.That(cmd.WorkingDirectory, Is.EqualTo(Some "/bound/dir"), "the raw Run hatch binds dir")
                Assert.That(String.concat " " cmd.Arguments, Is.EqualTo "login list")
            | None -> Assert.Fail "no command captured for Run"

            let capturedRaw, runnerRaw = capturingCmd (Reply.Ok "")
            let teaRaw = Gitea.WithRunner runnerRaw

            let! _ = teaRaw.At("/bound/dir").RunRaw [ "version" ]

            match capturedRaw.Value with
            | Some cmd ->
                Assert.That(cmd.WorkingDirectory, Is.EqualTo(Some "/bound/dir"), "the raw RunRaw hatch binds dir")
            | None -> Assert.Fail "no command captured for RunRaw"
        }

    [<Test>]
    member _.GiteaUnboundRawRunStaysProcessCwd() : Task =
        task {
            // The unbound client's raw `Run` still runs in the process cwd (WorkingDirectory =
            // None) — the `dir`-bound form lives only on the `at(dir)` view / `Run(dir, …)`.
            let captured, runner = capturingCmd (Reply.Ok "ok\n")
            let tea = Gitea.WithRunner runner

            let! _ = tea.Run [ "login"; "list" ]

            match captured.Value with
            | Some cmd -> Assert.That(cmd.WorkingDirectory, Is.EqualTo None, "the unbound raw Run is NOT bound to dir")
            | None -> Assert.Fail "no command captured"
        }

[<TestFixture>]
type ObserverWiringTests() =

    [<Test>]
    member _.WithObserverThreadsThroughTheGiteaClient() : Task =
        task {
            let events = ResizeArray<CommandEvent>()

            let observer =
                { new ICommandObserver with
                    member _.OnStarted(ev) = events.Add ev
                    member _.OnFinished(_, _, _) = () }

            let tea =
                Gitea.WithRunner(ScriptedRunner().Fallback(Reply.Ok "tea version 0.9.0")).WithObserver observer

            match! tea.Run [ "--version" ] with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"{e}"

            Assert.That(events.Count, Is.EqualTo 1, "the observer is threaded through the Gitea client")
            Assert.That(events[0].Program, Is.EqualTo "tea")
        }
