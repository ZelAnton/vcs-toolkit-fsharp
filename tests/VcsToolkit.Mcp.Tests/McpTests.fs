module VcsToolkit.Mcp.Tests

open System
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.Core
open VcsToolkit.Forge
open VcsToolkit.Git
open VcsToolkit.GitHub
open VcsToolkit.Mcp

/// A git-backed server over a scripted runner — no real binary, no forge — with an
/// explicit output budget (`None` = unlimited).
let private gitServerWithBudget (runner: ScriptedRunner) (writes: WriteGate) (outputBudget: int option) =
    new VcsMcpServer(Repo.FromGit("/repo", "/repo", Git.WithRunner runner), Option.None, writes, outputBudget)

/// A git-backed server over a scripted runner — no real binary, no forge, no output budget.
let private gitServer (runner: ScriptedRunner) (writes: WriteGate) =
    gitServerWithBudget runner writes Option.None

/// A git-backed server with a GitHub forge, both wired to the same scripted runner (no real
/// `git`/`gh` binaries).
let private gitServerWithForge (runner: ScriptedRunner) (writes: WriteGate) =
    new VcsMcpServer(
        Repo.FromGit("/repo", "/repo", Git.WithRunner runner),
        Some(Forge.FromGitHub("/repo", GitHub.WithRunner runner)),
        writes,
        Option.None
    )

// ---------------------------------------------------------------------------
// WriteGate
// ---------------------------------------------------------------------------

[<TestFixture>]
type WriteGateTests() =

    [<Test>]
    member _.AllowsReflectsThePolicy() =
        Assert.That(WriteGate.All.Allows "repo_commit", Is.True)
        Assert.That(WriteGate.None.Allows "repo_commit", Is.False)
        let s = WriteGate.Set(Set.ofList [ "repo_commit" ])
        Assert.That(s.Allows "repo_commit", Is.True)
        Assert.That(s.Allows "repo_push", Is.False, "a name not in the set is refused")

    [<Test>]
    member _.WriteToolsCoversTheGatedTools() =
        Assert.That(List.length WriteTools.all, Is.EqualTo 15)
        Assert.That(WriteTools.asSet.Contains "repo_commit", Is.True)
        Assert.That(WriteTools.asSet.Contains "forge_pr_checkout", Is.True, "the local-checkout tool is write-gated")
        Assert.That(WriteTools.asSet.Contains "repo_status", Is.False, "a read tool is not a write tool")

// ---------------------------------------------------------------------------
// Argument parsing
// ---------------------------------------------------------------------------

