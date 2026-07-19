module VcsToolkit.TestKit.Tests

open System
open System.IO
open NUnit.Framework
open VcsToolkit.TestKit

/// Whether a probe (a `<binary> --version` call) runs without raising — i.e. the binary is
/// on PATH.
let private binaryAvailable (probe: unit -> unit) : bool =
    try
        probe ()
        true
    with _ ->
        // the binary isn't on PATH (or failed to spawn) — the guarded test can't run.
        false

let private requireBinary (name: string) (probe: unit -> unit) =
    if not (binaryAvailable probe) then
        let message = $"{name} not available on PATH"

        if name = "jj" && Environment.GetEnvironmentVariable "REQUIRE_JJ" = "1" then
            Assert.Fail $"REQUIRE_JJ=1 but {message}"
        else
            Assert.Ignore message

// ---------------------------------------------------------------------------
// TempDir — hermetic (needs no binary)
// ---------------------------------------------------------------------------

[<TestFixture>]
type TempDirTests() =

    [<Test>]
    member _.UniqueAndRemovedOnDispose() =
        let a = new TempDir("unique")
        let b = new TempDir("unique")

        try
            Assert.That(a.Path, Is.Not.EqualTo b.Path, "two temp dirs never collide")
            Assert.That(Directory.Exists a.Path, Is.True)
            Assert.That(Directory.Exists b.Path, Is.True)

            let kept = a.Path
            (a :> IDisposable).Dispose()
            Assert.That(Directory.Exists kept, Is.False, "removed on dispose")
        finally
            (b :> IDisposable).Dispose()

    [<Test>]
    member _.PathIsUnderTempAndTagged() =
        use dir = new TempDir("tagme")
        Assert.That(Path.GetFileName dir.Path, Does.StartWith "vcs-testkit-tagme-")
        Assert.That(dir.Path, Does.StartWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)))

// ---------------------------------------------------------------------------
// GitSandbox / BareRemote — require the git binary (present on CI runners)
// ---------------------------------------------------------------------------

[<TestFixture>]
type GitSandboxTests() =

    [<Test>]
    member _.BuildsScenarios() =
        requireBinary "git" (fun () -> Raw.git "." [ "--version" ])
        use repo = GitSandbox.Init "sandbox"
        repo.CommitFile("a.txt", "one\n", "first")
        repo.Branch "feature"
        repo.Checkout "feature"
        repo.CommitFile("sub/b.txt", "two\n", "second")

        let head = repo.RevParse "HEAD"
        Assert.That(head.Length, Is.EqualTo 40, "rev-parse yields a full hash")
        Assert.That(head, Is.Not.EqualTo(repo.RevParse "main"), "feature has diverged from main")

    [<Test>]
    member _.HasNoLeakedHooks() =
        requireBinary "git" (fun () -> Raw.git "." [ "--version" ])
        use repo = GitSandbox.Init "hooks"
        repo.CommitFile("a.txt", "one\n", "first")
        let hooks = Path.Combine(repo.Path, ".git", "hooks")

        let enabled =
            if Directory.Exists hooks then
                // git ships `*.sample` hooks (inert); only non-sample files run.
                Directory.GetFiles hooks
                |> Array.filter (fun f -> not (f.EndsWith(".sample", StringComparison.Ordinal)))
            else
                [||]

        Assert.That(enabled, Is.Empty, "sandbox should have no live hooks")

    [<Test>]
    member _.BareRemoteSeedsAndFetches() =
        requireBinary "git" (fun () -> Raw.git "." [ "--version" ])
        use repo = GitSandbox.Init "local"
        repo.CommitFile("a.txt", "one\n", "first")
        use remote = BareRemote.Seeded "origin"
        repo.Git [ "remote"; "add"; "origin"; remote.Url ]
        repo.Git [ "fetch"; "-q"; "origin" ]

        // The seed commit is now fetchable through the tracking ref.
        Assert.That((repo.RevParse "origin/main").Length, Is.EqualTo 40, "seed commit fetched")

// ---------------------------------------------------------------------------
// JjSandbox — requires the jj binary (skipped locally when it is unavailable)
// ---------------------------------------------------------------------------

[<TestFixture>]
type JjSandboxTests() =

    [<Test>]
    member _.BuildsScenarios() =
        requireBinary "jj" (fun () -> Raw.jj "." [ "--version" ])
        use repo = JjSandbox.Init "sandbox"
        // The colocated jj repo has its state dir.
        Assert.That(Directory.Exists(Path.Combine(repo.Path, ".jj")), Is.True, "jj init created .jj")

        // A full scenario builds without raising (each step is a real jj command).
        repo.Write("a.txt", "one\n")
        repo.Describe "base"
        repo.Bookmark "mark"
        repo.NewChange "next"
        Assert.Pass "jj scenario built without error"

// ---------------------------------------------------------------------------
// Construction failure must not leak the temp dir (only forceable when jj is
// absent, so it skips wherever jj is installed).
// ---------------------------------------------------------------------------

[<TestFixture>]
type ConstructionFailureTests() =

    [<Test>]
    member _.FailedConstructionDisposesTheTempDir() =
        if binaryAvailable (fun () -> Raw.jj "." [ "--version" ]) then
            Assert.Ignore "jj is present, so JjSandbox.Init can't be forced to fail mid-construction"

        let tag = $"leak{Guid.NewGuid():N}"

        let matching () =
            Directory.GetDirectories(Path.GetTempPath(), $"vcs-testkit-{tag}-*")

        let mutable raised = false

        try
            (JjSandbox.Init tag :> IDisposable).Dispose()
        with _ ->
            raised <- true

        Assert.That(raised, Is.True, "Init must raise (fail loudly) when jj is absent")
        Assert.That(matching (), Is.Empty, "a failed construction must leave no temp dir behind")
