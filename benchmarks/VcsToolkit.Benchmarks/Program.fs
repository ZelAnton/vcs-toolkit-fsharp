namespace VcsToolkit.Benchmarks

open System.Reflection
open BenchmarkDotNet.Configs
open BenchmarkDotNet.Jobs
open BenchmarkDotNet.Running
open BenchmarkDotNet.Toolchains.InProcess.NoEmit

type InProcessConfig() =
    inherit ManualConfig()

    do
        base.AddJob(Job.Default.WithToolchain(InProcessNoEmitToolchain.Instance)) |> ignore

module Program =

    [<EntryPoint>]
    let main _ =
        BenchmarkRunner.Run(Assembly.GetExecutingAssembly(), InProcessConfig()) |> ignore
        0
