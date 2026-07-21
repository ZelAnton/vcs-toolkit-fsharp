module VcsToolkit.Mcp.Tests

open System
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.CliSupport
open VcsToolkit.Core
open VcsToolkit.Forge
open VcsToolkit.Git
open VcsToolkit.Gitea
open VcsToolkit.GitHub
open VcsToolkit.Mcp
open VcsToolkit.TestKit

/// A git-backed server over a scripted runner — no real binary, no forge — with an
/// explicit output budget (`None` = unlimited).
let private gitServerWithBudget (runner: ScriptedRunner) (writes: WriteGate) (outputBudget: int option) =
    new VcsMcpServer(Repo.FromGit("/repo", "/repo", Git.WithRunner runner), Option.None, writes, outputBudget)

/// A git-backed server over a scripted runner — no real binary, no forge, no output budget.
let private gitServer (runner: ScriptedRunner) (writes: WriteGate) =
    gitServerWithBudget runner writes Option.None

/// A git-backed server with a GitHub forge, both wired to the same scripted runner (no real
/// `git`/`gh` binaries), with an explicit output budget (`None` = unlimited).
let private gitServerWithForgeAndBudget (runner: ScriptedRunner) (writes: WriteGate) (outputBudget: int option) =
    new VcsMcpServer(
        Repo.FromGit("/repo", "/repo", Git.WithRunner runner),
        Some(Forge.FromGitHub("/repo", GitHub.WithRunner runner)),
        writes,
        outputBudget
    )

/// A git-backed server with a GitHub forge, both wired to the same scripted runner (no real
/// `git`/`gh` binaries).
let private gitServerWithForge (runner: ScriptedRunner) (writes: WriteGate) =
    gitServerWithForgeAndBudget runner writes Option.None