[<TestFixture>]
type ArgsTests() =

    let ok (argv: string list) =
        match Args.parse argv with
        | Ok(Some a) -> a
        | Ok Option.None -> failwith "expected Some args, got help"
        | Error e -> failwith $"expected Ok, got error: {e}"

    let err (argv: string list) =
        match Args.parse argv with
        | Error e -> e
        | _ -> failwith $"expected a parse error for {argv}"

    [<Test>]
    member _.DefaultsWithNoArgs() =
        let a = ok []
        Assert.That(a.Repo, Is.EqualTo ".")
        Assert.That(a.Forge, Is.EqualTo Option.None)
        Assert.That(a.Writes, Is.EqualTo WriteGate.None)
        Assert.That(a.Timeout, Is.EqualTo(Some(TimeSpan.FromSeconds 120.0)))
        Assert.That(a.OutputBudget, Is.EqualTo(Some Args.defaultOutputBudgetBytes))

    [<Test>]
    member _.AllowToolsBuildsASetGateAndIsRepeatable() =
        let a = ok [ "--allow-tools"; "repo_commit, forge_pr_create" ]

        match a.Writes with
        | WriteGate.Set tools ->
            Assert.That(tools.Contains "repo_commit", Is.True)
            Assert.That(tools.Contains "forge_pr_create", Is.True)
            Assert.That(tools.Count, Is.EqualTo 2)
        | other -> Assert.Fail $"expected Set gate, got {other}"

        let b = ok [ "--allow-tools"; "repo_push"; "--allow-tools"; "repo_fetch" ]

        match b.Writes with
        | WriteGate.Set tools -> Assert.That(tools.Count, Is.EqualTo 2, "occurrences accumulate")
        | other -> Assert.Fail $"expected Set gate, got {other}"

        Assert.That((err [ "--allow-tools" ]), Does.Contain "needs")
        Assert.That((err [ "--allow-tools"; " , " ]), Does.Contain "names no tools")

    [<Test>]
    member _.AllowToolsRejectsUnknownName() =
        let e = err [ "--allow-tools"; "repo_comit" ] // typo
        Assert.That(e, Does.Contain "unknown tool")
        Assert.That(e, Does.Contain "repo_comit", "names the offender")
        // A read-tool name is also not a valid write allowlist entry.
        Assert.That((err [ "--allow-tools"; "repo_commit,repo_status" ]), Does.Contain "repo_status")

    [<Test>]
    member _.AllowWriteWinsOverAllowTools() =
        let a = ok [ "--allow-tools"; "repo_commit"; "--allow-write" ]
        Assert.That(a.Writes, Is.EqualTo WriteGate.All)

    [<Test>]
    member _.HelpShortCircuits() =
        let isHelp argv =
            match Args.parse argv with
            | Ok Option.None -> true
            | _ -> false

        Assert.That(isHelp [ "--help" ], Is.True)
        Assert.That(isHelp [ "-h" ], Is.True)

    [<Test>]
    member _.UnknownFlagAndMissingValuesError() =
        Assert.That((err [ "--bogus" ]), Does.Contain "unknown argument")
        Assert.That(Result.isError (Args.parse [ "--repo" ]), Is.True)
        Assert.That(Result.isError (Args.parse [ "--forge" ]), Is.True)
        Assert.That(Result.isError (Args.parse [ "--timeout" ]), Is.True)

    [<Test>]
    member _.TimeoutParsing() =
        Assert.That((ok [ "--timeout"; "0" ]).Timeout, Is.EqualTo Option.None, "0 disables")
        Assert.That((ok [ "--timeout"; "45" ]).Timeout, Is.EqualTo(Some(TimeSpan.FromSeconds 45.0)))
        Assert.That((err [ "--timeout"; "junk" ]), Does.Contain "invalid --timeout")

        Assert.That(
            Result.isError (Args.parse [ "--timeout"; "-5" ]),
            Is.True,
            "a negative isn't a valid whole-second count"
        )

    [<Test>]
    member _.OutputBudgetParsing() =
        Assert.That((ok [ "--output-budget"; "0" ]).OutputBudget, Is.EqualTo Option.None, "0 disables")
        Assert.That((ok [ "--output-budget"; "500" ]).OutputBudget, Is.EqualTo(Some 500))
        Assert.That((err [ "--output-budget"; "junk" ]), Does.Contain "invalid --output-budget")

        Assert.That(
            Result.isError (Args.parse [ "--output-budget"; "-5" ]),
            Is.True,
            "a negative isn't a valid whole-byte count"
        )

        Assert.That(Result.isError (Args.parse [ "--output-budget" ]), Is.True)

    [<Test>]
    member _.ForgeParsing() =
        Assert.That((ok [ "--forge"; "github" ]).Forge, Is.EqualTo(Some ForgeKind.GitHub))
        Assert.That((ok [ "--forge"; "gitlab" ]).Forge, Is.EqualTo(Some ForgeKind.GitLab))
        Assert.That((ok [ "--forge"; "gitea" ]).Forge, Is.EqualTo(Some ForgeKind.Gitea))
        Assert.That((err [ "--forge"; "bitbucket" ]), Does.Contain "unknown forge")

    [<Test>]
    member _.CombinedFlags() =
        let a = ok [ "--repo"; "X"; "--forge"; "gitea"; "--allow-write"; "--timeout"; "7" ]
        Assert.That(a.Repo, Is.EqualTo "X")
        Assert.That(a.Forge, Is.EqualTo(Some ForgeKind.Gitea))
        Assert.That(a.Writes, Is.EqualTo WriteGate.All)
        Assert.That(a.Timeout, Is.EqualTo(Some(TimeSpan.FromSeconds 7.0)))

