module VcsToolkit.Git.EmptyTreeOidIntegrationTests

open System.Threading.Tasks
open NUnit.Framework
open VcsToolkit.Git
open VcsToolkit.TestKit

/// Real-`git` integration coverage for `Git.EmptyTreeOid` and the unborn-`HEAD` diff path
/// it feeds (T-006): a repository under `extensions.objectFormat=sha256` has no
/// `4b825dc…` object — the SHA-1-specific hardcoded `EMPTY_TREE` constant — so diffing an
/// unborn tree there must resolve the empty-tree id via the repository's own
/// `git hash-object` instead. Skips (rather than fails) when `git`, or this build of
/// `git`'s SHA-256 support, isn't available.
[<TestFixture>]
type EmptyTreeOidTests() =

    let requireGit () =
        try
            Raw.git "." [ "--version" ]
        with _ ->
            // git isn't on PATH (or failed to spawn) — a hermetic CI without it must skip,
            // not fail, this fixture.
            Assert.Ignore "git not available on PATH"

    /// A SHA-256 sandbox, or skip: not every git distribution is built with SHA-256
    /// object-format support, so a fixture that needs one must not hard-fail on those.
    let sha256Sandbox (tag: string) : GitSandbox =
        try
            GitSandbox.InitSha256 tag
        with _ ->
            Assert.Ignore "this git build does not support --object-format=sha256"
            failwith "unreachable: Assert.Ignore always throws"

    // --- Regression: SHA-1 repositories keep resolving to the legacy constant --------

    [<Test>]
    member _.Sha1UnbornEmptyTreeOidMatchesLegacyConstant() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "empty-tree-sha1-oid"

            let git = Git.Create()

            match! git.EmptyTreeOid repo.Path with
            | Ok oid -> Assert.That(oid, Is.EqualTo "4b825dc642cb6eb9a060e54bf8d69288fbee4904")
            | Error e -> Assert.Fail $"EmptyTreeOid failed: {e}"
        }

    [<Test>]
    member _.Sha1UnbornDiffTextStillWorks() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "empty-tree-sha1-diff"
            repo.Write("a.txt", "one\n")
            repo.AddAll()

            let git = Git.Create()

            match! git.DiffText(repo.Path, DiffSpec.WorkingTree) with
            | Ok text -> Assert.That(text, Does.Contain "a.txt")
            | Error e -> Assert.Fail $"DiffText failed: {e}"
        }

    // --- SHA-256: the computed id is used, not the SHA-1 hardcoded constant ----------

    [<Test>]
    member _.Sha256UnbornEmptyTreeOidIs64HexAndNotTheSha1Constant() : Task =
        task {
            requireGit ()
            use repo = sha256Sandbox "empty-tree-sha256-oid"

            let git = Git.Create()

            match! git.EmptyTreeOid repo.Path with
            | Ok oid ->
                Assert.That(oid.Length, Is.EqualTo 64)

                let isHex =
                    oid |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

                Assert.That(isHex, Is.True, $"expected a 64-hex id, got \"{oid}\"")
                Assert.That(oid, Is.Not.EqualTo "4b825dc642cb6eb9a060e54bf8d69288fbee4904")
            | Error e -> Assert.Fail $"EmptyTreeOid failed: {e}"
        }

    /// The decisive check: if the unborn-`HEAD` diff path still used the hardcoded
    /// SHA-1 `EMPTY_TREE` constant, `git diff <that 40-hex sha1>` would fail outright in
    /// a SHA-256 repository (`fatal: bad object` / `fatal: ambiguous argument`) — that
    /// object simply does not exist here. A successful diff that correctly reports the
    /// staged file is only possible because the target is the freshly computed,
    /// repository-native `EmptyTreeOid`.
    [<Test>]
    member _.Sha256UnbornDiffTextTargetsComputedEmptyTree() : Task =
        task {
            requireGit ()
            use repo = sha256Sandbox "empty-tree-sha256-diff"
            repo.Write("a.txt", "one\n")
            repo.AddAll()

            let git = Git.Create()

            match! git.DiffText(repo.Path, DiffSpec.WorkingTree) with
            | Ok text -> Assert.That(text, Does.Contain "a.txt")
            | Error e -> Assert.Fail $"DiffText failed: {e}"
        }
