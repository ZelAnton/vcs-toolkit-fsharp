# Contributing to VcsToolkit

Thanks for your interest in improving **VcsToolkit**.

## Prerequisites

- .NET 10 SDK (the exact band is pinned in [`global.json`](global.json)).
- Local tools restored once per clone (`dotnet tool restore`) — this installs
  [Fantomas](https://fsprojects.github.io/fantomas/), the F# formatter.
- Optional: PowerShell 7+ and Docker/Rancher Desktop to run the Linux test
  helper (`scripts/test-linux.ps1`).
- Optional: PowerShell 7+ to build the [documentation site](https://zelanton.github.io/vcs-toolkit-fsharp/)
  locally (`scripts/build-docs.ps1`, or `-Watch` for a live-reloading preview) —
  see [.github/workflows/docs.yml](.github/workflows/docs.yml) for how it is published.

## Build and test

```sh
dotnet tool restore
dotnet build VcsToolkit.slnx
dotnet test  VcsToolkit.slnx
```

The build treats **warnings as errors**, so a clean local build is required
before opening a pull request. Run a single test with:

```sh
dotnet test VcsToolkit.slnx --filter "FullyQualifiedName~TestMethodName"
```

## Conventions

- **Formatting** is governed by [Fantomas](https://fsprojects.github.io/fantomas/),
  this repo's style authority (the F# compiler does not enforce `.editorconfig`
  style the way Roslyn does for C#). F# source is indented with **spaces, not
  tabs** — the compiler rejects tabs. Check before pushing:
  ```sh
  dotnet fantomas --check src tests
  ```
  CI fails on unformatted F#. Do not reformat code you are not changing.
- **Compile order matters.** F# resolves declarations top-to-bottom; the
  `<Compile Include="..." />` order in the `.fsproj` is the dependency order, not
  cosmetic. Insert a new file after everything it depends on.
- **Dependencies** use Central Package Management — declare versions only in
  [`Directory.Packages.props`](Directory.Packages.props); `PackageReference`
  items carry no `Version`.
- **Cross-project references** use `Reference` + `AssemblySearchPaths`, never
  `ProjectReference`. Build order comes from `BuildDependency` in the `.slnx`.
- See [`AGENTS.md`](AGENTS.md) for the full, authoritative set of conventions
  (exception-handling style, comments, architecture).

## Changelog

Every user-visible change ships its [`CHANGELOG.md`](CHANGELOG.md) entry in the
same change set, under `## [Unreleased]`. Write the bullet for a consumer of the
library, not the implementer. Pure internal refactors are exempt.

## Pull requests

- Keep changes focused; unrelated cleanups belong in their own PR.
- Ensure CI (YAML lint, Fantomas formatting, and build/test on Linux, Windows,
  and macOS) passes.
- Fill in the pull-request checklist.