// ---------------------------------------------------------------------------
// Tool dispatch, gating, and error mapping (over a scripted repo)
// ---------------------------------------------------------------------------

[<TestFixture>]
type ToolTests() =

    [<Test>]
    member _.ReadToolReturnsDtoJson() : Task =
        task {
            let server =
                gitServer (ScriptedRunner().On([ "symbolic-ref" ], Reply.Ok "main\n")) WriteGate.None

            match! server.RepoCurrentBranch() with
            | Ok json -> Assert.That(json, Does.Contain "main")
            | Error e -> Assert.Fail $"tool failed: {e.Message}"
        }

    [<Test>]
    member _.ReadToolWorksInReadOnlyMode() : Task =
        task {
            let server =
                gitServer (ScriptedRunner().On([ "status" ], Reply.Ok " M a.rs ")) WriteGate.None

            match! server.RepoStatus() with
            | Ok json -> Assert.That(json, Does.Contain "a.rs")
            | Error e -> Assert.Fail $"status failed: {e.Message}"
        }

    [<Test>]
    member _.RepoLogReturnsCommitJson() : Task =
        task {
            // repo_log is a read tool (no write gate) that surfaces the facade's unified `Commit`
            // DTO as JSON — author/date included on git.
            let us = string (char 0x1f)
            let nul = string (char 0)

            let row = $"deadbeef{us}dead{us}Jane{us}2026-01-02T00:00:00+00:00{us}Fix bug{nul}"

            let server =
                gitServer (ScriptedRunner().On([ "log"; "HEAD" ], Reply.Ok row)) WriteGate.None

            match! server.RepoLog("HEAD", 10UL) with
            | Ok json ->
                Assert.That(json, Does.Contain "deadbeef")
                Assert.That(json, Does.Contain "Fix bug")
                Assert.That(json, Does.Contain "Jane")
            | Error e -> Assert.Fail $"repo_log failed: {e.Message}"
        }

    [<Test>]
    member _.MutationIsGatedWithoutAllowWrite() : Task =
        task {
            // The scripted runner has NO checkout rule; if the gate failed and the tool
            // spawned, the error would differ from the gate's --allow-write message.
            let server = gitServer (ScriptedRunner()) WriteGate.None

            match! server.RepoCheckout "feat" with
            | Error e -> Assert.That(e.Message, Does.Contain "allow-write")
            | Ok _ -> Assert.Fail "a gated write must be refused"
        }

    [<Test>]
    member _.MutationReachesRunnerWithAllowWrite() : Task =
        task {
            let server =
                gitServer (ScriptedRunner().On([ "checkout" ], Reply.Ok "")) WriteGate.All

            match! server.RepoCheckout "feat" with
            | Ok json -> Assert.That(json, Does.Contain "feat")
            | Error e -> Assert.Fail $"checkout failed: {e.Message}"
        }

    [<Test>]
    member _.TryMergeIsWriteGated() : Task =
        task {
            // repo_try_merge spawns a real (rolled-back) trial merge, so it is gated despite
            // reading no state — it must NOT be callable in the default read-only mode.
            let server = gitServer (ScriptedRunner()) WriteGate.None

            match! server.RepoTryMerge "feat" with
            | Error e -> Assert.That(e.Message, Does.Contain "allow-write")
            | Ok _ -> Assert.Fail "try_merge must be gated in read-only mode"
        }

    [<Test>]
    member _.AllowToolsGatesPerTool() : Task =
        task {
            // Only repo_checkout is enabled — repo_push stays gated.
            let server =
                gitServer
                    (ScriptedRunner().On([ "checkout" ], Reply.Ok ""))
                    (WriteGate.Set(Set.ofList [ "repo_checkout" ]))

            match! server.RepoCheckout "feat" with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"repo_checkout should be allowed: {e.Message}"

            match! server.RepoPush "feat" with
            | Error e -> Assert.That(e.Message, Does.Contain "allow-write", "repo_push is not in the allowlist")
            | Ok _ -> Assert.Fail "repo_push must stay gated"
        }

    [<Test>]
    member _.ForgeToolErrorsWhenNoForge() : Task =
        task {
            let server = gitServer (ScriptedRunner()) WriteGate.None

            match! server.ForgePrList() with
            | Error e -> Assert.That(e.Message, Does.Contain "no forge")
            | Ok _ -> Assert.Fail "a forge tool must error when no forge is configured"
        }

    [<Test>]
    member _.ForgePrCheckoutIsWriteGated() : Task =
        task {
            // forge_pr_checkout mutates the LOCAL working copy (switches the checked-out
            // branch), so it must be write-gated: refused in the default read-only mode. The
            // gate is checked before the forge is resolved, so a forge-less server suffices.
            let server = gitServer (ScriptedRunner()) WriteGate.None

            match! server.ForgePrCheckout 1UL with
            | Error e -> Assert.That(e.Message, Does.Contain "allow-write")
            | Ok _ -> Assert.Fail "forge_pr_checkout must be gated in read-only mode"
        }

    [<Test>]
    member _.ForgePrCheckoutClearsGateAsAWriteTool() : Task =
        task {
            // Enabled via the per-tool allowlist as a write op, it clears the write gate and
            // then errors only because no forge is configured — proving it passes WriteGate as
            // a write and is forge-bound (not that the gate silently dropped it).
            let server =
                gitServer (ScriptedRunner()) (WriteGate.Set(Set.ofList [ "forge_pr_checkout" ]))

            match! server.ForgePrCheckout 1UL with
            | Error e ->
                Assert.That(e.Message, Does.Contain "no forge", "gate cleared → falls through to forge resolution")
            | Ok _ -> Assert.Fail "forge_pr_checkout must require a configured forge"
        }

    [<Test>]
    member _.ForgePrCheckoutAndForgePrMergeSerializeOnTheRepoWriteLock() : Task =
        task {
            // Both forge_pr_checkout and forge_pr_merge mutate the LOCAL working copy, so they
            // must hold the same per-repo write lock as repo_* mutations rather than just
            // checking the write gate. Prove it end-to-end: block the scripted "gh pr checkout"
            // call until released (simulating a slow local mutation holding the lock), then show
            // a concurrent forge_pr_merge is stuck behind it — and only completes once the lock
            // is released.
            use checkoutStarted = new SemaphoreSlim(0)
            use releaseCheckout = new SemaphoreSlim(0)

            let isCheckout (command: Command) =
                command.Program :: List.ofSeq command.Arguments |> List.contains "checkout"

            let runner =
                ScriptedRunner()
                    // forge_pr_merge is version-gated (`gh --version`) before it dispatches.
                    .On([ "--version" ], Reply.Ok "gh version 2.40.0\n")
                    .When(
                        (fun (command: Command) ->
                            if isCheckout command then
                                checkoutStarted.Release() |> ignore
                                releaseCheckout.Wait(TimeSpan.FromSeconds 5.0) |> ignore
                                true
                            else
                                false),
                        Reply.Ok ""
                    )
                    .On([ "merge" ], Reply.Ok "")

            let server = gitServerWithForge runner WriteGate.All

            // Run on a background thread: the scripted reply above blocks its calling thread
            // synchronously, so it must not be the test's own thread.
            let checkoutTask =
                Task.Run<Result<string, McpError>>(fun () -> server.ForgePrCheckout 1UL)

            Assert.That(checkoutStarted.Wait(TimeSpan.FromSeconds 5.0), Is.True, "checkout must have started")

            let mergeTask =
                Task.Run<Result<string, McpError>>(fun () -> server.ForgePrMerge(2UL, "merge", false, false))

            Assert.That(
                (mergeTask :> Task).Wait(TimeSpan.FromMilliseconds 300.0),
                Is.False,
                "forge_pr_merge must block on the repo write lock held by forge_pr_checkout"
            )

            releaseCheckout.Release() |> ignore

            match! checkoutTask with
            | Ok json -> Assert.That(json, Does.Contain "checkedOut")
            | Error e -> Assert.Fail $"forge_pr_checkout failed: {e.Message}"

            match! mergeTask with
            | Ok json -> Assert.That(json, Does.Contain "merged")
            | Error e -> Assert.Fail $"forge_pr_merge failed: {e.Message}"
        }

    [<Test>]
    member _.RepoInfoReportsBackendAndNoForge() : Task =
        task {
            let server = gitServer (ScriptedRunner()) WriteGate.None

            match! server.RepoInfo() with
            | Ok json ->
                Assert.That(json, Does.Contain "\"backend\": \"git\"")
                Assert.That(json, Does.Contain "\"forge\": null", "no forge configured")
            | Error e -> Assert.Fail $"repo_info failed: {e.Message}"
        }

    [<Test>]
    member _.ForgeIssueCreateRejectsDashLeadingTitleWithoutSpawning() : Task =
        task {
            // The guard fires before any spawn, so a fallback that would fail loudly is never
            // reached — proving the refusal precedes the forge call.
            let runner =
                ScriptedRunner().Fallback(Reply.Fail(1, "must not spawn — refusal must precede it"))

            let server = gitServerWithForge runner WriteGate.All

            match! server.ForgeIssueCreate("-title", "body") with
            | Error(McpError.InvalidParams _) -> ()
            | Error e -> Assert.Fail $"expected invalid params, got: {e.Message}"
            | Ok _ -> Assert.Fail "a dash-leading title must be refused"
        }

    [<Test>]
    member _.ForgeIssueCreateRejectsDashLeadingBodyWithoutSpawning() : Task =
        task {
            let runner =
                ScriptedRunner().Fallback(Reply.Fail(1, "must not spawn — refusal must precede it"))

            let server = gitServerWithForge runner WriteGate.All

            match! server.ForgeIssueCreate("title", "-body") with
            | Error(McpError.InvalidParams _) -> ()
            | Error e -> Assert.Fail $"expected invalid params, got: {e.Message}"
            | Ok _ -> Assert.Fail "a dash-leading body must be refused"
        }

    [<Test>]
    member _.ForgeIssueCreateAcceptsNonDashTitleAndBody() : Task =
        task {
            let server =
                gitServerWithForge
                    (ScriptedRunner()
                        .On([ "--version" ], Reply.Ok "gh version 2.40.0\n")
                        .On([ "issue"; "create" ], Reply.Ok "https://x/1\n"))
                    WriteGate.All

            match! server.ForgeIssueCreate("fine title", "fine body") with
            | Ok json -> Assert.That(json, Does.Contain "https://x/1")
            | Error e -> Assert.Fail $"forge_issue_create failed: {e.Message}"
        }

    [<Test>]
    member _.ForgePrCreateRejectsDashLeadingTitleWithoutSpawning() : Task =
        task {
            let runner =
                ScriptedRunner().Fallback(Reply.Fail(1, "must not spawn — refusal must precede it"))

            let server = gitServerWithForge runner WriteGate.All

            match! server.ForgePrCreate("-title", "body", Option.None, Option.None) with
            | Error(McpError.InvalidParams _) -> ()
            | Error e -> Assert.Fail $"expected invalid params, got: {e.Message}"
            | Ok _ -> Assert.Fail "a dash-leading title must be refused"
        }

    [<Test>]
    member _.ForgePrCreateRejectsDashLeadingBodyWithoutSpawning() : Task =
        task {
            let runner =
                ScriptedRunner().Fallback(Reply.Fail(1, "must not spawn — refusal must precede it"))

            let server = gitServerWithForge runner WriteGate.All

            match! server.ForgePrCreate("title", "-body", Option.None, Option.None) with
            | Error(McpError.InvalidParams _) -> ()
            | Error e -> Assert.Fail $"expected invalid params, got: {e.Message}"
            | Ok _ -> Assert.Fail "a dash-leading body must be refused"
        }

    [<Test>]
    member _.ForgePrCreateAcceptsNonDashTitleAndBody() : Task =
        task {
            let server =
                gitServerWithForge
                    (ScriptedRunner()
                        .On([ "--version" ], Reply.Ok "gh version 2.40.0\n")
                        .On([ "pr"; "create" ], Reply.Ok "https://x/2\n"))
                    WriteGate.All

            match! server.ForgePrCreate("fine title", "fine body", Option.None, Option.None) with
            | Ok json -> Assert.That(json, Does.Contain "https://x/2")
            | Error e -> Assert.Fail $"forge_pr_create failed: {e.Message}"
        }

