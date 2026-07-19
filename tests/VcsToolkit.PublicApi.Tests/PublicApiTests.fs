namespace VcsToolkit.PublicApi.Tests

open System
open System.IO
open System.Reflection
open NUnit.Framework
open PublicApiGenerator

/// Snapshot tests over the public API surface of every publishable src/* library.
///
/// Each library's public surface is rendered to text with PublicApiGenerator and
/// compared against a committed `.approved.txt` baseline (one file per package, under
/// `ApprovedApi/`). A mismatch fails the test and writes a `.received.txt` next to the
/// baseline. This turns SemVer / breaking-change control into an automatic gate:
///
///   * Intentional API change: review the `.received.txt` diff, replace the matching
///     `.approved.txt` with it in the SAME change set (and add a CHANGELOG entry).
///   * Accidental change (dropped member, widened visibility, changed signature):
///     the red test surfaces it before merge.
[<TestFixture>]
type PublicApiTests() =

    /// The twelve publishable libraries under src/*, kept in sync with the `<Reference>`
    /// items in the .fsproj and the BuildDependency entries in VcsToolkit.slnx.
    static member Packages: string[] =
        [| "VcsToolkit.CliSupport"
           "VcsToolkit.Diff"
           "VcsToolkit.Git"
           "VcsToolkit.Jj"
           "VcsToolkit.GitHub"
           "VcsToolkit.GitLab"
           "VcsToolkit.Gitea"
           "VcsToolkit.Core"
           "VcsToolkit.Forge"
           "VcsToolkit.Watch"
           "VcsToolkit.TestKit"
           "VcsToolkit.Mcp" |]

    /// Directory holding the committed `.approved.txt` baselines, resolved from the
    /// project directory embedded at build time so `dotnet test` reads and writes them
    /// in the source tree rather than the build output.
    static member private ApprovedDir =
        let projectDir =
            Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            |> Seq.tryPick (fun attr ->
                if attr.Key = "PublicApiTestsProjectDir" then
                    Option.ofObj attr.Value
                else
                    None)
            |> Option.defaultWith (fun () ->
                failwith
                    "PublicApiTestsProjectDir assembly metadata is missing; check the AssemblyMetadata item in VcsToolkit.PublicApi.Tests.fsproj.")

        let dir = Path.Combine(projectDir, "ApprovedApi")
        Directory.CreateDirectory dir |> ignore
        dir

    /// Normalize for a stable, cross-platform comparison: strip CR (CI runs on Linux,
    /// Windows and macOS, and .gitattributes stores the baselines as LF) and collapse
    /// trailing newlines to exactly one.
    static member private Normalize(text: string) =
        text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n') + "\n"

    [<Test>]
    [<TestCaseSource("Packages")>]
    member _.``Public API surface matches the approved snapshot``(package: string) =
        let dllPath = Path.Combine(AppContext.BaseDirectory, package + ".dll")
        Assert.That(File.Exists dllPath, Is.True, $"Library assembly not found in test output: {dllPath}")

        // IncludeAssemblyAttributes = false keeps the snapshot to the type surface only:
        // assembly-level attributes carry SourceLink / informational-version noise (a git
        // commit hash) that would churn the baseline on every commit.
        let options = ApiGeneratorOptions(IncludeAssemblyAttributes = false)

        let actual =
            PublicApiTests.Normalize(ApiGenerator.GeneratePublicApi(Assembly.LoadFrom dllPath, options))

        let approvedPath =
            Path.Combine(PublicApiTests.ApprovedDir, package + ".approved.txt")

        let receivedPath =
            Path.Combine(PublicApiTests.ApprovedDir, package + ".received.txt")

        let approved =
            if File.Exists approvedPath then
                PublicApiTests.Normalize(File.ReadAllText approvedPath)
            else
                ""

        if actual = approved then
            // Matched — drop any stale received artifact left by a previous failure.
            if File.Exists receivedPath then
                File.Delete receivedPath
        else
            // Persist the current surface next to the baseline so the change is promoted
            // by reviewing the diff and replacing the .approved file with the .received one.
            File.WriteAllText(receivedPath, actual)

            let message =
                if approved = "" then
                    $"No approved public-API snapshot for {package}. Wrote {receivedPath}; review it and rename it to {package}.approved.txt to accept the baseline."
                else
                    $"Public API of {package} differs from the approved snapshot. Review {receivedPath} against {approvedPath}; if the change is intentional, update the .approved file (and CHANGELOG) in this change set."

            Assert.Fail message
