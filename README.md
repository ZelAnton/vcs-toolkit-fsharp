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

> **Pre-release status:** the APIs and the `vcs-mcp` / `vcs-agent` tools are implemented in this repository, but
> the first `VcsToolkit.*` / tool packages have not been published to NuGet.org yet. Clone
> and build the repository to evaluate them today; the NuGet.org install command below becomes
> available after the first release.

## Requirements

- .NET 10.0 or later
- The CLI tools you intend to drive (`git`, `jj`, `gh`, …) on `PATH`

## Packages

The toolkit is split into one package per concern, mirroring the Rust workspace.

| Package | Source status | Purpose |
|---|---|---|
| `VcsToolkit.CliSupport` | Implemented | Shared plumbing: argv injection guard, error classifiers, lock-contention retry, credential provisioning, the `ManagedClient` runner wrapper. |
| `VcsToolkit.Diff` | Implemented | The git-format unified-diff model and parser, plus a tolerant `<tool> --version` parser. Pure, no subprocess. |
| `VcsToolkit.Git` | Implemented | The `git` CLI client: status, branches, commit, checkout, diff/log, merge/rebase/reset, fetch/push/clone, worktrees, tags, blame, config — plus a `.At(dir)` cwd-bound view and a pure conflict-marker model (`Conflict`: `parseConflicts`/`render`/`resolve`, no subprocess). |
| `VcsToolkit.Jj` | Implemented | The Jujutsu (`jj`) CLI client: changes/log, bookmarks, the operation log with rollback transactions, workspaces, squash/split/absorb, diff queries, and git sync — plus a `.At(dir)` cwd-bound view and the native materialized conflict model (`Conflict`). |
| `VcsToolkit.GitHub` | Implemented | The GitHub (`gh`) CLI client: pull requests and issues (including typed label creation and add/remove operations), typed Actions workflow definitions and runs (including exact-commit lookup), authenticated-user identity, releases, repo view, and the REST/GraphQL escape hatch — plus a `.At(dir)` cwd-bound view. Tokens are injected as `GH_TOKEN`, never in argv. |
| `VcsToolkit.GitLab` | Implemented | The GitLab (`glab`) CLI client: the lean merge-request and issue lifecycle (including typed label creation and add/remove operations), exact-revision pipelines and authenticated-user identity, releases, project view, and the REST/GraphQL escape hatch — plus a `.At(dir)` cwd-bound view. Tokens are injected as `GITLAB_TOKEN`, never in argv. |
| `VcsToolkit.Gitea` | Implemented | The Gitea/Forgejo (`tea`) CLI client: pull requests and issues (including labels on creation), releases, and a `.At(dir)` cwd-bound view. Unsupported `tea` operations such as PR/issue label mutation, PR edit, and release delete fail before spawning. Authentication is ambient (`tea`'s stored logins). |
| `VcsToolkit.Core` | Implemented | The backend-agnostic `Repo` facade over Git / Jujutsu: `Open` auto-detects git vs jj, then one handle runs whatever both tools support — branch/snapshot reads, changed files, unified working-copy diffs & diff stats, partial commits, fetch/push/checkout/rebase, a trace-free merge-conflict probe (`TryMerge`), in-progress merge/rebase state, and worktree management — returning plain result types. Escape hatches `.Git`/`.Jj` (raw client) and `.GitAt`/`.JjAt` (dir-bound views) reach the raw surface; only the synchronous `cleanupWorktreeBlocking` Drop-guard is intentionally not ported (`IAsyncDisposable` awaits `RemoveWorktree`). |
| `VcsToolkit.Forge` | Implemented | The unified forge facade over GitHub / GitLab / Gitea: one `Forge` handle exposes a common PR/MR, issue, release, authenticated-identity, and exact-revision CI surface. Backend gaps are explicit `Unsupported` results rather than silently dropped options; `ForgeKind.OfRemoteUrl` classifies the public-SaaS hosts with anti-spoofing checks. The gh/glab/tea analogue of `Core`'s `Repo` over git/jj. |
| `VcsToolkit.Agent` | Implemented (v1 checked outcomes) | The transport-neutral application contract for outcome-oriented agent workflows. It owns the versioned envelope and the shared configured-handle services for `inspect`/`changes`, exact-path checked `commit`, verified exact-revision `publish`, and exact-revision `ci status`/`ci wait`, plus bounded JSON rendering, redaction, and stable exit mapping used by both thin transports. |
| `VcsToolkit.TestKit` | Implemented | Throwaway git/jj sandboxes (and a seeded bare remote) for integration tests, plus canonical parser-shaped GitHub, GitLab, and Gitea PR/issue/release fixtures: a self-cleaning `TempDir` whose tag is sanitized to one component and verified under the canonical OS temp root, `GitSandbox` / `JjSandbox` scenario builders, and `BareRemote` — dependency-free (no wrapper libraries, so it can be a test dependency of any without a cycle), hermetic (no host VCS config leaks in), and raising on failure; sandbox writes reject rooted or escaping paths before filesystem changes. |
| `VcsToolkit.Watch` | Implemented | Filesystem-watch a git/jj repository and emit typed state-change events. A `RepoWatcher` watches the `.git`/`.jj` state dir (and, optionally, the working tree), debounces the write burst a VCS operation makes, re-queries `Repo.Snapshot`, and diffs it against the previous state to yield typed `RepoEvent`s (`HeadMoved`, `BranchSwitched`, `BranchCreated`/`Deleted`, `WorkingCopyChanged`, upstream/ahead-behind/operation/conflict). Re-query-and-diff (not raw FS events) makes it robust to ref temp-file renames and `index.lock` churn. The foundation for prompts, status bars, and TUIs. |
| `VcsToolkit.Mcp` | Implemented | A Model Context Protocol adapter over the shared `VcsToolkit.Agent` outcomes and the compatible low-level `repo_*` / `forge_*` surface. Its intent catalogue is capability-aware, so unavailable forge or write outcomes are omitted from discovery and stale calls receive structured Agent refusals. `WriteGate`, request cancellation, output budgets, and the per-repository lock cover both intent and low-level mutations. The thin `vcs-mcp` binary wires this core to the MCP SDK over stdio. |

`Git.ResolvedGitDir(dir)` always returns an absolute metadata-directory path, including when
`dir` is relative and Git reports a relative `--git-dir` for a linked worktree.

## The `vcs-agent` outcome tool

The `vcs-agent` binary (`VcsToolkit.Agent.Server`) is packaged as a **.NET global tool** over
the reusable `VcsToolkit.Agent` library. After the first NuGet release, install it with:

```sh
dotnet tool install --global vcs-agent
```

For a source build, pack the solution and install from the local artifacts directory:

```sh
dotnet pack VcsToolkit.slnx --configuration Release --output ./artifacts
dotnet tool install --global vcs-agent --version 0.1.0 --add-source ./artifacts
```

The repository also ships the standalone [`using-vcs-agent` Skill](skills/using-vcs-agent/SKILL.md).
For Codex, copy the complete `skills/using-vcs-agent` directory to
`$CODEX_HOME/skills/using-vcs-agent` (or `~/.codex/skills/using-vcs-agent` when
`CODEX_HOME` is unset); the adjacent versioned reference is required. Its narrow trigger covers
repository inspection, change review, exact-path commits, publication, exact-revision CI, and
conflict diagnosis, while ordinary source search, reading, and editing remain outside the Skill.
The tracked 20-scenario routing observation comes from an independent Codex evaluator isolated
from expected and baseline files and allowed to read only the Skill and its routed references.
It measures activation, selected interface, and fallback reason; command/outcome evidence remains
explicitly supplemental fixture data. The second blinded run matched all 20 intended routes.
SHA-256 provenance binds the observation to the exact Skill,
reference contract, and corpus bytes, so changing routing text requires another blinded run. Run
`scripts/check-vcs-agent-skill.ps1` against the built executable and
`scripts/test-vcs-agent-skill.ps1` before installing a changed copy.

The implemented v1 outcomes are `probe`, `inspect`, `changes`, checked exact-path `commit`, verified
`publish`, and exact-revision `ci status` / `ci wait`. `probe` is
deterministic and does not inspect a repository, executable, network, environment variable,
or machine-local path.

```sh
vcs-agent probe
vcs-agent probe --output-budget 4096
vcs-agent inspect --repo .
vcs-agent changes --repo . --view summary
vcs-agent changes --repo . --view diff --output-budget 16384
vcs-agent commit --repo . --path src/App.fs --path tests/App.Tests.fs --message "Update app"
vcs-agent publish --repo . --branch feature --remote origin --revision <full-sha> --forge github --account alice --target main --title "Add feature"
vcs-agent ci status --repo . --branch feature --remote origin --revision <full-sha> --forge github --account alice
vcs-agent ci wait --repo . --branch feature --remote origin --revision <full-sha> --forge github --account alice --deadline-seconds 1800 --inactivity-seconds 600
```

Every invocation emits one versioned JSON envelope on stdout. Diagnostics use stderr, and the
exit code is a stable mapping from the structured error code. The stdout budget defaults to
65,536 bytes with a 512-byte minimum; the reusable API and JSON renderer both replace an
oversized complete result with the same explicit `output-limit` envelope. `inspect` returns the
detected Git/Jujutsu backend, current revision and branch, working-copy and tracking state,
remotes, and typed forge authentication
and capability facts. `changes` returns either a path list with a separately scoped diff stat,
or parsed unified-diff hunks; on Git the path list includes untracked entries while the stat
covers the tracked `HEAD` diff. Read-only `inspect` and `changes` default an omitted `--repo` to
the process working directory; `commit` requires the option explicitly, validates a non-empty
repo-relative path set, expands a selected rename to its old/new backend pair, and preflights the
complete result budget and repository state before mutation. It delegates only to
`Repo.CommitPaths`, verifies the created revision's own observed path set, and returns canonical
repository/ref evidence. Git success additionally proves that the revision is the single direct
child of the source (or the sole root of an unborn repository); a late failure is explicitly
ambiguous and carries bounded best-effort postflight evidence without claiming an unverified
revision. Unrelated dirt remains preserved. `publish` requires explicit repository,
branch/bookmark, remote, full revision, forge/account, target, and title; it pushes only that
revision, proves the selected remote ref, pins every forge call to that remote's exact repository,
and creates or recovers one exact PR/MR only after a complete search proves the candidate's source
project and head revision. CI status/wait
prove the same published revision before consuming complete GitHub/GitLab inventories by commit
id; wait adds caller cancellation plus bounded overall and inactivity deadlines. `probe` reports
the operation-by-backend/forge matrix, so Gitea is excluded from publish and exact-revision CI
before invocation rather than producing partial success.

Short read-only calls can run directly. For a durable lifecycle, hard or idle deadline,
bounded capture, out-of-band cancellation, or nested containment, supervise the independently
packaged tool through ProcessKit-CLI:

```sh
processkit-cli run \
  --jsonl ./run/events.jsonl \
  --capture-dir ./run/capture \
  --capture-max-bytes 64k \
  --no-echo \
  -- vcs-agent inspect --repo .
processkit-cli events --file ./run/events.jsonl --validate
```

The agent envelope is retained in `run/capture/stdout.log`; ProcessKit-CLI writes its
separate terminal JSONL lifecycle to `run/events.jsonl` and preserves a child exit unchanged.
Before launching anything, the repository's proof requires JSONL schema v1, reserved runner
exit band `100-119`, and every CLI surface it uses. `scripts/install-processkit-cli.ps1`
downloads the pinned published `v0.3.3` asset with an exact SHA-256 check, while
`scripts/test-vcs-agent-processkit.ps1` exercises success/non-success exit classification,
overall and idle timeouts, detached cancellation, bounded capture, fail-closed detached
cleanup, and nested containment teardown of a live descendant identity. The proof follows the
published leader-only member enumeration on the POSIX process-group fallback while still
requiring every exact identity to disappear. ProcessKit-CLI `v0.3.3` does not publish the
nested-owner teardown guarantee needed by that fallback; the fail-closed reproducer and
upstream request are [documented here](docs/processkit-cli-nested-posix-containment-request.md).
CI runs that cross-binary proof on Windows, Linux, and Apple Silicon macOS; a missing or
incompatible published binary fails closed instead of silently skipping supervision.

The Skill is workflow guidance, not an authorization or command-policy boundary. It checks
authorization immediately before each mutation and reports classified raw-CLI fallbacks, but a
host that prohibits raw VCS mutations must enforce that restriction through its sandbox, command
policy, or approval mechanism. A denial still uses the Skill's read-only `vcs-agent` inspection
route before refusing the mutation. Gitea publication is outside the v1 matrix and therefore
requires the Skill's structured `unsupported-forge` raw-CLI fallback.

See [docs/agent-interface.md](docs/agent-interface.md) for the complete envelope, operation,
error, exit-code, output, redaction, compatibility, and direct/supervised execution contract.

## The `vcs-mcp` MCP server

The `vcs-mcp` binary (`VcsToolkit.Mcp.Server`) is packaged as a **.NET global tool**. After the
first NuGet release, the Model Context Protocol server will install with a single command:

```sh
dotnet tool install --global vcs-mcp
```

Today, build the repository and create a local tool package instead:

```sh
dotnet pack VcsToolkit.slnx --configuration Release --output ./artifacts
dotnet tool install --global vcs-mcp --version 0.1.0 --add-source ./artifacts
```

Use `dotnet tool update --global vcs-mcp` or `dotnet tool uninstall --global vcs-mcp` for an
installed copy.

It speaks MCP over stdio — an agent harness launches it via an `mcpServers` config entry. Read
tools (`repo_*` / `forge_*` queries) are always available; the mutating tools stay disabled until
you opt in, either with `--allow-write` (enable all of them) or `--allow-tools name,...` (a named
subset):

For outcome workflows, prefer the capability-aware `agent_inspect`, `agent_changes`,
`agent_commit`, `agent_publish`, `agent_ci_status`, and `agent_ci_wait` tools advertised by the
server. They return the same v1 envelopes as `vcs-agent`; the server omits an intent tool when its
forge or write capability is unavailable while retaining every compatible low-level tool.

Conflict-aware sessions can inspect materialized Git/Jujutsu conflict regions with
`repo_conflict_regions` and, when explicitly enabled, resolve them with
`repo_resolve_conflict` (Git resolutions are staged automatically).

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
backend; `CommitPaths` accepts repository-root-relative paths and never commits an empty list,
while `ConflictedFiles` returns every unresolved path in that same root-relative form even
when the handle is bound to a subdirectory with `Repo.At`.

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

The Core facade also exposes typed Jujutsu-only operation recovery through OpLog and OpUndo;
the Git backend reports Unsupported without spawning a process.

For the end-to-end trust model, typed-surface guarantees, raw-command escape hatches,
credential handling, Git/submodule hardening, and a deployment checklist for libraries and
`vcs-mcp`, see [docs/security.md](docs/security.md).

For the package dependency graph, what each layer is responsible for, the
design principles that repeat across the wrapper clients (CLI subprocess
driving, total/tolerant parsing, argv guards, credential provisioning, error
classification, cancellation-safe cleanup), and the escape hatches available
at each layer, see [docs/architecture.md](docs/architecture.md).

For the implemented v1 transport-neutral agent contract, `probe`, ProcessKit-CLI composition,
and the shared MCP outcome adapters, see [docs/agent-interface.md](docs/agent-interface.md) and
[docs/agent-interface-roadmap.md](docs/agent-interface-roadmap.md).

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

The source, API-index, and documentation consistency gates used by CI can also be run locally:

```sh
dotnet fantomas --check src tests   # F# formatting gate (CI's `format` job)
pwsh scripts/run-analyzers.ps1      # F# static-analysis gate (CI's `analyzers` job)
pwsh scripts/check-command-index.ps1
pwsh scripts/check-docs.ps1
pwsh scripts/build-docs.ps1         # Generate the Pages artifact
pwsh scripts/check-docs-output.ps1  # Validate rendered links, assets, and fragments
```

`scripts/run-analyzers.ps1` runs the [Ionide.Analyzers](https://github.com/ionide/ionide-analyzers)
rule set (via the pinned `fsharp-analyzers` tool) over every `src/` project and fails on any
Warning/Error finding — the only F#-class static analysis available, since CodeQL has no F# support.

## Publishing status

**No public `VcsToolkit.*`, `vcs-mcp`, or `vcs-agent` release exists yet.** The repository is currently at the
`0.1.0` seed version; the release workflow will publish all library packages and both global tools
together on the first release. Until then, use a source build or the local packages produced by
`dotnet pack`.

**Inter-package dependencies are now declared.** Because cross-project references use
`Reference` + `AssemblySearchPaths` (per the repo conventions) rather than
`ProjectReference`, `dotnet pack` cannot see the sibling dependencies. So a post-pack
target ([`Directory.Build.targets`](Directory.Build.targets)) rewrites each packed
`.nuspec` to add its `VcsToolkit.*` siblings as NuGet dependencies at the build's version
— derived from the `@(Reference)` set, so it stays in sync automatically. A consumer of
`VcsToolkit.Git` now transitively restores `VcsToolkit.CliSupport` / `VcsToolkit.Diff`;
the facades declare their backends (`Core` → `Git`/`Jj` (+ `CliSupport`/`Diff`), `Forge`
→ `GitHub`/`GitLab`/`Gitea`, `Watch` → `Core`, `Agent` →
`Core`/`Forge`/`CliSupport`, `Mcp` → `Agent`/`Core`/`Forge`).
`VcsToolkit.TestKit` is self-contained (no sibling references).

**ProcessKit and `ProcessKit.Testing` are both on nuget.org** (pinned at 2.10.0), so a consumer of
any `VcsToolkit.*` package restores its `ProcessKit (>= 2.10.0)` runtime dependency cleanly — the
packages are ready to publish. The split-out `ScriptedRunner` / `Reply` test doubles now restore
from the published **`ProcessKit.Testing`** package too — a **test-only** dependency that never
reaches the published `VcsToolkit.*` packages, so it does not affect consumers. Nothing is
vendored and there is no local NuGet feed.

### ProcessKit 2.10.0 compatibility

The upstream [2.10.0](https://github.com/ZelAnton/ProcessKit-fSharp/blob/v2.10.0/CHANGELOG.md)
changelog was reviewed. Its retry-backoff, extra-file-descriptor, per-run signal, configurable
soft-stop signal, CPU-time limit, and HTTP-client readiness APIs are additive and are not used by
VcsToolkit. `ManagedClient` still constructs ordinary `Command` values and invokes the `JobRunner`
through `IProcessRunner`'s capture verbs; its explicit stdin payloads remain byte-based. Progress
APIs use streamed output, and `WithOutputBudget` retains the bounded diagnostic tail for those
runs while still delivering every progress event. It does not use idle timeouts, PTY sessions,
readiness probes, supervisors, or process-group profiles.
`ManagedClient.DefaultInactivityTimeout` is an opt-in resettable stdout/stderr watchdog for
progress runs; ordinary captures remain unchanged.

The 2.10.0 fixes therefore require no source changes here. Consumers receive the corrected retry
and readiness validation plus ProcessKit's platform runtime improvements transitively, while
VcsToolkit's UTF-8 output and byte-exact stdin contracts remain unchanged.

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for the version history.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for build/test instructions and
conventions. To report a security issue, follow [SECURITY.md](SECURITY.md) —
please do not open a public issue.

## License

This project is licensed under the [MIT License](LICENSE).