// ---------------------------------------------------------------------------
// repo_show_file output-budget truncation
// ---------------------------------------------------------------------------

[<TestFixture>]
type OutputBudgetTests() =

    [<Test>]
    member _.ContentWithinBudgetIsUntouched() : Task =
        task {
            let server =
                gitServerWithBudget
                    (ScriptedRunner().On([ "show"; "abc:file.txt" ], Reply.Ok "hello"))
                    WriteGate.None
                    (Some 100)

            match! server.RepoShowFile("abc", "file.txt") with
            | Ok json ->
                Assert.That(json, Is.EqualTo "\"hello\"")
                Assert.That(json, Does.Not.Contain "truncated")
            | Error e -> Assert.Fail $"repo_show_file failed: {e.Message}"
        }

    [<Test>]
    member _.ContentOverBudgetIsTruncatedWithMarker() : Task =
        task {
            let content = "0123456789ABCDEF" // 16 ASCII bytes

            let server =
                gitServerWithBudget
                    (ScriptedRunner().On([ "show"; "abc:file.txt" ], Reply.Ok content))
                    WriteGate.None
                    (Some 10)

            match! server.RepoShowFile("abc", "file.txt") with
            | Ok json ->
                Assert.That(json, Does.Contain "0123456789")
                Assert.That(json, Does.Not.Contain "ABCDEF", "content past the budget is dropped")
                Assert.That(json, Does.Contain "[truncated: showing 10 of 16 bytes]")
            | Error e -> Assert.Fail $"repo_show_file failed: {e.Message}"
        }

    [<Test>]
    member _.NoOrZeroBudgetDoesNotTruncate() : Task =
        task {
            let content = String.replicate 50 "0123456789" // 500 bytes

            let unbounded =
                gitServerWithBudget
                    (ScriptedRunner().On([ "show"; "abc:file.txt" ], Reply.Ok content))
                    WriteGate.None
                    Option.None

            match! unbounded.RepoShowFile("abc", "file.txt") with
            | Ok json -> Assert.That(json, Does.Not.Contain "truncated")
            | Error e -> Assert.Fail $"repo_show_file failed: {e.Message}"

            let zero =
                gitServerWithBudget
                    (ScriptedRunner().On([ "show"; "abc:file.txt" ], Reply.Ok content))
                    WriteGate.None
                    (Some 0)

            match! zero.RepoShowFile("abc", "file.txt") with
            | Ok json -> Assert.That(json, Does.Not.Contain "truncated", "--output-budget 0 disables the cap")
            | Error e -> Assert.Fail $"repo_show_file failed: {e.Message}"
        }

