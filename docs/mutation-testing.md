# Mutation testing (investigated, not wired up)

This project deliberately does **not** run mutation testing. This document
records a verified negative result so the question does not get re-opened
without new information: as of the version tested below, Stryker.NET —
the only mature .NET mutation-testing tool — does not support F# at all.

## What was tried

- Tool: `dotnet-stryker` (Stryker.NET) **4.16.0**, the latest version on
  NuGet.org at the time of testing.
- SDK: `10.0.301` (pinned via `global.json` to `10.0.300` with
  `rollForward: latestFeature`).
- Installed as a standalone (non-manifest) tool for the investigation:
  `dotnet tool install --global dotnet-stryker --version 4.16.0`.

Two things were checked:

1. **Does Stryker's project discovery work with this repo's cross-project
   convention?** This repo wires inter-project references with
   `<Reference Include="..." />` + `AssemblySearchPaths` rather than
   `<ProjectReference>` (see the root `CLAUDE.md`/`AGENTS.md`, "Dependencies
   and project references"). Running `dotnet-stryker --test-project
   tests/VcsToolkit.Diff.Tests/VcsToolkit.Diff.Tests.fsproj` from
   `src/VcsToolkit.Diff` failed immediately during analysis:

   ```
   Could not find an assembly reference to a mutable assembly for project
   .../VcsToolkit.Diff.Tests.fsproj. Will look into project references.
   Analyzing 0 projects.
   No project found, check settings and ensure project file is not corrupted.
   ```

   Stryker's `-p|--project` option is documented as "Used to find the project
   to test **in the project references** of the test project" — its
   discovery walks the MSBuild `ProjectReference` graph exclusively.
   Passing `--project VcsToolkit.Diff.fsproj` explicitly did not change the
   outcome. There is no CLI option to point Stryker at the project under
   test without a `ProjectReference` edge.

2. **Is this only a discovery/wiring problem, or does Stryker not support F#
   at all?** To isolate the question from this repo's conventions, a
   throwaway two-project sample (`Lib.fsproj` + `Lib.Tests.fsproj`, the test
   project using a normal `<ProjectReference>` to the library) was built
   outside the repo. Stryker analyzed and resolved the project graph
   correctly this time, then failed with a hard, explicit error:

   ```
   Mutation testing of F# projects is not ready yet. No mutants will be generated.
   System.NotSupportedException: Language not supported: Fsharp
      at Stryker.Core.Initialisation.InputFileResolver.BuildSourceProjectInfo(...)
   ```

   This is a deliberate guard clause in `Stryker.Core`, not an incidental
   bug or a missing flag — Stryker.NET 4.16.0 detects the project's language
   from the `.fsproj` extension and refuses to generate mutants for it.

## Conclusion

Stryker.NET 4.16.0 does not support F# projects, independent of how source/
test projects reference each other. Per the task's own acceptance criterion,
this is recorded as a verified negative result rather than worked around:

- `dotnet-stryker` is **not** added to `.config/dotnet-tools.json`.
- No `stryker-config.json` and no `.github/workflows/mutation.yml` were
  added — there is nothing for either to drive.

If a future Stryker.NET release adds F# support (or a viable F#-focused
mutation tool appears), re-run the two checks above: first confirm the tool
can discover projects that reference each other via `<Reference Include>` +
`AssemblySearchPaths` (not just `<ProjectReference>`), since that is this
repo's convention; if it cannot, either special-case a `<ProjectReference>`
just for the mutation-testing project pairing or accept a narrower scope,
before wiring up a report-only schedule per the original task shape (targeting
`VcsToolkit.Diff`, `VcsToolkit.Git`'s `Parse.fs`/`Conflict.fs`,
`VcsToolkit.Jj`'s `Parse.fs`/`Conflict.fs`, and `VcsToolkit.CliSupport/Json.fs`,
excluding every project that spawns real `git`/`jj`/`gh`/`glab`/`tea` processes
or touches the network).
