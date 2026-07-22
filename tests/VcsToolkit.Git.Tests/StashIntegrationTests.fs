module VcsToolkit.Git.StashIntegrationTests

open System.IO
open System.Threading.Tasks
open NUnit.Framework
open VcsToolkit.Git
open VcsToolkit.TestKit

/// Real-`git` integration coverage for `Git.StashList`/`StashApply`/`StashDrop` (T-124):
/// push a stash via the existing `StashPush`, list it, apply it (without dropping), then
/// drop it — checking the stash list, and the working tree, at each step. Skips (rather
/// than fails) when `git` isn't on PATH.
[<TestFixture>]
type StashIntegrationTests() =

    let requireGit () =
        try
            Raw.git "." [ "--version" ]
        with _ ->
            // git isn't on PATH (or failed to spawn) — a hermetic CI without it must skip,
            // not fail, this fixture.
            Assert.Ignore "git not available on PATH"

    [<Test>]
    member _.PushListApplyDropRoundTrip() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "stash-roundtrip"
            repo.CommitFile("a.txt", "base\n", "seed")
            repo.Write("a.txt", "dirty\n")

            let git = Git.Create()
            let aPath = Path.Combine(repo.Path, "a.txt")

            match! git.StashPush(repo.Path, false) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"stash push failed: {e}"

            // The dirty change is stashed away — the working tree is clean again.
            Assert.That(File.ReadAllText aPath, Is.EqualTo "base\n")

            match! git.StashList repo.Path with
            | Ok [ entry ] ->
                Assert.That(entry.Index, Is.EqualTo 0u)
                Assert.That(entry.Hash.Length, Is.EqualTo 40, "a real repo's stash commit is a 40-hex sha1")
                Assert.That(entry.Branch, Is.EqualTo(Some "main"))
            | Ok other -> Assert.Fail $"expected exactly one stash entry after push, got {other.Length}"
            | Error e -> Assert.Fail $"stash list failed: {e}"

            match! git.StashApply(repo.Path, 0u) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"stash apply failed: {e}"

            // Apply restores the working tree...
            Assert.That(File.ReadAllText aPath, Is.EqualTo "dirty\n")

            // ...but does NOT drop the entry (unlike `StashPop`).
            match! git.StashList repo.Path with
            | Ok afterApply -> Assert.That(afterApply.Length, Is.EqualTo 1, "apply must not drop the entry")
            | Error e -> Assert.Fail $"stash list after apply failed: {e}"

            match! git.StashDrop(repo.Path, 0u) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"stash drop failed: {e}"

            match! git.StashList repo.Path with
            | Ok afterDrop -> Assert.That(afterDrop, Is.Empty, "drop must remove the entry")
            | Error e -> Assert.Fail $"stash list after drop failed: {e}"
        }
