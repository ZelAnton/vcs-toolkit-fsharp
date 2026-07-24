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
dotnet fantomas --check src tests
pwsh ./scripts/run-analyzers.ps1
pwsh ./scripts/check-command-index.ps1
pwsh ./scripts/check-docs.ps1
pwsh ./scripts/build-docs.ps1
pwsh ./scripts/check-docs-output.ps1
```

The build treats **warnings as errors**, so a clean local build is required before opening a
pull request. The source checks keep the CLI command index and MCP tool reference synchronized
with their executable API/catalogue sources. The generated-output check catches broken targets
and case-sensitive fragments after fsdocs renders the site. Run a single test with:

```sh
dotnet test VcsToolkit.slnx --filter "FullyQualifiedName~TestMethodName"
```

## Adding a new capability

Adding a new git/jj/forge operation touches up to three layers — a CLI wrapper, a
backend-agnostic facade, and the MCP tool surface. See
[docs/extending.md](docs/extending.md) for the full, layer-by-layer contributor
workflow: validating a CLI's real contract before designing an API, where argv
guards and option types live, the `Unsupported`/`Supports` facade contract, and
`WriteGate`-gating an MCP tool.

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
- Follow the [architecture](docs/architecture.md) and
  [extension workflow](docs/extending.md) for the package boundaries, public API rules,
  exception-handling style, and cross-layer conventions.

## Changelog

Every user-visible change ships its [`CHANGELOG.md`](CHANGELOG.md) entry in the
same change set, under `## [Unreleased]`. Write the bullet for a consumer of the
library, not the implementer. Pure internal refactors are exempt.

## Pull requests

- Keep changes focused; unrelated cleanups belong in their own PR.
- Ensure CI (YAML lint, Fantomas formatting, and build/test on Linux, Windows,
  and macOS) passes.
- Fill in the pull-request checklist.
