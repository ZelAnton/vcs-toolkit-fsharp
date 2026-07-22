# Parser benchmarks

`VcsToolkit.Benchmarks` is a manual BenchmarkDotNet tool for before/after parser comparisons. It measures the unified-diff parser, Git porcelain-v2 and conflict parsers, and jj template and conflict parsers against deterministic fixtures committed under `benchmarks/VcsToolkit.Benchmarks/fixtures/`.

Benchmarks are not a CI gate. Hosted runners are noisy, so timing changes there are not reliable enough to block merges. Run them locally when changing parser hot paths and compare results on the same machine.

From the repository root:

```powershell
cd benchmarks/VcsToolkit.Benchmarks
dotnet run -c Release
```

Run one benchmark group with BenchmarkDotNet's filter option:

```powershell
dotnet run -c Release -- --filter "*DiffBenchmarks*"
dotnet run -c Release -- --filter "*GitPorcelainBenchmarks*"
dotnet run -c Release -- --filter "*JjBenchmarks*"
```

The benchmark project is included in the solution for deterministic build ordering, but it is non-packable and `.github/workflows/ci.yml` does not run it.
