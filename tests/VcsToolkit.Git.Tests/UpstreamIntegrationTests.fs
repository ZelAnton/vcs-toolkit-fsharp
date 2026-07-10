module VcsToolkit.Git.UpstreamIntegrationTests

open System.Threading.Tasks
open NUnit.Framework
open VcsToolkit.Git
open VcsToolkit.TestKit

/// Real-`git` integration coverage for `Git.Upstream` (T-005): the surrounding unit tests
/// in `GitTests.fs` drive a `ScriptedRunner`, but the whole point here is the *actual*
/// exit codes `symbolic-ref`/`rev-parse @{u}` produce for each failure mode — a fake
/// runner can't tell us those are real. Skips (rather than fails) when `git` isn't on PATH.
[<TestFixture>]
type UpstreamTests() =

    let requireGit () =
        try
            Raw.git "." [ "--version" ]
        with _ ->
            // git isn't on PATH (or failed to spawn) — a hermetic CI without it must skip,
            // not fail, this fixture.
            Assert.Ignore "git not available on PATH"

    [<Test>]
    member _.ConfiguredUpstreamReturnsSomeUpstream() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "upstream-set"
            repo.CommitFile("a.txt", "one\n", "first")
            // A registered remote plus a remote-tracking ref plus branch config is exactly what
            // a real `fetch`/`push -u` leaves behind — no actual network needed to exercise the
            // success path (`--set-upstream-to` refuses a tracking ref under an unregistered
            // remote name, so `remote add` is required even though nothing is ever fetched).
            repo.Git [ "remote"; "add"; "origin"; "https://example.invalid/repo.git" ]
            repo.Git [ "update-ref"; "refs/remotes/origin/main"; "HEAD" ]
            repo.Git [ "branch"; "--set-upstream-to=origin/main"; "main" ]

            let git = Git.Create()

            match! git.Upstream repo.Path with
            | Ok(Some upstream) -> Assert.That(upstream, Is.EqualTo "origin/main")
            | Ok None -> Assert.Fail "expected a configured upstream, got None"
            | Error e -> Assert.Fail $"Upstream failed: {e}"
        }

    [<Test>]
    member _.BranchWithoutUpstreamReturnsOkNone() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "upstream-none"
            repo.CommitFile("a.txt", "one\n", "first")

            let git = Git.Create()

            match! git.Upstream repo.Path with
            | Ok None -> ()
            | Ok(Some upstream) -> Assert.Fail $"expected no upstream, got Some \"{upstream}\""
            | Error e -> Assert.Fail $"Upstream failed: {e}"
        }

    [<Test>]
    member _.DetachedHeadReturnsError() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "upstream-detached"
            repo.CommitFile("a.txt", "one\n", "first")
            repo.Git [ "checkout"; "-q"; "--detach"; "HEAD" ]

            let git = Git.Create()

            match! git.Upstream repo.Path with
            | Error _ -> ()
            | Ok result -> Assert.Fail $"expected an Error on a detached HEAD, got Ok {result}"
        }

    [<Test>]
    member _.DirectoryOutsideRepositoryReturnsError() : Task =
        task {
            requireGit ()
            use dir = new TempDir("upstream-outside")

            let git = Git.Create()

            match! git.Upstream dir.Path with
            | Error _ -> ()
            | Ok result -> Assert.Fail $"expected an Error outside a repository, got Ok {result}"
        }

// Two criteria cases are intentionally not covered by a dedicated real-`git` test here:
//
// - A `rev-parse --abbrev-ref --symbolic-full-name @{u}` exit code other than 0/128 (the
//   task's "код 1" case): every fatal failure mode probed against real git — no upstream
//   configured, unborn HEAD with no upstream, and an upstream branch not stored as a
//   remote-tracking branch — exits 128, not 1. No reproducible real-git scenario yields a
//   different non-zero/non-128 code for this exact command, so there is nothing distinct
//   to script; the `_ -> ProcessResult.ensureSuccess` fallback path is shared code already
//   exercised by the sibling `Some _` branches elsewhere in `Git.fs` (e.g. `ConfigGet`,
//   `CurrentBranch`) and by `CliSupport.Tests`.
// - A timeout / no-exit-code outcome: reproducing a real timer-driven timeout here would be
//   flaky by construction. The `None` branch's `ProcessResult.ensureSuccess` fallback is the
//   same code path `CurrentBranch`/`RemoteHeadBranch`/`ConfigGet` already share, and is
//   covered directly (without a real timer) by `ProcessKit`/`CliSupport.Tests`'s existing
//   `ProcessError.Timeout` coverage.
