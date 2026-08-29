namespace VcsToolkit.Agent.Server.Tests

open System
open System.IO
open System.Reflection
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open VcsToolkit.Agent
open VcsToolkit.TestKit

[<TestFixture>]
type ServerTests() =

    [<Test>]
    member _.``Probe writes one successful machine envelope to stdout``() =
        let result = Main.run [| "probe" |]
        Assert.That(result.ExitCode, Is.EqualTo 0)
        Assert.That(result.Stderr, Is.Empty)

        use document = JsonDocument.Parse result.Stdout
        let root = document.RootElement
        Assert.That(root.GetProperty("contractVersion").GetString(), Is.EqualTo Agent.ContractVersion)
        Assert.That(root.GetProperty("operation").GetString(), Is.EqualTo "probe")
        Assert.That(root.GetProperty("status").GetString(), Is.EqualTo "success")
        Assert.That(root.GetProperty("terminal").GetBoolean(), Is.True)

    [<Test>]
    member _.``CI status requires explicit identity before repository execution``() =
        let result = Main.run [| "ci"; "status" |]
        Assert.That(result.ExitCode, Is.EqualTo 22)
        Assert.That(result.Stderr, Is.EqualTo "vcs-agent: invalid-input\n")

        use document = JsonDocument.Parse result.Stdout
        let root = document.RootElement
        Assert.That(root.GetProperty("operation").GetString(), Is.EqualTo "ci.status")
        Assert.That(root.GetProperty("error").GetProperty("code").GetString(), Is.EqualTo "invalid-input")

    [<Test>]
    member _.``Publication identity flags are strict and complete before repository execution``() =
        let missing =
            Path.Combine(Path.GetTempPath(), "vcs-agent-missing-" + Guid.NewGuid().ToString("N"))

        let missingAccount =
            Main.run
                [| "publish"
                   "--repo"
                   missing
                   "--branch"
                   "feature"
                   "--remote"
                   "origin"
                   "--revision"
                   String('a', 40)
                   "--forge"
                   "github"
                   "--target"
                   "main"
                   "--title"
                   "feature" |]

        Assert.That(missingAccount.ExitCode, Is.EqualTo 22)

        let duplicateRemote =
            Main.run
                [| "ci"
                   "status"
                   "--repo"
                   missing
                   "--branch"
                   "feature"
                   "--remote"
                   "origin"
                   "--remote"
                   "upstream"
                   "--revision"
                   String('a', 40)
                   "--forge"
                   "github"
                   "--account"
                   "alice" |]

        Assert.That(duplicateRemote.ExitCode, Is.EqualTo 22)

        let complete =
            Main.run
                [| "ci"
                   "status"
                   "--repo"
                   missing
                   "--branch"
                   "feature"
                   "--remote"
                   "origin"
                   "--revision"
                   String('a', 40)
                   "--forge"
                   "github"
                   "--account"
                   "alice" |]

        Assert.That(complete.ExitCode, Is.EqualTo 23)

    [<Test>]
    member _.``Unknown command has stable invalid-input boundary``() =
        let result = Main.run [| "raw"; "status" |]
        Assert.That(result.ExitCode, Is.EqualTo 22)
        Assert.That(result.Stderr, Is.EqualTo "vcs-agent: invalid-input\n")

        use document = JsonDocument.Parse result.Stdout

        Assert.That(
            document.RootElement.GetProperty("error").GetProperty("code").GetString(),
            Is.EqualTo "invalid-input"
        )

    [<Test>]
    member _.``Probe does not mutate its working directory``() =
        let previous = Directory.GetCurrentDirectory()

        let sandbox =
            Path.Combine(Path.GetTempPath(), "vcs-agent-probe-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory sandbox |> ignore
        let sentinel = Path.Combine(sandbox, "sentinel.txt")
        File.WriteAllText(sentinel, "unchanged")

        try
            Directory.SetCurrentDirectory sandbox
            let before = Directory.GetFileSystemEntries sandbox
            let result = Main.run [| "probe" |]
            let after = Directory.GetFileSystemEntries sandbox
            Assert.That(result.ExitCode, Is.EqualTo 0)
            Assert.That((before = after), Is.True)
            Assert.That(File.ReadAllText sentinel, Is.EqualTo "unchanged")
        finally
            Directory.SetCurrentDirectory previous
            Directory.Delete(sandbox, true)

    [<Test>]
    member _.``Tool version is derived from entry assembly metadata``() =
        let expected = Main.readVersionFromAssembly (Assembly.GetEntryAssembly())
        Assert.That(Main.toolVersion (), Is.EqualTo expected)

    [<Test>]
    member _.``Output budget option preserves a valid bounded error envelope``() =
        let result =
            Main.run [| "probe"; "--output-budget"; string Agent.MinimumOutputLimitBytes |]

        Assert.That(result.ExitCode, Is.EqualTo 28)

        use document = JsonDocument.Parse result.Stdout

        Assert.That(
            document.RootElement.GetProperty("error").GetProperty("code").GetString(),
            Is.EqualTo "output-limit"
        )

    [<Test>]
    member _.``Inspect accepts an explicit repository and reports a typed backend failure``() =
        let missing =
            Path.Combine(Path.GetTempPath(), "vcs-agent-missing-" + Guid.NewGuid().ToString("N"))

        let result = Main.run [| "inspect"; "--repo"; missing |]
        Assert.That(result.ExitCode, Is.EqualTo 23)

        use document = JsonDocument.Parse result.Stdout
        Assert.That(document.RootElement.GetProperty("operation").GetString(), Is.EqualTo "inspect")
        Assert.That(document.RootElement.GetProperty("error").GetProperty("code").GetString(), Is.EqualTo "backend")

    [<Test>]
    member _.``Changes parses the selected diff view before repository execution``() =
        let missing =
            Path.Combine(Path.GetTempPath(), "vcs-agent-missing-" + Guid.NewGuid().ToString("N"))

        let result = Main.run [| "changes"; "--view"; "diff"; "--repo"; missing |]
        Assert.That(result.ExitCode, Is.EqualTo 23)

        use document = JsonDocument.Parse result.Stdout
        Assert.That(document.RootElement.GetProperty("operation").GetString(), Is.EqualTo "changes")

    [<Test>]
    member _.``Commit parses repeated exact paths and refuses incomplete requests before opening a repository``() =
        let missing =
            Path.Combine(Path.GetTempPath(), "vcs-agent-missing-" + Guid.NewGuid().ToString("N"))

        let emptyPaths = Main.run [| "commit"; "--repo"; missing; "--message"; "message" |]
        Assert.That(emptyPaths.ExitCode, Is.EqualTo 22)

        use emptyDocument = JsonDocument.Parse emptyPaths.Stdout
        Assert.That(emptyDocument.RootElement.GetProperty("operation").GetString(), Is.EqualTo "commit")

        let parsed =
            Main.run
                [| "commit"
                   "--repo"
                   missing
                   "--path"
                   "src/App.fs"
                   "--path"
                   "tests/App.Tests.fs"
                   "--message"
                   "message" |]

        Assert.That(parsed.ExitCode, Is.EqualTo 23)

        use parsedDocument = JsonDocument.Parse parsed.Stdout
        Assert.That(parsedDocument.RootElement.GetProperty("operation").GetString(), Is.EqualTo "commit")

        let missingMessage =
            Main.run [| "commit"; "--repo"; missing; "--path"; "src/App.fs" |]

        Assert.That(missingMessage.ExitCode, Is.EqualTo 22)

    [<Test; NonParallelizable>]
    member _.``Commit without an explicit repo refuses before mutating the current directory``() =
        try
            Raw.git "." [ "--version" ]
        with _ ->
            Assert.Ignore "git not available on PATH"

        use sandbox = GitSandbox.Init "agent-server-explicit-repo"
        sandbox.CommitFile("selected.txt", "before\n", "base")
        sandbox.Write("selected.txt", "dirty\n")
        let beforeRevision = sandbox.RevParse "HEAD"
        let previous = Directory.GetCurrentDirectory()

        try
            Directory.SetCurrentDirectory sandbox.Path

            let result =
                Main.run [| "commit"; "--path"; "selected.txt"; "--message"; "must refuse" |]

            Assert.That(result.ExitCode, Is.EqualTo 22)

            use document = JsonDocument.Parse result.Stdout
            Assert.That(document.RootElement.GetProperty("operation").GetString(), Is.EqualTo "commit")

            Assert.That(
                document.RootElement.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo "invalid-input"
            )

            Assert.That(sandbox.RevParse "HEAD", Is.EqualTo beforeRevision)
            Assert.That(File.ReadAllText(Path.Combine(sandbox.Path, "selected.txt")), Is.EqualTo "dirty\n")
        finally
            Directory.SetCurrentDirectory previous

    [<Test>]
    member _.``Commit executes repeated exact paths and preserves unrelated tracked and untracked dirt``() =
        try
            Raw.git "." [ "--version" ]
        with _ ->
            Assert.Ignore "git not available on PATH"

        use sandbox = GitSandbox.Init "agent-server-commit"
        sandbox.CommitFile("selected-a.txt", "before a\n", "base a")
        sandbox.CommitFile("selected-b.txt", "before b\n", "base b")
        sandbox.CommitFile("tracked-unrelated.txt", "before unrelated\n", "base unrelated")
        let sourceRevision = sandbox.RevParse "HEAD"
        sandbox.Write("selected-a.txt", "selected a\n")
        sandbox.Write("selected-b.txt", "selected b\n")
        sandbox.Write("tracked-unrelated.txt", "dirty tracked\n")
        sandbox.Write("untracked-unrelated.txt", "dirty untracked\n")

        let result =
            Main.run
                [| "commit"
                   "--repo"
                   sandbox.Path
                   "--path"
                   "selected-a.txt"
                   "--path"
                   "selected-b.txt"
                   "--message"
                   "selected only" |]

        Assert.That(result.ExitCode, Is.EqualTo 0)
        Assert.That(result.Stderr, Is.Empty)

        use document = JsonDocument.Parse result.Stdout
        let data = document.RootElement.GetProperty "data"
        Assert.That(data.GetProperty("sourceRevision").GetString(), Is.EqualTo sourceRevision)
        Assert.That(data.GetProperty("createdRevision").GetString(), Is.EqualTo(sandbox.RevParse "HEAD"))

        let includedPaths =
            data.GetProperty("paths").EnumerateArray()
            |> Seq.map _.GetString()
            |> Seq.toList

        Assert.That((includedPaths = [ "selected-a.txt"; "selected-b.txt" ]), Is.True)
        sandbox.Git [ "diff"; "--quiet"; "--"; "selected-a.txt"; "selected-b.txt" ]

        let trackedUnrelatedStillDirty =
            try
                sandbox.Git [ "diff"; "--quiet"; "--"; "tracked-unrelated.txt" ]
                false
            with _ ->
                // TestKit turns git diff's expected exit 1 for a dirty path into an exception.
                true

        Assert.That(trackedUnrelatedStillDirty, Is.True)

        Assert.That(
            File.ReadAllText(Path.Combine(sandbox.Path, "untracked-unrelated.txt")),
            Is.EqualTo "dirty untracked\n"
        )

    [<Test>]
    member _.``Request cancellation returns the stable cancellation envelope``() : Task =
        task {
            use cts = new CancellationTokenSource()
            cts.Cancel()

            let! result = Main.runWithCancellation [| "inspect"; "--repo"; Directory.GetCurrentDirectory() |] cts.Token

            Assert.That(result.ExitCode, Is.EqualTo 27)

            use document = JsonDocument.Parse result.Stdout

            Assert.That(
                document.RootElement.GetProperty("error").GetProperty("code").GetString(),
                Is.EqualTo "cancellation"
            )
        }
