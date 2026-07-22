module VcsToolkit.Git.CleanIntegrationTests

open System.IO
open System.Threading.Tasks
open NUnit.Framework
open VcsToolkit.Git
open VcsToolkit.TestKit

/// Real-`git` integration coverage for `Git.Clean` (T-125): an untracked file survives a
/// `DryRun` clean (git only reports what it *would* remove) and is reported back as a
/// `CleanEntry`. Skips (rather than fails) when `git` isn't on PATH.
[<TestFixture>]
type CleanIntegrationTests() =

    let requireGit () =
        try
            Raw.git "." [ "--version" ]
        with _ ->
            // git isn't on PATH (or failed to spawn) — a hermetic CI without it must skip,
            // not fail, this fixture.
            Assert.Ignore "git not available on PATH"

    [<Test>]
    member _.DryRunReportsUntrackedFileWithoutRemovingIt() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "clean-dry-run"
            repo.CommitFile("tracked.txt", "kept\n", "seed")
            repo.Write("junk.txt", "untracked\n")

            let git = Git.Create()
            let junkPath = Path.Combine(repo.Path, "junk.txt")

            Assert.That(File.Exists junkPath, Is.True, "precondition: the untracked file exists")

            match! git.Clean(repo.Path, Clean.Create().WithDryRun()) with
            | Ok entries ->
                Assert.That(entries.Length, Is.EqualTo 1, "only the untracked file should be reported")
                Assert.That(entries.[0].Path, Is.EqualTo "junk.txt")
                Assert.That(entries.[0].DryRun, Is.True)
            | Error e -> Assert.Fail $"clean dry-run failed: {e}"

            // A dry run must never actually remove anything.
            Assert.That(File.Exists junkPath, Is.True, "DryRun must not delete the untracked file")
        }
