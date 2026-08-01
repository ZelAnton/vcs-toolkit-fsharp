module VcsToolkit.Git.DiffRelativeIntegrationTests

open System.IO
open System.Threading.Tasks
open NUnit.Framework
open VcsToolkit.Git
open VcsToolkit.TestKit

/// Real-`git` integration coverage for T-146: every `git diff` invocation in the wrapper
/// (`ConflictedFiles`, `DiffIsEmpty`, `StagedIsEmpty`, `DiffRangeIsEmpty`, `DiffStat`,
/// `DiffText`/`Diff`) pins `--no-relative`, so a repo-local `diff.relative=true` config
/// cannot rewrite paths cwd-relative nor silently drop entries outside cwd. Each test
/// configures `diff.relative=true`, binds the wrapper to a subdirectory (`Cwd` != `Root`,
/// mirroring a `Repo.At` handle), and changes something OUTSIDE that subdirectory —
/// without the pin, git would exclude that change from the relevant diff view entirely
/// (or rewrite its path cwd-relative). Skips (rather than fails) when `git` isn't on PATH.
[<TestFixture>]
type DiffRelativeIntegrationTests() =

    let requireGit () =
        try
            Raw.git "." [ "--version" ]
        with _ ->
            // git isn't on PATH (or failed to spawn) — a hermetic CI without it must skip,
            // not fail, this fixture.
            Assert.Ignore "git not available on PATH"

    /// Create a subdirectory `sub` inside `repo`, returning its absolute path. Not itself
    /// tracked by git — it's only ever used as the wrapper's bound `dir` (cwd), matching a
    /// `Repo.At` handle whose `Cwd` is a subdirectory of `Root`.
    let subdirOf (repo: GitSandbox) =
        let sub = Path.Combine(repo.Path, "sub")
        Directory.CreateDirectory sub |> ignore
        sub

    [<Test>]
    member _.ConflictedFilesSeesConflictOutsideCwdWithDiffRelativeConfig() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "diff-relative-conflicted"
            repo.Git [ "config"; "diff.relative"; "true" ]
            repo.CommitFile("outside.txt", "base\n", "seed")
            let sub = subdirOf repo

            repo.Branch "feature"
            repo.Checkout "feature"
            repo.Write("outside.txt", "feature change\n")
            repo.AddAll()
            repo.Commit "feature change"

            repo.Checkout "main"
            repo.Write("outside.txt", "main change\n")
            repo.AddAll()
            repo.Commit "main change"

            // Both sides edited the same line — the merge conflicts by construction. A
            // non-zero exit here is the expected outcome, not a fixture failure.
            try
                repo.Git [ "merge"; "-q"; "--no-edit"; "feature" ]
            with _ ->
                ()

            let git = Git.Create()

            match! git.ConflictedFiles sub with
            | Ok paths ->
                Assert.That(paths.Length, Is.EqualTo 1, "diff.relative must not drop the conflict outside cwd")

                Assert.That(paths.[0], Is.EqualTo "outside.txt", "diff.relative must not rewrite the path cwd-relative")
            | Error e -> Assert.Fail $"ConflictedFiles failed: {e}"
        }

    [<Test>]
    member _.DiffIsEmptyReportsFalseForUnstagedChangeOutsideCwdWithDiffRelativeConfig() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "diff-relative-unstaged"
            repo.Git [ "config"; "diff.relative"; "true" ]
            repo.CommitFile("outside.txt", "base\n", "seed")
            let sub = subdirOf repo
            repo.Write("outside.txt", "dirty\n")

            let git = Git.Create()

            match! git.DiffIsEmpty sub with
            | Ok isEmpty -> Assert.That(isEmpty, Is.False, "an unstaged change outside cwd must not read as empty")
            | Error e -> Assert.Fail $"DiffIsEmpty failed: {e}"
        }

    [<Test>]
    member _.StagedIsEmptyReportsFalseForStagedChangeOutsideCwdWithDiffRelativeConfig() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "diff-relative-staged"
            repo.Git [ "config"; "diff.relative"; "true" ]
            repo.CommitFile("outside.txt", "base\n", "seed")
            let sub = subdirOf repo
            repo.Write("outside.txt", "staged\n")
            repo.AddAll()

            let git = Git.Create()

            match! git.StagedIsEmpty sub with
            | Ok isEmpty -> Assert.That(isEmpty, Is.False, "a staged change outside cwd must not read as empty")
            | Error e -> Assert.Fail $"StagedIsEmpty failed: {e}"
        }

    [<Test>]
    member _.DiffRangeIsEmptyAndDiffStatSeeChangeOutsideCwdWithDiffRelativeConfig() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "diff-relative-range"
            repo.Git [ "config"; "diff.relative"; "true" ]
            repo.CommitFile("outside.txt", "base\n", "seed")
            let sub = subdirOf repo
            repo.CommitFile("outside.txt", "changed\n", "change")

            let git = Git.Create()

            match! git.DiffRangeIsEmpty(sub, "HEAD~1..HEAD") with
            | Ok isEmpty -> Assert.That(isEmpty, Is.False, "a range change outside cwd must not read as empty")
            | Error e -> Assert.Fail $"DiffRangeIsEmpty failed: {e}"

            match! git.DiffStat(sub, "HEAD~1..HEAD") with
            | Ok stat -> Assert.That(stat.FilesChanged, Is.EqualTo 1UL, "the file outside cwd must be counted")
            | Error e -> Assert.Fail $"DiffStat failed: {e}"
        }

    [<Test>]
    member _.DiffTextReturnsRepoRelativePathForChangeOutsideCwdWithDiffRelativeConfig() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "diff-relative-text"
            repo.Git [ "config"; "diff.relative"; "true" ]
            repo.CommitFile("outside.txt", "base\n", "seed")
            let sub = subdirOf repo
            repo.Write("outside.txt", "dirty\n")

            let git = Git.Create()

            match! git.Diff(sub, DiffSpec.WorkingTree) with
            | Ok files ->
                Assert.That(files.Length, Is.EqualTo 1, "the change outside cwd must not be dropped")

                Assert.That(
                    files.[0].Path,
                    Is.EqualTo "outside.txt",
                    "the path must stay repo-relative, not rewritten cwd-relative"
                )
            | Error e -> Assert.Fail $"Diff failed: {e}"
        }
