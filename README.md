# VcsToolkit

A toolkit for automating Git, Jujutsu, and the major forges (GitHub, GitLab, Gitea) through CLI process execution.

VcsToolkit is an F# port of the Rust [vcs-toolkit-rs](https://github.com/ZelAnton/vcs-toolkit-rs)
workspace. It drives the real `git`, `jj`, `gh` (and `glab` / `tea`) command-line
tools as subprocesses rather than binding to libraries, so it stays faithful to
whatever the installed CLIs actually do. Process execution is handled by
[ProcessKit](https://github.com/ZelAnton/ProcessKit-fSharp).

**[Browse the documentation site](https://zelanton.github.io/vcs-toolkit-fsharp/)** for the
full API reference (generated from the XML doc comments of every package below) plus the
architecture and examples guides.

## Requirements

- .NET 10.0 or later
- The CLI tools you intend to drive (`git`, `jj`, `gh`, …) on `PATH`

## Packages

The toolkit is split into one package per concern, mirroring the Rust workspace.

| Package | Status | Purpose |
|---|---|---|
| `VcsToolkit.CliSupport` | ✅ available | Shared plumbing: argv injection guard, error classifiers, lock-contention retry, credential provisioning, the `ManagedClient` runner wrapper. |
| `VcsToolkit.Diff` | ✅ available | The git-format unified-diff model and parser, plus a tolerant `<tool> --version` parser. Pure, no subprocess. |
| `VcsToolkit.Git` | ✅ available | The `git` CLI client: status, branches, commit, checkout, diff/log, merge/rebase/reset, fetch/push/clone, worktrees, tags, blame, config — plus a `.At(dir)` cwd-bound view and a pure conflict-marker model (`Conflict`: `parseConflicts`/`render`/`resolve`, no subprocess). |
| `VcsToolkit.Jj` | ✅ available | The Jujutsu (`jj`) CLI client: changes/log, bookmarks, the operation log with rollback transactions, workspaces, squash/split/absorb, diff queries, and git sync — plus a `.At(dir)` cwd-bound view and the native materialized conflict model (`Conflict`). |
| `VcsToolkit.GitHub` | ✅ available | The GitHub (`gh`) CLI client: pull requests (list/view/create/merge/edit/review/checks/feedback), issues, Actions runs (list/view/watch), releases, repo view, and the REST/GraphQL escape hatch — plus a `.At(dir)` cwd-bound view. Tokens are injected as `GH_TOKEN`, never in argv. |
| `VcsToolkit.GitLab` | ✅ available | The GitLab (`glab`) CLI client: the lean merge-request lifecycle (list/view/create/merge/ready/close/comment/edit), CI/pipeline status, issues, releases, project view, and the REST/GraphQL escape hatch — plus a `.At(dir)` cwd-bound view. Tokens are injected as `GITLAB_TOKEN`, never in argv. |
| `VcsToolkit.Gitea` | ✅ available | The Gitea/Forgejo (`tea`) CLI client: the lean pull-request lifecycle (list/view/create/merge/close/comment/edit), issues (list/view/create), and release listing — plus a `.At(dir)` cwd-bound view. Authentication is ambient (`tea`'s stored logins). |
| `VcsToolkit.Core` | ✅ available | The backend-agnostic `Repo` facade over Git / Jujutsu: `Open` auto-detects git vs jj, then one handle runs whatever both tools support — branch/snapshot reads, changed files & diff stat, partial commits, fetch/push/checkout/rebase, a trace-free merge-conflict probe (`TryMerge`), in-progress merge/rebase state, and worktree management — returning plain result types. Escape hatches `.Git`/`.Jj` (raw client) and `.GitAt`/`.JjAt` (dir-bound views) reach the raw surface; only the synchronous `cleanupWorktreeBlocking` Drop-guard is intentionally not ported (`IAsyncDisposable` awaits `RemoveWorktree`). |
| `VcsToolkit.Forge` | ✅ available | The unified forge facade over GitHub / GitLab / Gitea: one `Forge` handle runs the PR/MR lifecycle all three share — auth, repo view, PR/MR list/view/create/comment/edit/merge/ready/close/checks, the flat capability map, issues, and releases — returning plain result types that don't mention which forge produced them. `ForgeKind.OfRemoteUrl` classifies the public-SaaS hosts (anti-spoofing); a few ops are `Unsupported` on Gitea (`tea` lacks the command). The gh/glab/tea analogue of `Core`'s `Repo` over git/jj. |
| `VcsToolkit.TestKit` | ✅ available | Throwaway git/jj sandboxes (and a seeded bare remote) for integration tests: a self-cleaning `TempDir`, `GitSandbox` / `JjSandbox` scenario builders, and `BareRemote` — dependency-free (no wrapper libraries, so it can be a test dependency of any without a cycle), hermetic (no host VCS config leaks in), and raising on failure. |
| `VcsToolkit.Watch` | ✅ available | Filesystem-watch a git/jj repository and emit typed state-change events. A `RepoWatcher` watches the `.git`/`.jj` state dir (and, optionally, the working tree), debounces the write burst a VCS operation makes, re-queries `Repo.Snapshot`, and diffs it against the previous state to yield typed `RepoEvent`s (`HeadMoved`, `BranchSwitched`, `BranchCreated`/`Deleted`, `WorkingCopyChanged`, upstream/ahead-behind/operation/conflict). Re-query-and-diff (not raw FS events) makes it robust to ref temp-file renames and `index.lock` churn. The foundation for prompts, status bars, and TUIs. |
| `VcsToolkit.Mcp` | ✅ available | A Model Context Protocol server exposing the toolkit's typed git/jj + forge operations as agent-callable tools. The `VcsToolkit.Mcp` library is the hermetically-testable core — `VcsMcpServer` with the `repo_*` / `forge_*` tools over `Core`/`Forge`, the `WriteGate` write policy (read tools always available, mutations gated by `--allow-write`/`--allow-tools`), the tool catalogue and dispatcher, and the CLI parser. The thin `vcs-mcp` binary (`VcsToolkit.Mcp.Server`) wires it to the `ModelContextProtocol` SDK over stdio, with a hardened git client (repo hooks/config disabled) and a per-command timeout. |

## The `vcs-mcp` MCP server

The `vcs-mcp` binary (`VcsToolkit.Mcp.Server`) ships as a **.NET global tool**, so the Model
Context Protocol server installs with a single command:

```sh
dotnet tool install --global vcs-mcp
# update it later with:   dotnet tool update    --global vcs-mcp
# remove it with:         dotnet tool uninstall --global vcs-mcp
```

It speaks MCP over stdio — an agent harness launches it via an `mcpServers` config entry. Read
tools (`repo_*` / `forge_*` queries) are always available; the mutating tools stay disabled until
you opt in, either with `--allow-write` (enable all of them) or `--allow-tools name,...` (a named
subset):

```sh
# Serve the repository at ./my-repo with every mutating tool enabled
vcs-mcp --repo ./my-repo --allow-write

# Read-only by default; force the forge to GitHub with a 60s per-command timeout
vcs-mcp --repo ./my-repo --forge github --timeout 60
```

Run `vcs-mcp --help` for the full flag list. The forge is auto-detected from the repository's
`origin` remote unless `--forge` overrides it, and the git client is hardened (repo hooks and
config disabled) so serving a repository you did not create cannot execute its hooks. The `git` /
`jj` and `gh` / `glab` / `tea` CLIs you intend to drive must be on `PATH` (see Requirements).

See [docs/mcp-server.md](docs/mcp-server.md) for the full user guide: every CLI flag, the
complete `repo_*`/`forge_*` tool reference (arguments, read/write, destructive/idempotent
semantics), the `WriteGate` write policy and per-repo write lock, forge auto-detection, and an
example `mcpServers` configuration block.

## Quick start

Install `VcsToolkit.Core`, then open a Git or Jujutsu repository. `Repo.Open` detects the
backend; `CommitPaths` accepts repository-root-relative paths and never commits an empty list.

```fsharp
open VcsToolkit.Core

let commitReadme repoDir =
    task {
        match Repo.Open repoDir with
        | Error error -> return Error error
        | Ok repo ->
            match! repo.Snapshot() with
            | Error error -> return Error error
            | Ok snapshot ->
                printfn "Current head: %A" snapshot.Head
                return! repo.CommitPaths([ "README.md" ], "Document the quick start")
    }
```

See [the examples cookbook](docs/examples.md) for repository, forge, watcher, conflict, and
credential-provider examples.

## Architecture

For the package dependency graph, what each layer is responsible for, the
design principles that repeat across the wrapper clients (CLI subprocess
driving, total/tolerant parsing, argv guards, credential provisioning, error
classification, cancellation-safe cleanup), and the escape hatches available
at each layer, see [docs/architecture.md](docs/architecture.md).

Already know the CLI command you need (`git rebase --onto`, `jj parallelize`, `gh api`) and
want to know whether it's covered by a typed method or needs the escape hatch? See
[docs/command-index.md](docs/command-index.md) — a reverse index, one row per typed method and
the exact subcommand/flags it runs, plus each wrapper's "not modeled" list.

## Building from source

`VcsToolkit` restores every dependency — ProcessKit (the runtime process-execution
layer) and, for the test projects, its split-out `ProcessKit.Testing` doubles — from
nuget.org. No extra feeds or setup are needed.

```sh
dotnet tool restore        # restores Fantomas + the fsharp-analyzers runner
dotnet build VcsToolkit.slnx
dotnet test  VcsToolkit.slnx
```

Two quality gates run in CI and can be run locally with a single command each:

```sh
dotnet fantomas --check src tests   # F# formatting gate (CI's `format` job)
pwsh scripts/run-analyzers.ps1      # F# static-analysis gate (CI's `analyzers` job)
```

`scripts/run-analyzers.ps1` runs the [Ionide.Analyzers](https://github.com/ionide/ionide-analyzers)
rule set (via the pinned `fsharp-analyzers` tool) over every `src/` project and fails on any
Warning/Error finding — the only F#-class static analysis available, since CodeQL has no F# support.

## Publishing status

**Inter-package dependencies are now declared.** Because cross-project references use
`Reference` + `AssemblySearchPaths` (per the repo conventions) rather than
`ProjectReference`, `dotnet pack` cannot see the sibling dependencies. So a post-pack
target ([`Directory.Build.targets`](Directory.Build.targets)) rewrites each packed
`.nuspec` to add its `VcsToolkit.*` siblings as NuGet dependencies at the build's version
— derived from the `@(Reference)` set, so it stays in sync automatically. A consumer of
`VcsToolkit.Git` now transitively restores `VcsToolkit.CliSupport` / `VcsToolkit.Diff`;
the facades declare their backends (`Core` → `Git`/`Jj` (+ `CliSupport`/`Diff`), `Forge`
→ `GitHub`/`GitLab`/`Gitea`, `Watch` → `Core`, `Mcp` → `Core`/`Forge`).
`VcsToolkit.TestKit` is self-contained (no sibling references).

**ProcessKit and `ProcessKit.Testing` are both on nuget.org** (pinned at 2.4.0), so a consumer of
any `VcsToolkit.*` package restores its `ProcessKit (>= 2.4.0)` runtime dependency cleanly — the
packages are ready to publish. The split-out `ScriptedRunner` / `Reply` test doubles now restore
from the published **`ProcessKit.Testing`** package too — a **test-only** dependency that never
reaches the published `VcsToolkit.*` packages, so it does not affect consumers. Nothing is
vendored and there is no local NuGet feed.

### ProcessKit 2.4.0 compatibility

The [upstream 2.4.0 changelog](https://github.com/ZelAnton/ProcessKit-fSharp/blob/v2.4.0/CHANGELOG.md)
was reviewed. `ManagedClient` only constructs `Command` values and invokes the `JobRunner` through
the `IProcessRunner` capture/parse verbs; it does not use `RunningProcess`, `ProcessGroup`, hosted
processes, supplementary groups, inherited stdin, profiling, or readiness probes. Its only stdin
sources (`Stdin.Empty` and `Stdin.FromBytes`) are repeatable, so the retry/supervision guard is also
inapplicable. Tests use `ScriptedRunner`/`Reply` only: there are no record/replay cassettes or
cwd-sensitive matches, and no `WaitForAsync`/`WaitForPortAsync` calls. Thus the 2.4.0 additions and
the pid-reuse, cgroup, pipe-teardown, and timeout-reporting fixes do not require source or test
changes here; the version update is internal, so it has no separate VcsToolkit changelog entry.

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for the version history.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for build/test instructions and
conventions. To report a security issue, follow [SECURITY.md](SECURITY.md) —
please do not open a public issue.

## License

This project is licensed under the [MIT License](LICENSE).
