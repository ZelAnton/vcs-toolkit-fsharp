namespace VcsToolkit.Benchmarks

open System
open System.IO
open BenchmarkDotNet.Attributes

module private Fixtures =

    let read name =
        Path.Combine(AppContext.BaseDirectory, "fixtures", name)
        |> File.ReadAllText

    let readNulDelimited name =
        (read name)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\n', char 0)

[<MemoryDiagnoser>]
type DiffBenchmarks() =
    let mutable input = ""

    [<GlobalSetup>]
    member _.LoadFixture() = input <- Fixtures.read "large-diff.txt"

    [<Benchmark>]
    member _.ParseDiff() =
        VcsToolkit.Diff.Parse.parseDiff input |> ignore

[<MemoryDiagnoser>]
type GitPorcelainBenchmarks() =
    let mutable porcelain = ""
    let mutable conflicts = ""

    [<GlobalSetup>]
    member _.LoadFixtures() =
        porcelain <- Fixtures.readNulDelimited "porcelain-v2-large.txt"
        conflicts <- Fixtures.read "git-conflicts.txt"

    [<Benchmark>]
    member _.ParsePorcelainV2() =
        VcsToolkit.Git.GitParse.parsePorcelainV2 porcelain |> ignore

    [<Benchmark>]
    member _.ParseConflicts() =
        VcsToolkit.Git.Conflict.parseConflicts conflicts |> ignore

[<MemoryDiagnoser>]
type JjBenchmarks() =
    let mutable templateOutput = ""
    let mutable conflicts = ""

    [<GlobalSetup>]
    member _.LoadFixtures() =
        templateOutput <- Fixtures.read "jj-template-large.txt"
        conflicts <- Fixtures.read "jj-conflicts.txt"

    [<Benchmark>]
    member _.ParseTemplateOutput() =
        VcsToolkit.Jj.JjParse.parseChanges templateOutput |> ignore

    [<Benchmark>]
    member _.ParseConflicts() =
        VcsToolkit.Jj.Conflict.parseConflicts conflicts |> ignore