/// A git-backed server with a Gitea forge (no real `git`/`tea` binaries) — used to exercise
/// the structural `Unsupported` refusal `forge_pr_diff` shares with the other `ReadForge`-based
/// forge tools that Gitea doesn't cover (`forge_pr_checks`, `forge_release_view`, ...).
let private gitServerWithGiteaForge (runner: ScriptedRunner) (writes: WriteGate) =
    new VcsMcpServer(
        Repo.FromGit("/repo", "/repo", Git.WithRunner runner),
        Some(Forge.FromGitea("/repo", Gitea.WithRunner runner)),
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
        Assert.That(List.length WriteTools.all, Is.EqualTo 25)
        Assert.That(WriteTools.asSet.Contains "repo_commit", Is.True)
        Assert.That(WriteTools.asSet.Contains "repo_rebase", Is.True, "the new rebase tool is write-gated")
        Assert.That(WriteTools.asSet.Contains "forge_pr_checkout", Is.True, "the local-checkout tool is write-gated")
        Assert.That(WriteTools.asSet.Contains "forge_pr_review", Is.True, "the new pr-review tool is write-gated")
        Assert.That(WriteTools.asSet.Contains "forge_issue_close", Is.True, "the new issue-close tool is write-gated")

        Assert.That(
            WriteTools.asSet.Contains "forge_issue_comment",
            Is.True,
            "the new issue-comment tool is write-gated"
        )

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

    [<Test>]
    member _.LogCommandsDefaultsToOff() =
        Assert.That((ok []).LogCommands, Is.EqualTo Option.None)

    [<Test>]
    member _.LogCommandsStderrParsesToTheStderrSink() =
        Assert.That((ok [ "--log-commands"; "stderr" ]).LogCommands, Is.EqualTo(Some LogSink.Stderr))

    [<Test>]
    member _.LogCommandsPathParsesToTheFileSink() =
        Assert.That(
            (ok [ "--log-commands"; "/tmp/vcs-mcp-commands.log" ]).LogCommands,
            Is.EqualTo(Some(LogSink.File "/tmp/vcs-mcp-commands.log"))
        )

    [<Test>]
    member _.LogCommandsMissingValueErrors() =
        Assert.That((err [ "--log-commands" ]), Does.Contain "needs a value")

    [<Test>]
    member _.LogCommandsAppearsInHelp() =
        Assert.That(Args.usage, Does.Contain "--log-commands")

// ---------------------------------------------------------------------------
// CommandLog — the `--log-commands` line formatters and the `ICommandObserver` they back.
// ---------------------------------------------------------------------------

[<TestFixture>]
type CommandLogTests() =

    let ev =
        { Program = "git"
          Argv = [ "status"; "--porcelain" ]
          WorkingDirectory = Some "/repo"
          Attempt = 0
          HasSecret = false }

    [<Test>]
    member _.FormatStartedNamesProgramArgvCwdAndAttempt() =
        let line = CommandLog.formatStarted ev
        Assert.That(line, Does.Contain "vcs-mcp: start")
        Assert.That(line, Does.Contain "program=git")
        Assert.That(line, Does.Contain "\"status\" \"--porcelain\"")
        Assert.That(line, Does.Contain "cwd=/repo")
        Assert.That(line, Does.Contain "attempt=0")

    [<Test>]
    member _.FormatStartedFallsBackToDashWithNoWorkingDirectory() =
        let line =
            CommandLog.formatStarted
                { ev with
                    WorkingDirectory = Option.None }

        Assert.That(line, Does.Contain "cwd=-")

    [<Test>]
    member _.FormatFinishedReportsOkOutcomeAndDuration() =
        let line = CommandLog.formatFinished ev (TimeSpan.FromMilliseconds 42.0) (Ok 0)
        Assert.That(line, Does.Contain "vcs-mcp: done")
        Assert.That(line, Does.Contain "duration=42ms")
        Assert.That(line, Does.Contain "outcome=ok(0)")

    [<Test>]
    member _.FormatFinishedReportsErrorMessageNeverSecretValue() =
        let error = ProcessError.Exit("git", 128, "", "fatal: not a git repository")

        let line =
            CommandLog.formatFinished ev (TimeSpan.FromMilliseconds 5.0) (Error error)

        Assert.That(line, Does.Contain "outcome=error(")
        Assert.That(line, Does.Contain "not a git repository")

    [<Test>]
    member _.WriterWritesOneLinePerCallAndFlushes() =
        use sw = new IO.StringWriter()
        let observer = CommandLog.Writer(sw) :> ICommandObserver
        observer.OnStarted ev
        observer.OnFinished(ev, TimeSpan.FromMilliseconds 1.0, Ok 0)

        let lines =
            sw.ToString().Split([| Environment.NewLine |], StringSplitOptions.RemoveEmptyEntries)

        Assert.That(lines.Length, Is.EqualTo 2)
        Assert.That(lines.[0], Does.Contain "start")
        Assert.That(lines.[1], Does.Contain "done")

// ---------------------------------------------------------------------------
// Tool dispatch, gating, and error mapping (over a scripted repo)
// ---------------------------------------------------------------------------

[<TestFixture>]
type ErrorMappingTests() =

    [<Test>]
    member _.CoreInvalidInputMapsToInvalidParams() =
        match coreErr (RepoError.InvalidInput "paths cannot be empty") with
        | McpError.InvalidParams message -> Assert.That(message, Is.EqualTo "paths cannot be empty")
        | McpError.Internal message -> Assert.Fail $"expected invalid params, got internal: {message}"

    [<Test>]
    member _.CoreUnsupportedMapsToInvalidParams() =
        match coreErr (RepoError.Unsupported "continue during bisect") with
        | McpError.InvalidParams message ->
            Assert.That(message, Is.EqualTo "unsupported operation: continue during bisect")
        | McpError.Internal message -> Assert.Fail $"expected invalid params, got internal: {message}"

    [<Test>]
    member _.CoreIoMapsToInternal() =
        match coreErr (RepoError.Io "directory delete failed") with
        | McpError.Internal message -> Assert.That(message, Is.EqualTo "directory delete failed")
        | McpError.InvalidParams message -> Assert.Fail $"expected internal, got invalid params: {message}"

    [<Test>]
    member _.ForgeUnsupportedMapsToInvalidParams() =
        match forgeErr (ForgeError.Unsupported(ForgeKind.Gitea, "prMarkReady")) with
        | McpError.InvalidParams message -> Assert.That(message, Is.EqualTo "gitea does not support `prMarkReady`")
        | McpError.Internal message -> Assert.Fail $"expected invalid params, got internal: {message}"

    [<Test>]
    member _.ForgeUnsupportedVersionMapsToInvalidParams() =
        let found: VcsToolkit.Diff.Version =
            { Major = 1UL
              Minor = 0UL
              Patch = 0UL }

        let minimum: VcsToolkit.Diff.Version =
            { Major = 2UL
              Minor = 0UL
              Patch = 0UL }

        match forgeErr (ForgeError.UnsupportedVersion(ForgeKind.GitHub, "prReview", found, minimum)) with
        | McpError.InvalidParams message ->
            Assert.That(message, Is.EqualTo "github `prReview` requires the CLI at version 2.0.0 or newer, found 1.0.0")
        | McpError.Internal message -> Assert.Fail $"expected invalid params, got internal: {message}"

    [<Test>]
    member _.ForgeInvalidInputMapsToInvalidParams() =
        match forgeErr (ForgeError.InvalidInput "title and body cannot both be empty") with
        | McpError.InvalidParams message -> Assert.That(message, Is.EqualTo "title and body cannot both be empty")
        | McpError.Internal message -> Assert.Fail $"expected invalid params, got internal: {message}"

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
    member _.RepoRemotesReturnsRemoteJson() : Task =
        task {
            // repo_remotes is a read tool (no write gate) surfacing the facade's `Remote` list as
            // JSON — name + URL, with git's fetch/push lines deduplicated to one entry per remote.
            let server =
                gitServer
                    (ScriptedRunner()
                        .On(
                            [ "remote"; "-v" ],
                            Reply.Ok
                                "origin\thttps://github.com/example/repo.git (fetch)\norigin\thttps://github.com/example/repo.git (push)\n"
                        ))
                    WriteGate.None

            match! server.RepoRemotes() with
            | Ok json ->
                Assert.That(json, Does.Contain "origin")
                Assert.That(json, Does.Contain "github.com/example/repo.git")
            | Error e -> Assert.Fail $"repo_remotes failed: {e.Message}"
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
    member _.RepoAnnotateReturnsAnnotateLineJson() : Task =
        task {
            // repo_annotate is a read tool (no write gate) that surfaces the facade's unified
            // `AnnotateLine` DTO as a JSON array — author/date included on git.
            let tab = string (char 9)
            let sha = "0123456789abcdef0123456789abcdef01234567"

            let out =
                [ sha + " 1 1 1"
                  "author Alice Example"
                  "author-time 1700000000"
                  "author-tz +0000"
                  tab + "let x = 1" ]
                |> String.concat "\n"

            let server =
                gitServer
                    (ScriptedRunner().On([ "blame"; "--line-porcelain"; "--"; "f.txt" ], Reply.Ok out))
                    WriteGate.None

            match! server.RepoAnnotate("f.txt", Option.None) with
            | Ok json ->
                Assert.That(json, Does.Contain sha)
                Assert.That(json, Does.Contain "let x = 1")
                Assert.That(json, Does.Contain "Alice Example")
                // System.Text.Json's default HTML-safe encoder escapes a literal plus sign, so
                // match the date only up to the offset sign, not the raw "+00:00" suffix.
                Assert.That(json, Does.Contain "2023-11-14T22:13:20")
            | Error e -> Assert.Fail $"repo_annotate failed: {e.Message}"
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

            match! server.ForgePrList(None, None) with
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
    member _.ForgePrCloseAndRepoCheckoutSerializeOnTheRepoWriteLock() : Task =
        task {
            // `gh pr close --delete-branch` can delete the current local branch and switch the
            // working tree to the default branch. It therefore must share the repo write lock
            // with repo_* mutations even when `deleteBranch` is false, avoiding a lock decision
            // that races the branch. Block repo_checkout, then prove forge_pr_close cannot run
            // until that local mutation releases the lock.
            use checkoutStarted = new SemaphoreSlim(0)
            use releaseCheckout = new SemaphoreSlim(0)

            let isCheckout (command: Command) =
                command.Program :: List.ofSeq command.Arguments |> List.contains "checkout"

            let runner =
                ScriptedRunner()
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
                    .On([ "pr"; "close"; "2" ], Reply.Ok "")

            let server = gitServerWithForge runner WriteGate.All

            let checkoutTask =
                Task.Run<Result<string, McpError>>(fun () -> server.RepoCheckout "feature")

            Assert.That(checkoutStarted.Wait(TimeSpan.FromSeconds 5.0), Is.True, "checkout must have started")

            let closeTask =
                Task.Run<Result<string, McpError>>(fun () -> server.ForgePrClose(2UL, false))

            Assert.That(
                (closeTask :> Task).Wait(TimeSpan.FromMilliseconds 300.0),
                Is.False,
                "forge_pr_close must block on the repo write lock held by repo_checkout"
            )

            releaseCheckout.Release() |> ignore

            match! checkoutTask with
            | Ok json -> Assert.That(json, Does.Contain "feature")
            | Error e -> Assert.Fail $"repo_checkout failed: {e.Message}"

            match! closeTask with
            | Ok json -> Assert.That(json, Does.Contain "closed")
            | Error e -> Assert.Fail $"forge_pr_close failed: {e.Message}"
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
    member _.ForgeReleaseCreateIsWriteGated() : Task =
        task {
            // forge_release_create mutates the remote, so it is write-gated: refused in the
            // default read-only mode. The gate is checked before the forge is resolved.
            let server = gitServer (ScriptedRunner()) WriteGate.None

            match! server.ForgeReleaseCreate("v1", Option.None, Option.None, false, false) with
            | Error e -> Assert.That(e.Message, Does.Contain "allow-write")
            | Ok _ -> Assert.Fail "forge_release_create must be gated in read-only mode"
        }

    [<Test>]
    member _.ForgeReleaseCreateRejectsDashLeadingTagWithoutSpawning() : Task =
        task {
            // The guard fires before any spawn, so a fallback that would fail loudly is never
            // reached — proving the refusal precedes the forge call.
            let runner =
                ScriptedRunner().Fallback(Reply.Fail(1, "must not spawn — refusal must precede it"))

            let server = gitServerWithForge runner WriteGate.All

            match! server.ForgeReleaseCreate("-tag", Option.None, Option.None, false, false) with
            | Error(McpError.InvalidParams _) -> ()
            | Error e -> Assert.Fail $"expected invalid params, got: {e.Message}"
            | Ok _ -> Assert.Fail "a dash-leading tag must be refused"
        }

    [<Test>]
    member _.ForgeReleaseCreateAcceptsTagTitleNotesAndFlags() : Task =
        task {
            // The GitHub-backed server dispatches to `gh release create` and returns the URL.
            let server =
                gitServerWithForge
                    (ScriptedRunner()
                        .On([ "--version" ], Reply.Ok "gh version 2.40.0\n")
                        .On([ "release"; "create" ], Reply.Ok "https://x/releases/v1\n"))
                    WriteGate.All

            match! server.ForgeReleaseCreate("v1", Some "1.0", Some "notes", true, true) with
            | Ok json -> Assert.That(json, Does.Contain "https://x/releases/v1")
            | Error e -> Assert.Fail $"forge_release_create failed: {e.Message}"
        }

    [<Test>]
    member _.ForgeIssueCloseIsWriteGated() : Task =
        task {
            // forge_issue_close mutates the remote, so it is write-gated: refused in the
            // default read-only mode. The gate is checked before the forge is resolved.
            let server = gitServer (ScriptedRunner()) WriteGate.None

            match! server.ForgeIssueClose 1UL with
            | Error e -> Assert.That(e.Message, Does.Contain "allow-write")
            | Ok _ -> Assert.Fail "forge_issue_close must be gated in read-only mode"
        }

    [<Test>]
    member _.ForgeIssueCloseAcceptsAndReportsClosed() : Task =
        task {
            let server =
                gitServerWithForge
                    (ScriptedRunner()
                        .On([ "--version" ], Reply.Ok "gh version 2.40.0\n")
                        .On([ "issue"; "close" ], Reply.Ok ""))
                    WriteGate.All

            match! server.ForgeIssueClose 7UL with
            | Ok json -> Assert.That(json, Does.Contain "closed")
            | Error e -> Assert.Fail $"forge_issue_close failed: {e.Message}"
        }

    [<Test>]
    member _.ForgeIssueCommentIsWriteGated() : Task =
        task {
            let server = gitServer (ScriptedRunner()) WriteGate.None

            match! server.ForgeIssueComment(1UL, "hi") with
            | Error e -> Assert.That(e.Message, Does.Contain "allow-write")
            | Ok _ -> Assert.Fail "forge_issue_comment must be gated in read-only mode"
        }

    [<Test>]
    member _.ForgeIssueCommentRejectsDashLeadingBodyWithoutSpawning() : Task =
        task {
            // The argv guard fires before any spawn, so a fallback that would fail loudly is
            // never reached — proving the refusal precedes the forge call.
            let runner =
                ScriptedRunner().Fallback(Reply.Fail(1, "must not spawn — refusal must precede it"))

            let server = gitServerWithForge runner WriteGate.All

            match! server.ForgeIssueComment(1UL, "-body") with
            | Error(McpError.InvalidParams _) -> ()
            | Error e -> Assert.Fail $"expected invalid params, got: {e.Message}"
            | Ok _ -> Assert.Fail "a dash-leading body must be refused"
        }

    [<Test>]
    member _.ForgeIssueCommentRejectsEmptyBodyWithoutSpawning() : Task =
        task {
            // A whitespace-only body is refused (as InvalidInput → InvalidParams) by the facade
            // before the version probe, so a loudly-failing fallback is never reached.
            let runner =
                ScriptedRunner().Fallback(Reply.Fail(1, "must not spawn — refusal must precede it"))

            let server = gitServerWithForge runner WriteGate.All

            match! server.ForgeIssueComment(1UL, "   ") with
            | Error(McpError.InvalidParams message) -> Assert.That(message, Does.Contain "empty")
            | Error e -> Assert.Fail $"expected invalid params, got: {e.Message}"
            | Ok _ -> Assert.Fail "a whitespace-only body must be refused"
        }

    [<Test>]
    member _.ForgeIssueCommentAcceptsNonDashBody() : Task =
        task {
            let server =
                gitServerWithForge
                    (ScriptedRunner()
                        .On([ "--version" ], Reply.Ok "gh version 2.40.0\n")
                        .On([ "issue"; "comment" ], Reply.Ok "https://c/9\n"))
                    WriteGate.All

            match! server.ForgeIssueComment(9UL, "fine body") with
            | Ok json -> Assert.That(json, Does.Contain "https://c/9")
            | Error e -> Assert.Fail $"forge_issue_comment failed: {e.Message}"
        }

    [<Test>]
    member _.ForgeIssueCommentDoesNotHoldTheRepoWriteLock() : Task =
        task {
            // forge_issue_comment is a remote-only mutation (K-003): it must use WithForgeWrite,
            // NOT WithForgeRepoWrite, so it does NOT serialize on the per-repo write lock the way
            // the local-mutating forge writes (forge_pr_checkout/merge/close) do. Prove it: hold
            // the repo write lock with a blocked repo_checkout, then show forge_issue_comment
            // still completes rather than blocking behind it.
            use checkoutStarted = new SemaphoreSlim(0)
            use releaseCheckout = new SemaphoreSlim(0)

            let isCheckout (command: Command) =
                command.Program :: List.ofSeq command.Arguments |> List.contains "checkout"

            let runner =
                ScriptedRunner()
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
                    .On([ "issue"; "comment" ], Reply.Ok "https://c/1\n")

            let server = gitServerWithForge runner WriteGate.All

            let checkoutTask =
                Task.Run<Result<string, McpError>>(fun () -> server.RepoCheckout "feature")

            Assert.That(checkoutStarted.Wait(TimeSpan.FromSeconds 5.0), Is.True, "checkout must have started")

            // The comment holds no repo lock, so it completes even while checkout still holds it.
            let commentTask =
                Task.Run<Result<string, McpError>>(fun () -> server.ForgeIssueComment(2UL, "hello"))

            Assert.That(
                (commentTask :> Task).Wait(TimeSpan.FromSeconds 5.0),
                Is.True,
                "forge_issue_comment must NOT block on the repo write lock held by repo_checkout"
            )

            match! commentTask with
            | Ok json -> Assert.That(json, Does.Contain "output")
            | Error e -> Assert.Fail $"forge_issue_comment failed: {e.Message}"

            releaseCheckout.Release() |> ignore

            match! checkoutTask with
            | Ok json -> Assert.That(json, Does.Contain "feature")
            | Error e -> Assert.Fail $"repo_checkout failed: {e.Message}"
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

    [<Test>]
    member _.ForgePrReviewIsWriteGated() : Task =
        task {
            // A remote-only mutation, so it is write-gated: refused in the default read-only mode,
            // and the gate is checked before the forge is resolved.
            let server = gitServer (ScriptedRunner()) WriteGate.None

            match! server.ForgePrReview(1UL, "approve", Option.None) with
            | Error e -> Assert.That(e.Message, Does.Contain "allow-write")
            | Ok _ -> Assert.Fail "forge_pr_review must be gated in read-only mode"
        }

    [<Test>]
    member _.ForgePrReviewEnforcesBodyInvariantAndKindWithoutSpawning() : Task =
        task {
            // request_changes/comment require a non-empty body, and an unknown kind is refused —
            // all as InvalidParams before any spawn, so a loudly-failing fallback is never reached.
            let runner =
                ScriptedRunner().Fallback(Reply.Fail(1, "must not spawn — refusal must precede it"))

            let server = gitServerWithForge runner WriteGate.All

            match! server.ForgePrReview(1UL, "request_changes", Option.None) with
            | Error(McpError.InvalidParams message) -> Assert.That(message, Does.Contain "body")
            | Error e -> Assert.Fail $"expected invalid params, got: {e.Message}"
            | Ok _ -> Assert.Fail "request_changes without a body must be refused"

            match! server.ForgePrReview(1UL, "comment", Some "   ") with
            | Error(McpError.InvalidParams message) -> Assert.That(message, Does.Contain "body")
            | Error e -> Assert.Fail $"expected invalid params, got: {e.Message}"
            | Ok _ -> Assert.Fail "a comment review with a whitespace-only body must be refused"

            match! server.ForgePrReview(1UL, "bogus", Option.None) with
            | Error(McpError.InvalidParams message) -> Assert.That(message, Does.Contain "review kind")
            | Error e -> Assert.Fail $"expected invalid params, got: {e.Message}"
            | Ok _ -> Assert.Fail "an unknown review kind must be refused"
        }

    [<Test>]
    member _.ForgePrReviewRejectsDashLeadingBodyWithoutSpawning() : Task =
        task {
            let runner =
                ScriptedRunner().Fallback(Reply.Fail(1, "must not spawn — refusal must precede it"))

            let server = gitServerWithForge runner WriteGate.All

            match! server.ForgePrReview(1UL, "request_changes", Some "-flagged") with
            | Error(McpError.InvalidParams _) -> ()
            | Error e -> Assert.Fail $"expected invalid params, got: {e.Message}"
            | Ok _ -> Assert.Fail "a dash-leading review body must be refused"
        }

    [<Test>]
    member _.ForgePrReviewApproveDispatchesAndReportsReviewed() : Task =
        task {
            let server =
                gitServerWithForge
                    (ScriptedRunner()
                        .On([ "--version" ], Reply.Ok "gh version 2.40.0\n")
                        .On([ "pr"; "review"; "7"; "--approve" ], Reply.Ok ""))
                    WriteGate.All

            match! server.ForgePrReview(7UL, "approve", Option.None) with
            | Ok json -> Assert.That(json, Does.Contain "reviewed")
            | Error e -> Assert.Fail $"forge_pr_review approve failed: {e.Message}"
        }

    [<Test>]
    member _.ForgePrReviewDoesNotHoldTheRepoWriteLock() : Task =
        task {
            // forge_pr_review is a remote-only mutation (K-003): it must use WithForgeWrite, NOT
            // WithForgeRepoWrite, so it does NOT serialize on the per-repo write lock. Prove it:
            // hold the repo write lock with a blocked repo_checkout, then show forge_pr_review
            // still completes rather than blocking behind it.
            use checkoutStarted = new SemaphoreSlim(0)
            use releaseCheckout = new SemaphoreSlim(0)

            let isCheckout (command: Command) =
                command.Program :: List.ofSeq command.Arguments |> List.contains "checkout"

            let runner =
                ScriptedRunner()
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
                    .On([ "pr"; "review" ], Reply.Ok "")

            let server = gitServerWithForge runner WriteGate.All

            let checkoutTask =
                Task.Run<Result<string, McpError>>(fun () -> server.RepoCheckout "feature")

            Assert.That(checkoutStarted.Wait(TimeSpan.FromSeconds 5.0), Is.True, "checkout must have started")

            // The review holds no repo lock, so it completes even while checkout still holds it.
            let reviewTask =
                Task.Run<Result<string, McpError>>(fun () -> server.ForgePrReview(2UL, "approve", Option.None))

            Assert.That(
                (reviewTask :> Task).Wait(TimeSpan.FromSeconds 5.0),
                Is.True,
                "forge_pr_review must NOT block on the repo write lock held by repo_checkout"
            )

            match! reviewTask with
            | Ok json -> Assert.That(json, Does.Contain "reviewed")
            | Error e -> Assert.Fail $"forge_pr_review failed: {e.Message}"

            releaseCheckout.Release() |> ignore

            match! checkoutTask with
            | Ok json -> Assert.That(json, Does.Contain "feature")
            | Error e -> Assert.Fail $"repo_checkout failed: {e.Message}"
        }

    // --- the six new repo mutations: write-gate + happy path (scripted) -----

    [<Test>]
    member _.RepoRebaseIsWriteGated() : Task =
        task {
            // No `rebase` rule on the runner: were the gate to fail open, the spawn would error
            // differently than the gate's --allow-write message.
            let server = gitServer (ScriptedRunner()) WriteGate.None

            match! server.RepoRebase "main" with
            | Error e -> Assert.That(e.Message, Does.Contain "allow-write")
            | Ok _ -> Assert.Fail "repo_rebase must be gated in read-only mode"
        }

    [<Test>]
    member _.RepoRebaseReachesRunnerWithAllowWrite() : Task =
        task {
            let server =
                gitServer (ScriptedRunner().On([ "rebase" ], Reply.Ok "")) WriteGate.All

            match! server.RepoRebase "main" with
            | Ok json -> Assert.That(json, Does.Contain "main")
            | Error e -> Assert.Fail $"repo_rebase failed: {e.Message}"
        }

    [<Test>]
    member _.RepoDeleteBranchIsWriteGated() : Task =
        task {
            let server = gitServer (ScriptedRunner()) WriteGate.None

            match! server.RepoDeleteBranch("feature", false) with
            | Error e -> Assert.That(e.Message, Does.Contain "allow-write")
            | Ok _ -> Assert.Fail "repo_delete_branch must be gated in read-only mode"
        }

    [<Test>]
    member _.RepoDeleteBranchReachesRunnerWithAllowWrite() : Task =
        task {
            // git branch -d/-D <name>; the `branch` token is enough to match the scripted rule.
            let server =
                gitServer (ScriptedRunner().On([ "branch" ], Reply.Ok "")) WriteGate.All

            match! server.RepoDeleteBranch("feature", false) with
            | Ok json -> Assert.That(json, Does.Contain "feature")
            | Error e -> Assert.Fail $"repo_delete_branch failed: {e.Message}"
        }

    [<Test>]
    member _.RepoRenameBranchIsWriteGated() : Task =
        task {
            let server = gitServer (ScriptedRunner()) WriteGate.None

            match! server.RepoRenameBranch("old", "renamed") with
            | Error e -> Assert.That(e.Message, Does.Contain "allow-write")
            | Ok _ -> Assert.Fail "repo_rename_branch must be gated in read-only mode"
        }

    [<Test>]
    member _.RepoRenameBranchReachesRunnerWithAllowWrite() : Task =
        task {
            let server =
                gitServer (ScriptedRunner().On([ "branch" ], Reply.Ok "")) WriteGate.All

            match! server.RepoRenameBranch("old", "renamed") with
            | Ok json -> Assert.That(json, Does.Contain "renamed")
            | Error e -> Assert.Fail $"repo_rename_branch failed: {e.Message}"
        }

    [<Test>]
    member _.RepoNewChildIsWriteGated() : Task =
        task {
            let server = gitServer (ScriptedRunner()) WriteGate.None

            match! server.RepoNewChild "main" with
            | Error e -> Assert.That(e.Message, Does.Contain "allow-write")
            | Ok _ -> Assert.Fail "repo_new_child must be gated in read-only mode"
        }

    [<Test>]
    member _.RepoNewChildReachesRunnerWithAllowWrite() : Task =
        task {
            // On git, NewChild maps to checkout (append-on-top is already non-destructive there).
            let server =
                gitServer (ScriptedRunner().On([ "checkout" ], Reply.Ok "")) WriteGate.All

            match! server.RepoNewChild "main" with
            | Ok json -> Assert.That(json, Does.Contain "main")
            | Error e -> Assert.Fail $"repo_new_child failed: {e.Message}"
        }

    [<Test>]
    member _.RepoAbortInProgressIsWriteGated() : Task =
        task {
            // The gate is checked before any spawn, so a bare runner suffices to prove the refusal.
            let server = gitServer (ScriptedRunner()) WriteGate.None

            match! server.RepoAbortInProgress() with
            | Error e -> Assert.That(e.Message, Does.Contain "allow-write")
            | Ok _ -> Assert.Fail "repo_abort_in_progress must be gated in read-only mode"
        }

    [<Test>]
    member _.RepoContinueInProgressIsWriteGated() : Task =
        task {
            let server = gitServer (ScriptedRunner()) WriteGate.None

            match! server.RepoContinueInProgress() with
            | Error e -> Assert.That(e.Message, Does.Contain "allow-write")
            | Ok _ -> Assert.Fail "repo_continue_in_progress must be gated in read-only mode"
        }

    [<Test>]
    member _.RepoRebaseIsGatedPerToolAllowlist() : Task =
        task {
            // Only repo_rebase is enabled — repo_delete_branch stays gated. Proves the new tools
            // participate in the per-tool allowlist by their own names.
            let server =
                gitServer (ScriptedRunner().On([ "rebase" ], Reply.Ok "")) (WriteGate.Set(Set.ofList [ "repo_rebase" ]))

            match! server.RepoRebase "main" with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"repo_rebase should be allowed: {e.Message}"

            match! server.RepoDeleteBranch("feature", false) with
            | Error e ->
                Assert.That(e.Message, Does.Contain "allow-write", "repo_delete_branch is not in the allowlist")
            | Ok _ -> Assert.Fail "repo_delete_branch must stay gated"
        }

// ---------------------------------------------------------------------------
// repo_abort_in_progress / repo_continue_in_progress over a REAL merge-in-progress
// (a genuine git conflict in a throwaway sandbox — not a scripted "nothing to abort")
// ---------------------------------------------------------------------------

[<TestFixture>]
type RepoOperationStateIntegrationTests() =

    let requireGit () =
        try
            Raw.git "." [ "--version" ]
        with _ ->
            // git isn't on PATH (or failed to spawn) — a hermetic CI without it must skip,
            // not fail, this fixture.
            Assert.Ignore "git not available on PATH"

    /// Build a throwaway git repo left mid-merge with a real, unresolved conflict on `a.txt`:
    /// `main` and `feature` edit the same line, so `git merge feature` stops with `MERGE_HEAD`
    /// present and the index unmerged. Returns the live sandbox (the caller `use`s it).
    let conflictedMerge (tag: string) : GitSandbox =
        let sandbox = GitSandbox.Init tag
        sandbox.CommitFile("a.txt", "base\n", "seed")
        sandbox.Branch "feature"
        sandbox.Checkout "feature"
        sandbox.CommitFile("a.txt", "feature change\n", "feature")
        sandbox.Checkout "main"
        sandbox.CommitFile("a.txt", "main change\n", "main")

        // Conflicting by construction — the non-zero exit is the expected outcome, not a fixture
        // failure, so the raising sandbox helper is caught.
        try
            sandbox.Git [ "merge"; "-q"; "--no-edit"; "feature" ]
        with _ ->
            ()

        sandbox

    /// A write-enabled server over the real `git` binary bound to `path`.
    let realServer (path: string) =
        new VcsMcpServer(Repo.FromGit(path, path, Git.Create()), Option.None, WriteGate.All, Option.None)

    [<Test>]
    member _.AbortInProgressClearsARealMergeConflict() : Task =
        task {
            requireGit ()
            use sandbox = conflictedMerge "mcp-abort"
            use server = realServer sandbox.Path

            // Pre-condition: the merge really is in progress with an unresolved path.
            match! server.RepoConflicts() with
            | Ok json -> Assert.That(json, Does.Contain "a.txt", "the fixture must leave a real conflict")
            | Error e -> Assert.Fail $"repo_conflicts failed: {e.Message}"

            match! server.RepoAbortInProgress() with
            | Ok json -> Assert.That(json, Does.Contain "Clear", "aborting the merge returns to a Clear state")
            | Error e -> Assert.Fail $"repo_abort_in_progress failed: {e.Message}"

            // Post-condition: the conflict is gone (the abort really ran, not just reported Clear).
            match! server.RepoConflicts() with
            | Ok json -> Assert.That(json, Does.Not.Contain "a.txt", "the abort must clear the unmerged index")
            | Error e -> Assert.Fail $"repo_conflicts failed: {e.Message}"
        }

    [<Test>]
    member _.ContinueInProgressReportsConflictWhileUnresolved() : Task =
        task {
            requireGit ()
            use sandbox = conflictedMerge "mcp-continue-blocked"
            use server = realServer sandbox.Path

            // git refuses to continue while unmerged paths remain; the facade reports that as the
            // Conflict state rather than surfacing a hard error.
            match! server.RepoContinueInProgress() with
            | Ok json -> Assert.That(json, Does.Contain "Conflict", "an unresolved merge cannot be continued")
            | Error e -> Assert.Fail $"repo_continue_in_progress failed: {e.Message}"
        }

    [<Test>]
    member _.ContinueInProgressFinishesAResolvedMerge() : Task =
        task {
            requireGit ()
            use sandbox = conflictedMerge "mcp-continue-resolved"
            use server = realServer sandbox.Path

            // Resolve the conflict, then continue: git commits the merge and the state clears.
            sandbox.Write("a.txt", "resolved\n")
            sandbox.AddAll()

            match! server.RepoContinueInProgress() with
            | Ok json -> Assert.That(json, Does.Contain "Clear", "a resolved merge continues to completion")
            | Error e -> Assert.Fail $"repo_continue_in_progress failed: {e.Message}"

            // The merge is committed — nothing left in progress, no residual conflict.
            match! server.RepoConflicts() with
            | Ok json -> Assert.That(json, Does.Not.Contain "a.txt")
            | Error e -> Assert.Fail $"repo_conflicts failed: {e.Message}"
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

    [<Test>]
    member _.RepoAnnotateOverBudgetIsTruncatedWithMarker() : Task =
        task {
            // repo_annotate applies the SAME budget mechanism as repo_show_file, but to the
            // serialized JSON array (there is no single "content" string for a list of lines).
            let tab = string (char 9)
            let sha = "0123456789abcdef0123456789abcdef01234567"

            let out =
                [ sha + " 1 1 1"
                  "author Alice Example"
                  "author-time 1700000000"
                  "author-tz +0000"
                  tab + "let x = 1" ]
                |> String.concat "\n"

            let server =
                gitServerWithBudget
                    (ScriptedRunner().On([ "blame"; "--line-porcelain"; "--"; "f.txt" ], Reply.Ok out))
                    WriteGate.None
                    (Some 10)

            match! server.RepoAnnotate("f.txt", Option.None) with
            | Ok json -> Assert.That(json, Does.Contain "[truncated: showing 10 of")
            | Error e -> Assert.Fail $"repo_annotate failed: {e.Message}"
        }

    [<Test>]
    member _.ForgePrDiffOverBudgetIsTruncatedWithMarker() : Task =
        task {
            // forge_pr_diff applies the SAME budget mechanism as repo_show_file/repo_annotate,
            // to the serialized per-file JSON array — a PR diff easily blows past a reasonable
            // context budget.
            let raw =
                "diff --git a/foo.txt b/foo.txt\n"
                + "index e69de29..4b825dc 100644\n"
                + "--- a/foo.txt\n"
                + "+++ b/foo.txt\n"
                + "@@ -0,0 +1 @@\n"
                + "+new line\n"

            let server =
                gitServerWithForgeAndBudget
                    (ScriptedRunner().On([ "pr"; "diff"; "42" ], Reply.Ok raw))
                    WriteGate.None
                    (Some 10)

            match! server.ForgePrDiff 42UL with
            | Ok json -> Assert.That(json, Does.Contain "[truncated: showing 10 of")
            | Error e -> Assert.Fail $"forge_pr_diff failed: {e.Message}"
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
        // 12 repo-read + repo_try_merge + 12 repo-write + 12 forge-read + 12 forge-write = 49.
        Assert.That(List.length Catalog.all, Is.EqualTo 49)
        // Every write-gated tool name appears in the catalogue.
        let names = Catalog.all |> List.map (fun t -> t.Name) |> Set.ofList
        Assert.That(WriteTools.all |> List.forall names.Contains, Is.True, "every write tool is catalogued")
        // repo_try_merge is gated but non-destructive/idempotent.
        let tryMerge = Catalog.all |> List.find (fun t -> t.Name = "repo_try_merge")
        Assert.That(tryMerge.ReadOnly, Is.False)
        Assert.That(tryMerge.Destructive, Is.False)
        Assert.That(tryMerge.Idempotent, Is.True)

    [<Test>]
    member _.WriteToolAnnotationsMatchTheirSemantics() =
        let expected =
            [ "repo_try_merge", false, true
              "repo_commit", false, false
              "repo_checkout", false, true
              "repo_fetch", false, true
              "repo_push", false, true
              "repo_create_worktree", false, false
              "repo_remove_worktree", true, false
              "repo_rebase", true, false
              "repo_abort_in_progress", true, true
              "repo_continue_in_progress", false, false
              "repo_delete_branch", true, false
              "repo_rename_branch", false, false
              "repo_new_child", false, false
              "forge_issue_create", false, false
              "forge_issue_close", false, true
              "forge_issue_comment", false, false
              "forge_pr_create", false, false
              "forge_pr_merge", true, false
              "forge_pr_close", true, true
              "forge_pr_mark_ready", false, true
              "forge_pr_comment", false, false
              "forge_pr_edit", false, true
              "forge_pr_checkout", false, true
              "forge_pr_review", false, false
              "forge_release_create", false, false ]

        let expectedNames: Set<string> =
            expected |> List.map (fun (name, _, _) -> name) |> Set.ofList
        // `Set<string>` also satisfies NUnit's `'T seq` overload of `Is.EqualTo`, so a plain
        // `Assert.That(expectedNames, Is.EqualTo WriteTools.asSet)` is ambiguous (FS0041) even
        // with the type annotation above; compare via F#'s structural `=` instead.
        Assert.That((expectedNames = WriteTools.asSet), Is.True, "expected tool names must match WriteTools.all")

        for name, destructive, idempotent in expected do
            let tool = Catalog.all |> List.find (fun t -> t.Name = name)
            Assert.That(tool.ReadOnly, Is.False, name)
            Assert.That(tool.Destructive, Is.EqualTo destructive, name)
            Assert.That(tool.Idempotent, Is.EqualTo idempotent, name)

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
    member _.ForgePrReviewSchemaRequiresNumberAndKindWithOptionalBody() =
        // number + kind are required; body is optional (required only for request_changes/comment,
        // which the server enforces — not expressible as a static JSON-Schema `required`).
        let review = Catalog.all |> List.find (fun t -> t.Name = "forge_pr_review")
        let schema = Catalog.inputSchema review
        Assert.That(schema, Does.Contain "\"kind\"")
        Assert.That(schema, Does.Contain "\"body\"")
        Assert.That(schema, Does.Contain "\"required\":[\"number\",\"kind\"]", "body is optional")

    [<Test>]
    member _.ForgeReleaseCreateSchemaRequiresOnlyTag() =
        // Only the tag is required; title/notes and the draft/prerelease booleans are optional.
        let release = Catalog.all |> List.find (fun t -> t.Name = "forge_release_create")
        let schema = Catalog.inputSchema release
        Assert.That(schema, Does.Contain "\"notes\"")
        Assert.That(schema, Does.Contain "\"draft\"")
        Assert.That(schema, Does.Contain "\"prerelease\"")
        Assert.That(schema, Does.Contain "\"required\":[\"tag\"]", "only tag is required")

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
    member _.CallToolDispatchesForgeIssueCloseAndComment() : Task =
        task {
            let runner =
                ScriptedRunner()
                    .On([ "--version" ], Reply.Ok "gh version 2.40.0\n")
                    .On([ "issue"; "close" ], Reply.Ok "")
                    .On([ "issue"; "comment" ], Reply.Ok "https://c/1\n")

            let server = gitServerWithForge runner WriteGate.All

            match! Catalog.callTool server "forge_issue_close" (argsOf """{"number":3}""") with
            | Ok json -> Assert.That(json, Does.Contain "closed")
            | Error e -> Assert.Fail $"forge_issue_close dispatch failed: {e.Message}"

            match! Catalog.callTool server "forge_issue_comment" (argsOf """{"number":3,"body":"nice"}""") with
            | Ok json -> Assert.That(json, Does.Contain "output")
            | Error e -> Assert.Fail $"forge_issue_comment dispatch failed: {e.Message}"

            // A missing required arg is refused as invalid-params before the tool runs.
            match! Catalog.callTool server "forge_issue_comment" (argsOf """{"number":3}""") with
            | Error e -> Assert.That(e.Message, Does.Contain "body")
            | Ok _ -> Assert.Fail "forge_issue_comment must require a body"
        }

    [<Test>]
    member _.CallToolDispatchesForgePrReview() : Task =
        task {
            let runner =
                ScriptedRunner().On([ "--version" ], Reply.Ok "gh version 2.40.0\n").On([ "pr"; "review" ], Reply.Ok "")

            let server = gitServerWithForge runner WriteGate.All

            match! Catalog.callTool server "forge_pr_review" (argsOf """{"number":4,"kind":"approve"}""") with
            | Ok json -> Assert.That(json, Does.Contain "reviewed")
            | Error e -> Assert.Fail $"forge_pr_review dispatch failed: {e.Message}"

            // A missing kind is refused as invalid-params before the tool runs.
            match! Catalog.callTool server "forge_pr_review" (argsOf """{"number":4}""") with
            | Error e -> Assert.That(e.Message, Does.Contain "kind")
            | Ok _ -> Assert.Fail "forge_pr_review must require a kind"

            // request_changes requires a body — the server enforces the ReviewAction invariant.
            match! Catalog.callTool server "forge_pr_review" (argsOf """{"number":4,"kind":"request_changes"}""") with
            | Error e -> Assert.That(e.Message, Does.Contain "body")
            | Ok _ -> Assert.Fail "request_changes must require a body"
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
    member _.CallToolBooleanOptionsAcceptAbsentAndBooleanValues() : Task =
        task {
            let runner =
                ScriptedRunner().On([ "--version" ], Reply.Ok "gh version 2.40.0\n").Fallback(Reply.Ok "")

            let repoServer = gitServer runner WriteGate.All
            let forgeServer = gitServerWithForge runner WriteGate.All

            let assertOk server tool args =
                task {
                    match! Catalog.callTool server tool (argsOf args) with
                    | Ok _ -> ()
                    | Error e -> Assert.Fail $"{tool} should accept {args}: {e.Message}"
                }

            do! assertOk repoServer "repo_remove_worktree" """{"path":"/worktree"}"""
            do! assertOk repoServer "repo_remove_worktree" """{"path":"/worktree","force":true}"""
            do! assertOk repoServer "repo_remove_worktree" """{"path":"/worktree","force":false}"""
            do! assertOk forgeServer "forge_pr_merge" """{"number":1,"strategy":"merge"}"""

            do!
                assertOk
                    forgeServer
                    "forge_pr_merge"
                    """{"number":1,"strategy":"merge","auto":true,"delete_branch":false}"""

            do!
                assertOk
                    forgeServer
                    "forge_pr_merge"
                    """{"number":1,"strategy":"merge","auto":false,"delete_branch":true}"""

            do! assertOk forgeServer "forge_pr_close" """{"number":1}"""
            do! assertOk forgeServer "forge_pr_close" """{"number":1,"delete_branch":true}"""
            do! assertOk forgeServer "forge_pr_close" """{"number":1,"delete_branch":false}"""
        }

    [<Test>]
    member _.CallToolBooleanOptionsRejectNonBooleanValues() : Task =
        task {
            let server = gitServer (ScriptedRunner()) WriteGate.All

            let assertInvalid tool args argument =
                task {
                    match! Catalog.callTool server tool (argsOf args) with
                    | Error(McpError.InvalidParams message) ->
                        Assert.That(message, Does.Contain argument)
                        Assert.That(message, Does.Contain "must be a boolean")
                    | Error e -> Assert.Fail $"{tool} should return InvalidParams, got: {e.Message}"
                    | Ok _ -> Assert.Fail $"{tool} should reject {argument} with a non-boolean value"
                }

            do! assertInvalid "repo_remove_worktree" """{"path":"/worktree","force":"true"}""" "force"
            do! assertInvalid "forge_pr_merge" """{"number":1,"strategy":"merge","auto":1}""" "auto"
            do! assertInvalid "forge_pr_merge" """{"number":1,"strategy":"merge","delete_branch":{}}""" "delete_branch"
            do! assertInvalid "forge_pr_close" """{"number":1,"delete_branch":"true"}""" "delete_branch"
        }

    [<Test>]
    member _.CallToolOptionalStringArgumentsRejectNonStringsAndNull() : Task =
        task {
            let server = gitServerWithForge (ScriptedRunner()) WriteGate.All

            let assertInvalid tool args argument =
                task {
                    match! Catalog.callTool server tool (argsOf args) with
                    | Error(McpError.InvalidParams message) ->
                        Assert.That(message, Does.Contain argument)
                        Assert.That(message, Does.Contain "must be a string")
                    | Error e -> Assert.Fail $"{tool} should return InvalidParams, got: {e.Message}"
                    | Ok _ -> Assert.Fail $"{tool} should reject {argument} with a non-string value"
                }

            do! assertInvalid "forge_pr_create" """{"title":"title","body":"body","source":1}""" "source"
            do! assertInvalid "forge_pr_create" """{"title":"title","body":"body","target":false}""" "target"
            do! assertInvalid "forge_pr_edit" """{"number":1,"title":[]}""" "title"
            do! assertInvalid "forge_pr_edit" """{"number":1,"body":{}}""" "body"
            do! assertInvalid "forge_pr_create" """{"title":"title","body":"body","source":null}""" "source"
            do! assertInvalid "forge_pr_create" """{"title":"title","body":"body","target":null}""" "target"
            do! assertInvalid "forge_pr_edit" """{"number":1,"title":null}""" "title"
            do! assertInvalid "forge_pr_edit" """{"number":1,"body":null}""" "body"
        }

    [<Test>]
    member _.CallToolOptionalStringArgumentsAcceptAbsentAndStringValues() : Task =
        task {
            let runner =
                ScriptedRunner()
                    .On([ "--version" ], Reply.Ok "gh version 2.40.0\n")
                    .On([ "pr"; "create" ], Reply.Ok "https://x/2\n")
                    .On([ "pr"; "edit" ], Reply.Ok "")

            let server = gitServerWithForge runner WriteGate.All

            match! Catalog.callTool server "forge_pr_create" (argsOf """{"title":"title","body":"body"}""") with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"forge_pr_create should accept absent source/target: {e.Message}"

            match!
                Catalog.callTool
                    server
                    "forge_pr_create"
                    (argsOf """{"title":"title","body":"body","source":"feature","target":"main"}""")
            with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"forge_pr_create should accept string source/target: {e.Message}"

            match!
                Catalog.callTool
                    server
                    "forge_pr_edit"
                    (argsOf """{"number":1,"title":"new title","body":"new body"}""")
            with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"forge_pr_edit should accept string title/body: {e.Message}"
        }

    [<Test>]
    member _.CallToolForgePrEditStillRequiresAStringField() : Task =
        task {
            let server = gitServerWithForge (ScriptedRunner()) WriteGate.All

            match! Catalog.callTool server "forge_pr_edit" (argsOf """{"number":1}""") with
            | Error e -> Assert.That(e.Message, Does.Contain "at least one of title or body must be set")
            | Ok _ -> Assert.Fail "forge_pr_edit should require title or body when both are absent"
        }

    [<Test>]
    member _.CallToolDispatchesForgeReleaseCreate() : Task =
        task {
            let runner =
                ScriptedRunner()
                    .On([ "--version" ], Reply.Ok "gh version 2.40.0\n")
                    .On([ "release"; "create" ], Reply.Ok "https://x/releases/v1\n")

            let server = gitServerWithForge runner WriteGate.All

            match! Catalog.callTool server "forge_release_create" (argsOf """{"tag":"v1","notes":"the notes"}""") with
            | Ok json -> Assert.That(json, Does.Contain "releases/v1")
            | Error e -> Assert.Fail $"forge_release_create dispatch failed: {e.Message}"
        }

    [<Test>]
    member _.ForgePrListSchemaAdvertisesOptionalStateAndLimit() =
        let prList = Catalog.all |> List.find (fun t -> t.Name = "forge_pr_list")
        let schema = Catalog.inputSchema prList
        Assert.That(schema, Does.Contain "\"state\"")
        Assert.That(schema, Does.Contain "\"limit\"")
        Assert.That(schema, Does.Contain "\"required\":[]", "state/limit are both optional")

    [<Test>]
    member _.ForgeIssueListSchemaAdvertisesOptionalStateAndLimit() =
        let issueList = Catalog.all |> List.find (fun t -> t.Name = "forge_issue_list")
        let schema = Catalog.inputSchema issueList
        Assert.That(schema, Does.Contain "\"state\"")
        Assert.That(schema, Does.Contain "\"limit\"")
        Assert.That(schema, Does.Contain "\"required\":[]", "state/limit are both optional")

    [<Test>]
    member _.CallToolDispatchesForgePrListWithStateAndLimit() : Task =
        task {
            let runner =
                ScriptedRunner().On([ "pr"; "list"; "--state"; "closed"; "--limit"; "50"; "--json" ], Reply.Ok "[]")

            let server = gitServerWithForge runner WriteGate.None

            match! Catalog.callTool server "forge_pr_list" (argsOf """{"state":"closed","limit":50}""") with
            | Ok json -> Assert.That(json, Does.Contain "[]")
            | Error e -> Assert.Fail $"forge_pr_list dispatch failed: {e.Message}"
        }

    [<Test>]
    member _.CallToolForgePrListOmittedArgumentsUseTheOptionsDefault() : Task =
        task {
            // Omitting both arguments must reproduce the previous, options-less behaviour —
            // open, up to 100 — exactly.
            let runner =
                ScriptedRunner().On([ "pr"; "list"; "--state"; "open"; "--limit"; "100"; "--json" ], Reply.Ok "[]")

            let server = gitServerWithForge runner WriteGate.None

            match! Catalog.callTool server "forge_pr_list" (argsOf "{}") with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"forge_pr_list with no args should use the default options: {e.Message}"
        }

    [<Test>]
    member _.ForgePrForBranchSchemaAdvertisesRequiredSourceBranch() =
        let prForBranch = Catalog.all |> List.find (fun t -> t.Name = "forge_pr_for_branch")
        let schema = Catalog.inputSchema prForBranch
        Assert.That(schema, Does.Contain "\"source_branch\"")
        Assert.That(schema, Does.Contain "\"required\":[\"source_branch\"]")

    [<Test>]
    member _.CallToolDispatchesForgePrForBranchWithSourceBranch() : Task =
        task {
            let runner =
                ScriptedRunner().On([ "pr"; "list"; "--head"; "feat"; "--state"; "all" ], Reply.Ok "[]")

            let server = gitServerWithForge runner WriteGate.None

            match! Catalog.callTool server "forge_pr_for_branch" (argsOf """{"source_branch":"feat"}""") with
            | Ok json -> Assert.That(json, Does.Contain "[]")
            | Error e -> Assert.Fail $"forge_pr_for_branch dispatch failed: {e.Message}"
        }

    [<Test>]
    member _.CallToolForgePrForBranchRequiresSourceBranch() : Task =
        task {
            let server = gitServerWithForge (ScriptedRunner()) WriteGate.None

            match! Catalog.callTool server "forge_pr_for_branch" (argsOf "{}") with
            | Error(McpError.InvalidParams message) -> Assert.That(message, Does.Contain "source_branch")
            | Error e -> Assert.Fail $"expected InvalidParams, got: {e.Message}"
            | Ok _ -> Assert.Fail "forge_pr_for_branch must require source_branch"
        }

    [<Test>]
    member _.ForgePrDiffSchemaAdvertisesRequiredNumber() =
        let prDiff = Catalog.all |> List.find (fun t -> t.Name = "forge_pr_diff")
        let schema = Catalog.inputSchema prDiff
        Assert.That(schema, Does.Contain "\"number\"")
        Assert.That(schema, Does.Contain "\"required\":[\"number\"]")

    [<Test>]
    member _.CallToolDispatchesForgePrDiff() : Task =
        task {
            let raw =
                "diff --git a/foo.txt b/foo.txt\n"
                + "index e69de29..4b825dc 100644\n"
                + "--- a/foo.txt\n"
                + "+++ b/foo.txt\n"
                + "@@ -0,0 +1 @@\n"
                + "+new line\n"

            let runner = ScriptedRunner().On([ "pr"; "diff"; "42" ], Reply.Ok raw)
            let server = gitServerWithForge runner WriteGate.None

            match! Catalog.callTool server "forge_pr_diff" (argsOf """{"number":42}""") with
            | Ok json ->
                Assert.That(json, Does.Contain "\"path\": \"foo.txt\"")
                Assert.That(json, Does.Contain "\"change\": \"Modified\"")
            | Error e -> Assert.Fail $"forge_pr_diff dispatch failed: {e.Message}"
        }

    [<Test>]
    member _.CallToolForgePrDiffIsUnsupportedOnGiteaLikeItsNeighbours() : Task =
        task {
            // Same structural refusal as forge_pr_checks/forge_release_view: Gitea's dispatch
            // returns `Unsupported` before spawning `tea` at all — an empty `ScriptedRunner`
            // (no fallback) proves it never reaches a spawn.
            let server = gitServerWithGiteaForge (ScriptedRunner()) WriteGate.None

            match! Catalog.callTool server "forge_pr_diff" (argsOf """{"number":1}""") with
            | Error(McpError.InvalidParams _) -> ()
            | Error e -> Assert.Fail $"expected InvalidParams for an Unsupported forge op, got: {e.Message}"
            | Ok _ -> Assert.Fail "forge_pr_diff must be Unsupported on Gitea"
        }

    [<Test>]
    member _.CallToolDispatchesForgeIssueListWithStateAndLimit() : Task =
        task {
            let runner =
                ScriptedRunner().On([ "issue"; "list"; "--state"; "all"; "--limit"; "5"; "--json" ], Reply.Ok "[]")

            let server = gitServerWithForge runner WriteGate.None

            match! Catalog.callTool server "forge_issue_list" (argsOf """{"state":"all","limit":5}""") with
            | Ok json -> Assert.That(json, Does.Contain "[]")
            | Error e -> Assert.Fail $"forge_issue_list dispatch failed: {e.Message}"
        }

    [<Test>]
    member _.CallToolForgePrListRejectsAnUnknownState() : Task =
        task {
            let server = gitServerWithForge (ScriptedRunner()) WriteGate.None

            match! Catalog.callTool server "forge_pr_list" (argsOf """{"state":"bogus"}""") with
            | Error e -> Assert.That(e.Message, Does.Contain "unknown state")
            | Ok _ -> Assert.Fail "forge_pr_list must reject an unrecognised state"
        }

    [<Test>]
    member _.CallToolForgeIssueListRejectsAnUnknownState() : Task =
        task {
            let server = gitServerWithForge (ScriptedRunner()) WriteGate.None

            // "merged" is a valid forge_pr_list state but not a forge_issue_list one — issues
            // have no merged state.
            match! Catalog.callTool server "forge_issue_list" (argsOf """{"state":"merged"}""") with
            | Error e -> Assert.That(e.Message, Does.Contain "unknown state")
            | Ok _ -> Assert.Fail "forge_issue_list must reject a state it doesn't model"
        }

    [<Test>]
    member _.CallToolForgePrListRejectsANonPositiveLimit() : Task =
        task {
            let server = gitServerWithForge (ScriptedRunner()) WriteGate.None

            match! Catalog.callTool server "forge_pr_list" (argsOf """{"limit":0}""") with
            | Error e -> Assert.That(e.Message, Does.Contain "positive")
            | Ok _ -> Assert.Fail "forge_pr_list must reject a zero limit"

            match! Catalog.callTool server "forge_pr_list" (argsOf """{"limit":-5}""") with
            | Error e -> Assert.That(e.Message, Does.Contain "positive")
            | Ok _ -> Assert.Fail "forge_pr_list must reject a negative limit"
        }

    [<Test>]
    member _.CallToolForgeListLimitRejectsNonIntegerValues() : Task =
        task {
            let server = gitServerWithForge (ScriptedRunner()) WriteGate.None

            match! Catalog.callTool server "forge_pr_list" (argsOf """{"limit":"100"}""") with
            | Error(McpError.InvalidParams message) ->
                Assert.That(message, Does.Contain "limit")
                Assert.That(message, Does.Contain "integer")
            | Error e -> Assert.Fail $"forge_pr_list should return InvalidParams, got: {e.Message}"
            | Ok _ -> Assert.Fail "forge_pr_list must reject a string limit"
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

    [<Test>]
    member _.CallToolDispatchesRepoAnnotate() : Task =
        task {
            let tab = string (char 9)
            let sha = "0123456789abcdef0123456789abcdef01234567"

            let out =
                [ sha + " 1 1 1"
                  "author Alice Example"
                  "author-time 1700000000"
                  "author-tz +0000"
                  tab + "let x = 1" ]
                |> String.concat "\n"

            let server =
                gitServer
                    (ScriptedRunner().On([ "blame"; "--line-porcelain"; "HEAD"; "--"; "f.txt" ], Reply.Ok out))
                    WriteGate.None

            match! Catalog.callTool server "repo_annotate" (argsOf """{"path":"f.txt","rev":"HEAD"}""") with
            | Ok json -> Assert.That(json, Does.Contain "let x = 1")
            | Error e -> Assert.Fail $"dispatch failed: {e.Message}"
        }

    [<Test>]
    member _.CallToolRepoAnnotateAcceptsAnOmittedRev() : Task =
        task {
            let tab = string (char 9)
            let sha = "0123456789abcdef0123456789abcdef01234567"

            let out =
                [ sha + " 1 1 1"
                  "author Alice Example"
                  "author-time 1700000000"
                  "author-tz +0000"
                  tab + "let x = 1" ]
                |> String.concat "\n"

            let server =
                gitServer
                    (ScriptedRunner().On([ "blame"; "--line-porcelain"; "--"; "f.txt" ], Reply.Ok out))
                    WriteGate.None

            match! Catalog.callTool server "repo_annotate" (argsOf """{"path":"f.txt"}""") with
            | Ok json -> Assert.That(json, Does.Contain "let x = 1")
            | Error e -> Assert.Fail $"dispatch failed: {e.Message}"
        }

    [<Test>]
    member _.CallToolRepoAnnotateRejectsMissingArgument() : Task =
        task {
            let server = gitServer (ScriptedRunner()) WriteGate.None

            // Missing the required `path` argument is refused before the tool runs.
            match! Catalog.callTool server "repo_annotate" (argsOf "{}") with
            | Error e -> Assert.That(e.Message, Does.Contain "path")
            | Ok _ -> Assert.Fail "a missing required argument must be refused"
        }

    [<Test>]
    member _.CallToolDispatchesTheNewRepoMutations() : Task =
        task {
            // Each argv-bearing new tool is reachable through the dispatcher with its wire
            // arguments (the no-arg abort/continue are covered against a real merge in the
            // integration fixture, where their on-disk state probes have something to read).
            let runner =
                ScriptedRunner()
                    .On([ "rebase" ], Reply.Ok "")
                    .On([ "branch" ], Reply.Ok "")
                    .On([ "checkout" ], Reply.Ok "")

            let server = gitServer runner WriteGate.All

            let assertOk tool args needle =
                task {
                    match! Catalog.callTool server tool (argsOf args) with
                    | Ok json -> Assert.That(json, Does.Contain needle, tool)
                    | Error e -> Assert.Fail $"{tool} dispatch failed: {e.Message}"
                }

            do! assertOk "repo_rebase" """{"onto":"main"}""" "main"
            do! assertOk "repo_delete_branch" """{"name":"feature"}""" "feature"
            do! assertOk "repo_delete_branch" """{"name":"feature","force":true}""" "feature"
            do! assertOk "repo_rename_branch" """{"old_name":"old","new_name":"renamed"}""" "renamed"
            do! assertOk "repo_new_child" """{"reference":"main"}""" "main"
        }

    [<Test>]
    member _.CallToolNewRepoMutationsRejectMissingArguments() : Task =
        task {
            let server = gitServer (ScriptedRunner()) WriteGate.All

            let assertMissing tool args argument =
                task {
                    match! Catalog.callTool server tool (argsOf args) with
                    | Error e -> Assert.That(e.Message, Does.Contain argument, tool)
                    | Ok _ -> Assert.Fail $"{tool} must refuse a missing {argument}"
                }

            do! assertMissing "repo_rebase" "{}" "onto"
            do! assertMissing "repo_delete_branch" "{}" "name"
            do! assertMissing "repo_rename_branch" """{"old_name":"old"}""" "new_name"
            do! assertMissing "repo_new_child" "{}" "reference"
        }

    [<Test>]
    member _.DeleteBranchSchemaAdvertisesOptionalForce() =
        // `name` is required; `force` is an optional boolean (like repo_remove_worktree's force).
        let del = Catalog.all |> List.find (fun t -> t.Name = "repo_delete_branch")
        let schema = Catalog.inputSchema del
        Assert.That(schema, Does.Contain "\"force\"")
        Assert.That(schema, Does.Contain "\"required\":[\"name\"]", "force is optional")

    [<Test>]
    member _.AbortAndContinueSchemasTakeNoArguments() =
        for name in [ "repo_abort_in_progress"; "repo_continue_in_progress" ] do
            let tool = Catalog.all |> List.find (fun t -> t.Name = name)
            Assert.That(Catalog.inputSchema tool, Does.Contain "\"required\":[]", name)
