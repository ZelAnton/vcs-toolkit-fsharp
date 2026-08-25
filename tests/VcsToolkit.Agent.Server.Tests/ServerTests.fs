namespace VcsToolkit.Agent.Server.Tests

open System
open System.IO
open System.Reflection
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open VcsToolkit.Agent

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
    member _.``Declared future operation returns structured unsupported``() =
        let result = Main.run [| "ci"; "status" |]
        Assert.That(result.ExitCode, Is.EqualTo 20)
        Assert.That(result.Stderr, Is.EqualTo "vcs-agent: unsupported\n")

        use document = JsonDocument.Parse result.Stdout
        let root = document.RootElement
        Assert.That(root.GetProperty("operation").GetString(), Is.EqualTo "ci.status")
        Assert.That(root.GetProperty("fallbackReason").GetString(), Is.EqualTo "operation-not-implemented")

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
