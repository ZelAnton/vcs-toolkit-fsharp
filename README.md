# VcsToolkit

A toolkit for automating Git, Jujutsu, and the major forges (GitHub, GitLab, Gitea) through CLI process execution.

VcsToolkit is an F# port of the Rust [vcs-toolkit-rs](https://github.com/ZelAnton/vcs-toolkit-rs)
workspace. It drives the real `git`, `jj`, `gh` (and `glab` / `tea`) command-line
tools as subprocesses rather than binding to libraries, so it stays faithful to
whatever the installed CLIs actually do. Process execution is handled by
[ProcessKit](https://github.com/ZelAnton/ProcessKit-fSharp).

## Requirements

- .NET 10.0 or later
- The CLI tools you intend to drive (`git`, `jj`, `gh`, …) on `PATH`

## Packages

The toolkit is split into one package per concern, mirroring the Rust workspace.

| Package | Status | Purpose |
|---|---|---|
| `VcsToolkit.CliSupport` | ✅ available | Shared plumbing: argv injection guard, error classifiers, lock-contention retry, credential provisioning, the `ManagedClient` runner wrapper. |
| `VcsToolkit.Diff` | ✅ available | The git-format unified-diff model and parser, plus a tolerant `<tool> --version` parser. Pure, no subprocess. |
| `VcsToolkit.Git` | ✅ available | The `git` CLI client (the cwd-bound view and the conflict-marker model are still pending). |
| `VcsToolkit.Jj` | ✅ available | The Jujutsu (`jj`) CLI client: changes/log, bookmarks, the operation log with rollback transactions, workspaces, squash/split/absorb, diff queries, and git sync (the cwd-bound view and the native conflict model are still pending). |
| `VcsToolkit.GitHub` | ✅ available | The GitHub (`gh`) CLI client: pull requests (list/view/create/merge/edit/review/checks/feedback), issues, Actions runs (list/view/watch), releases, repo view, and the REST/GraphQL escape hatch. Tokens are injected as `GH_TOKEN`, never in argv (the cwd-bound view is still pending). |
| `VcsToolkit.GitLab` | ✅ available | The GitLab (`glab`) CLI client: the lean merge-request lifecycle (list/view/create/merge/ready/close/comment/edit), CI/pipeline status, issues, releases, project view, and the REST/GraphQL escape hatch. Tokens are injected as `GITLAB_TOKEN`, never in argv (the cwd-bound view is still pending). |
| `VcsToolkit.Gitea` | ✅ available | The Gitea/Forgejo (`tea`) CLI client: the lean pull-request lifecycle (list/view/create/merge/close/comment/edit), issues (list/view/create), and release listing. Authentication is ambient (`tea`'s stored logins); the cwd-bound view is still pending. |
| `VcsToolkit.Core` | ✅ available | The backend-agnostic `Repo` facade over Git / Jujutsu: `Open` auto-detects git vs jj, then one handle runs whatever both tools support — branch/snapshot reads, changed files & diff stat, partial commits, fetch/push/checkout/rebase, a trace-free merge-conflict probe (`TryMerge`), in-progress merge/rebase state, and worktree management — returning plain result types. Escape hatches (`.Git`/`.Jj`) reach the raw client; the dir-dropped `GitAt`/`JjAt` views and the blocking cleanup helper are still pending. |
| `VcsToolkit.Forge` | ✅ available | The unified forge facade over GitHub / GitLab / Gitea: one `Forge` handle runs the PR/MR lifecycle all three share — auth, repo view, PR/MR list/view/create/comment/edit/merge/ready/close/checks, the flat capability map, issues, and releases — returning plain result types that don't mention which forge produced them. `ForgeKind.OfRemoteUrl` classifies the public-SaaS hosts (anti-spoofing); a few ops are `Unsupported` on Gitea (`tea` lacks the command). The gh/glab/tea analogue of `Core`'s `Repo` over git/jj. |
| `VcsToolkit.TestKit` | ✅ available | Throwaway git/jj sandboxes (and a seeded bare remote) for integration tests: a self-cleaning `TempDir`, `GitSandbox` / `JjSandbox` scenario builders, and `BareRemote` — dependency-free (no wrapper libraries, so it can be a test dependency of any without a cycle), hermetic (no host VCS config leaks in), and raising on failure. |
| `VcsToolkit.Watch` | ✅ available | Filesystem-watch a git/jj repository and emit typed state-change events. A `RepoWatcher` watches the `.git`/`.jj` state dir (and, optionally, the working tree), debounces the write burst a VCS operation makes, re-queries `Repo.Snapshot`, and diffs it against the previous state to yield typed `RepoEvent`s (`HeadMoved`, `BranchSwitched`, `BranchCreated`/`Deleted`, `WorkingCopyChanged`, upstream/ahead-behind/operation/conflict). Re-query-and-diff (not raw FS events) makes it robust to ref temp-file renames and `index.lock` churn. The foundation for prompts, status bars, and TUIs. |
| `VcsToolkit.Mcp` | ✅ available | A Model Context Protocol server exposing the toolkit's typed git/jj + forge operations as agent-callable tools. The `VcsToolkit.Mcp` library is the hermetically-testable core — `VcsMcpServer` with the `repo_*` / `forge_*` tools over `Core`/`Forge`, the `WriteGate` write policy (read tools always available, mutations gated by `--allow-write`/`--allow-tools`), the tool catalogue + `Catalog.callTool` dispatcher, and the CLI parser. The thin `vcs-mcp` binary (`VcsToolkit.Mcp.Server`) wires it to the `ModelContextProtocol` SDK over stdio, with a hardened git client (repo hooks/config disabled) and a per-command timeout. |

## Building from source

`VcsToolkit` consumes ProcessKit 2.0.0, whose published feed currently tops out at
1.3.2, so the 2.0.0 `.nupkg` is vendored under [`local-packages/`](local-packages)
and exposed through a local NuGet source in [`nuget.config`](nuget.config). No extra
setup is needed — restore picks it up automatically.

```sh
dotnet tool restore        # restores Fantomas (the F# formatter)
dotnet build VcsToolkit.slnx
dotnet test  VcsToolkit.slnx
```

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

**One remaining blocker before the first nuget.org release:** **ProcessKit 2.0.0 is not on
nuget.org** (it tops out at 1.3.2 there). Every `VcsToolkit.*` package declares a
`ProcessKit (>= 2.0.0)` dependency, so external consumers cannot restore them until that
version is published upstream. Local build, test, and CI are unaffected — a fresh clone
restores ProcessKit 2.0.0 from the committed `local-packages/` feed.

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for the version history.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for build/test instructions and
conventions. To report a security issue, follow [SECURITY.md](SECURITY.md) —
please do not open a public issue.

## License

This project is licensed under the [MIT License](LICENSE).