// ---------------------------------------------------------------------------
// JSON serialization (clean F# → JSON)
// ---------------------------------------------------------------------------

[<TestFixture>]
type JsonTests() =

    [<Test>]
    member _.OptionsRenderAsValueOrNull() =
        Assert.That(Json.ok (Some "y"), Is.EqualTo "\"y\"")
        Assert.That(Json.ok (Option.None: string option), Is.EqualTo "null")

    [<Test>]
    member _.FieldlessUnionRendersAsItsTag() =
        Assert.That(Json.ok OperationState.Clear, Is.EqualTo "\"Clear\"")
        Assert.That(Json.ok CiStatus.Passing, Is.EqualTo "\"Passing\"")

    [<Test>]
    member _.PropertyNamesAreCamelCase() =
        let json = Json.ok {| CommittedPaths = 3; SomeName = "x" |}
        Assert.That(json, Does.Contain "\"committedPaths\": 3")
        Assert.That(json, Does.Contain "\"someName\": \"x\"")

// ---------------------------------------------------------------------------
// The catalogue + callTool dispatcher (the SDK-agnostic wiring seam)
// ---------------------------------------------------------------------------

[<TestFixture>]
type CatalogTests() =

    let argsOf (json: string) =
        System.Text.Json.JsonDocument.Parse(json).RootElement

    [<Test>]
    member _.CatalogCoversEveryTool() =
        // 10 repo-read + repo_try_merge + 6 repo-write + 10 forge-read + 8 forge-write = 35.
        Assert.That(List.length Catalog.all, Is.EqualTo 35)
        // Every write-gated tool name appears in the catalogue.
        let names = Catalog.all |> List.map (fun t -> t.Name) |> Set.ofList
        Assert.That(WriteTools.all |> List.forall names.Contains, Is.True, "every write tool is catalogued")
        // repo_try_merge is gated but non-destructive/idempotent.
        let tryMerge = Catalog.all |> List.find (fun t -> t.Name = "repo_try_merge")
        Assert.That(tryMerge.ReadOnly, Is.False)
        Assert.That(tryMerge.Destructive, Is.False)
        Assert.That(tryMerge.Idempotent, Is.True)

    [<Test>]
    member _.InputSchemaListsPropertiesAndRequired() =
        let checkout = Catalog.all |> List.find (fun t -> t.Name = "repo_checkout")
        let schema = Catalog.inputSchema checkout
        Assert.That(schema, Does.Contain "\"reference\"")
        Assert.That(schema, Does.Contain "\"required\":[\"reference\"]")
        // A no-arg tool has empty properties and no required.
        let snapshot = Catalog.all |> List.find (fun t -> t.Name = "repo_snapshot")
        Assert.That(Catalog.inputSchema snapshot, Does.Contain "\"required\":[]")
        // The array param renders as an array of strings.
        let commit = Catalog.all |> List.find (fun t -> t.Name = "repo_commit")
        Assert.That(Catalog.inputSchema commit, Does.Contain "\"type\":\"array\"")

    [<Test>]
    member _.ForgePrMergeSchemaAdvertisesOptionalAutoAndDeleteBranch() =
        // The unified merge-spec's auto/delete-branch are surfaced as optional boolean params —
        // only number/strategy stay required.
        let merge = Catalog.all |> List.find (fun t -> t.Name = "forge_pr_merge")
        let schema = Catalog.inputSchema merge
        Assert.That(schema, Does.Contain "\"auto\"")
        Assert.That(schema, Does.Contain "\"delete_branch\"")
        Assert.That(schema, Does.Contain "\"required\":[\"number\",\"strategy\"]", "auto/delete_branch are optional")

    [<Test>]
    member _.CallToolDispatchesReadTool() : Task =
        task {
            let server =
                gitServer (ScriptedRunner().On([ "symbolic-ref" ], Reply.Ok "main\n")) WriteGate.None

            match! Catalog.callTool server "repo_current_branch" (argsOf "{}") with
            | Ok json -> Assert.That(json, Does.Contain "main")
            | Error e -> Assert.Fail $"dispatch failed: {e.Message}"
        }

    [<Test>]
    member _.CallToolParsesArgsAndDispatchesWrite() : Task =
        task {
            let server =
                gitServer (ScriptedRunner().On([ "checkout" ], Reply.Ok "")) WriteGate.All

            match! Catalog.callTool server "repo_checkout" (argsOf """{"reference":"feat"}""") with
            | Ok json -> Assert.That(json, Does.Contain "feat")
            | Error e -> Assert.Fail $"dispatch failed: {e.Message}"
        }

    [<Test>]
    member _.CallToolRejectsMissingArgument() : Task =
        task {
            let server = gitServer (ScriptedRunner()) WriteGate.All

            // A missing required arg is refused (as invalid-params) before the tool runs.
            match! Catalog.callTool server "repo_checkout" (argsOf "{}") with
            | Error e -> Assert.That(e.Message, Does.Contain "reference")
            | Ok _ -> Assert.Fail "a missing required argument must be refused"
        }

    [<Test>]
    member _.CallToolRejectsUnknownTool() : Task =
        task {
            let server = gitServer (ScriptedRunner()) WriteGate.None

            match! Catalog.callTool server "no_such_tool" (argsOf "{}") with
            | Error e -> Assert.That(e.Message, Does.Contain "unknown tool")
            | Ok _ -> Assert.Fail "an unknown tool must be refused"
        }

    [<Test>]
    member _.CallToolDispatchesRepoShowFile() : Task =
        task {
            let server =
                gitServer (ScriptedRunner().On([ "show"; "abc:file.txt" ], Reply.Ok "hello\n")) WriteGate.None

            match! Catalog.callTool server "repo_show_file" (argsOf """{"rev":"abc","path":"file.txt"}""") with
            | Ok json -> Assert.That(json, Does.Contain "hello")
            | Error e -> Assert.Fail $"dispatch failed: {e.Message}"
        }

    [<Test>]
    member _.CallToolRepoShowFileRejectsMissingArgument() : Task =
        task {
            let server = gitServer (ScriptedRunner()) WriteGate.None

            // Missing the required `path` argument is refused before the tool runs.
            match! Catalog.callTool server "repo_show_file" (argsOf """{"rev":"abc"}""") with
            | Error e -> Assert.That(e.Message, Does.Contain "path")
            | Ok _ -> Assert.Fail "a missing required argument must be refused"
        }

    [<Test>]
    member _.CallToolDispatchesRepoLog() : Task =
        task {
            let us = string (char 0x1f)
            let nul = string (char 0)

            let row = $"deadbeef{us}dead{us}Jane{us}2026-01-02T00:00:00+00:00{us}Fix bug{nul}"

            let server =
                gitServer (ScriptedRunner().On([ "log"; "HEAD" ], Reply.Ok row)) WriteGate.None

            match! Catalog.callTool server "repo_log" (argsOf """{"revspec_or_revset":"HEAD","max":10}""") with
            | Ok json -> Assert.That(json, Does.Contain "deadbeef")
            | Error e -> Assert.Fail $"dispatch failed: {e.Message}"
        }

    [<Test>]
    member _.CallToolRepoLogRejectsMissingArgument() : Task =
        task {
            let server = gitServer (ScriptedRunner()) WriteGate.None

            // Missing the required `max` argument is refused before the tool runs.
            match! Catalog.callTool server "repo_log" (argsOf """{"revspec_or_revset":"HEAD"}""") with
            | Error e -> Assert.That(e.Message, Does.Contain "max")
            | Ok _ -> Assert.Fail "a missing required argument must be refused"
        }
