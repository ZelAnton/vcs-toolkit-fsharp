# VcsToolkit

A toolkit for automating Git, Jujutsu, and GitHub through CLI process execution.

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
| `VcsToolkit.Core` | 🚧 planned | The backend-agnostic `Repo` facade over Git / Jujutsu. |
| `VcsToolkit.Forge` | 🚧 planned | The unified forge facade over GitHub / GitLab / Gitea. |
| `VcsToolkit.Watch` / `VcsToolkit.TestKit` / `VcsToolkit.Mcp` | 🚧 planned | File watcher, test utilities, and the Model Context Protocol server. |

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

## Publishing status (not yet release-ready)

These packages are **not yet publishable to nuget.org**, for two reasons tracked
to resolve before the first release:

1. **ProcessKit 2.0.0 is not on nuget.org** (it tops out at 1.3.2 there). Every
   `VcsToolkit.*` package declares a `ProcessKit (>= 2.0.0)` dependency, so they
   cannot be installed externally until that version is published upstream.
2. **Inter-package dependencies are not yet declared.** Because cross-project
   references use `Reference` + `AssemblySearchPaths` (per the repo conventions)
   rather than `ProjectReference`, `dotnet pack` does not record sibling
   dependencies: the `VcsToolkit.Git` / `VcsToolkit.Jj` / `VcsToolkit.GitHub` /
   `VcsToolkit.GitLab` / `VcsToolkit.Gitea` packages do not yet declare their dependency
   on `VcsToolkit.CliSupport` / `VcsToolkit.Diff`. This must be wired up (e.g. via a
   pack-time dependency injection, or by revisiting the reference style for the
   packaged libraries) before publishing, or an external consumer of
   `VcsToolkit.Git` would hit a missing-assembly error.

Both are publish-time concerns only — local build, test, and CI (a fresh clone
restores ProcessKit from the committed `local-packages/` feed) are unaffected.

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for the version history.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for build/test instructions and
conventions. To report a security issue, follow [SECURITY.md](SECURITY.md) —
please do not open a public issue.

## License

This project is licensed under the [MIT License](LICENSE).
