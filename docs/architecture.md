# Architecture

This document is a map of how VcsToolkit is put together: the dependency graph
between its packages, why each layer exists, the design principles that repeat
across every wrapper client, and the seams a consumer can hook into. It
complements the [package table in the README](../README.md#packages), which
answers "what's in each package"; this document answers "why is it shaped this
way" and "how do the pieces fit together". Adding a new capability to one of
these layers? See [docs/extending.md](extending.md) for the step-by-step
contributor workflow built on top of the map below.

## Package layering

VcsToolkit ships thirteen packages under `src/`. Their build order — and their
allowed dependency direction — comes straight from the `BuildDependency`
entries in [`VcsToolkit.slnx`](../VcsToolkit.slnx); nothing here is aspirational,
it is what the solution file enforces. Laid out by layer, lowest first:

```
Layer 0   VcsToolkit.CliSupport    VcsToolkit.Diff
              |                         |
              +-----------+-------------+
                          |
Layer 1   VcsToolkit.Git       VcsToolkit.Jj
              |                     |
Layer 2   VcsToolkit.GitHub   VcsToolkit.GitLab   VcsToolkit.Gitea
              |                     |                   |
Layer 3   VcsToolkit.Core (Git+Jj)   VcsToolkit.Forge (GitHub+GitLab+Gitea)
              |                     |
Layer 4   VcsToolkit.Watch (on Core)   VcsToolkit.Mcp (on Core+Forge)
                                            |
Layer 5                            VcsToolkit.Mcp.Server

VcsToolkit.TestKit — no dependency on any of the above, and none of the above
depend on it either; it is a leaf that any test project may reference without
creating a cycle.
```

Every arrow in that diagram is a real `<Reference>` inside the corresponding
`.fsproj`, backed by an explicit `BuildDependency` in `VcsToolkit.slnx` — MSBuild
project-to-project references are deliberately not used, so the solution file
is the single place that encodes and enforces this order. A package may only
reference something strictly below it in this diagram; the five forge/VCS
backend clients (`Git`, `Jj`, `GitHub`, `GitLab`, `Gitea`) never reference each
other, and the two facades (`Core`, `Forge`) never reference one another
either — each facade only reaches down into its own family of backends. Note
that `CliSupport` and `Diff` are true siblings, not a chain: neither's
`.fsproj` references the other (`Diff` has zero `PackageReference`s beyond
`FSharp.Core`), they are consumed together starting at layer 1 — every
wrapper client needs both the process-execution plumbing and the shared
text-parsing helpers.

## What each layer is for

### `VcsToolkit.CliSupport` — the shared plumbing every wrapper is built from

Every other wrapper client in this repo (`Git`, `Jj`, `GitHub`, `GitLab`,
`Gitea`) drives its CLI through the same handful of concerns: build a
`Command`, guard its arguments, optionally retry it, optionally inject a
credential, and turn its output into a typed result without ever throwing on
malformed CLI text. Rather than re-implement (and slowly let drift) that
machinery five times, it lives once here as `ManagedClient`, a thin wrapper
around a ProcessKit `IProcessRunner` that adds opt-in retry
(`WithRetry`/`RetryPolicy`) and opt-in credential injection
(`WithCredentials`/`WithTokenEnv`) without changing any call site's shape when
neither is configured. `Classify` holds the shared `ProcessError`
classifiers (`isLockContention`, `isTransientFetchError`, `isMergeConflict`,
…) and the argv injection guard `rejectFlagLike`; `Secret`/`Credential`/
`ICredentialProvider` hold the credential model; `Wrappers` holds the small
set of helpers (`checkFlags`, `mapParse`, untrimmed-output capture,
clone-destination cleanup) that were previously copy-pasted per client with
only a `BINARY` constant differing. This package exists precisely because five
near-identical copies of this plumbing had already begun to drift before it was
factored out.

### `VcsToolkit.Diff` — the pure, total parsing core

`CliSupport`'s sibling at the bottom of the graph is the one piece of
genuinely pure, side-effect-free logic in the stack: the git-format
unified-diff model (`FileDiff`/`Hunk`/`ChangeKind`/`DiffStat`) and its parser
(`parseDiff`), plus a tolerant `<tool> --version` banner parser
(`parseDottedVersion`). It also hosts `TextParse`, the low-level total
text/number helpers (`linesOf`, `splitInclusive`, `parseUInt64Or0`) that the
diff parser and the git/jj CLI wrappers both need — kept in one place so the
near-duplicate copies these two call sites had accumulated don't drift again.
`Diff` has no subprocess dependency and, unlike every layer above it, no
dependency on `CliSupport` either (only `FSharp.Core`) — it is shared
*parsing* mechanics, not *process* mechanics, so it has nothing to gain from
depending on the process-execution plumbing. See "Mechanics vs. policy
layering for shared CLI parsing" below for why that split matters.

### `VcsToolkit.Git` / `VcsToolkit.Jj` — the two VCS backend clients

These are the two CLI clients that actually drive `git` and `jj` as
subprocesses: status, branches, commits, checkout, diff/log, merge/rebase/reset,
fetch/push/clone, worktrees, tags, blame, and config for `Git`; changes/log,
bookmarks, the operation log with rollback transactions, workspaces,
squash/split/absorb, diff queries, and git-sync for `Jj`. Each exposes both a
token-inheriting client and a `.At(dir)` cwd-bound view, and each hosts a
backend-specific conflict model (`Git`'s marker-based `Conflict` parses/renders
conflict markers in text; `Jj`'s is the natively materialized conflict state).
They are peers, not a hierarchy — neither depends on the other — because git
and jj are two different tools with genuinely different operating models
(index/working-tree vs. operation log/working-copy commit), and forcing one
client's shape onto the other would leak an abstraction neither tool actually
has.

### `VcsToolkit.GitHub` / `VcsToolkit.GitLab` / `VcsToolkit.Gitea` — the three forge clients

The forge-specific analogue of `Git`/`Jj`: each drives its own CLI (`gh`,
`glab`, `tea`) for the operations that CLI actually supports — pull/merge
requests, issues, CI status, releases, repo view — plus, for GitHub and GitLab,
a REST/GraphQL escape hatch for anything the typed surface doesn't cover yet.
They sit at the same layer as each other and don't depend on one another, for
the same reason `Git`/`Jj` don't: three different forges, three different CLIs,
three different capability sets (`tea` in particular is missing several
commands `gh`/`glab` have, which is why `Forge`'s `Unsupported`/`ForgeOp.Supports`
exists one layer up). GitHub and GitLab tokens are injected via
`ManagedClient.WithTokenEnv` as `GH_TOKEN`/`GITLAB_TOKEN` environment variables,
never as CLI arguments; Gitea authenticates only through `tea`'s own stored
login, since `tea` has no environment-token override.

### `VcsToolkit.Core` — the backend-agnostic `Repo` facade

`Core` is where "write code against *a* repository" becomes possible without
the caller ever branching on git-vs-jj. `Repo.Open` auto-detects which backend
is present at (or above) a directory, and from then on one handle answers
snapshot/branch reads, changed files and diff stats, partial commits,
fetch/push/checkout/rebase, a trace-free merge-conflict probe (`TryMerge`),
in-progress merge/rebase state, and worktree management, returning plain
backend-agnostic result types (`RepoSnapshot`, `FileChange`, `MergeProbe`, …).
It depends on both `Git` and `Jj` — it is the point in the graph where the two
backend families converge — but it is intentionally a *thin* common layer:
operations the two tools model too differently (a full three-way merge, jj's
op-restore, revset queries) are deliberately left off the common surface and
reached instead through the `.Git`/`.Jj` escape hatches or the `.GitAt`/`.JjAt`
dir-bound views. Forcing those onto a lowest-common-denominator shape would
either strip jj-specific power (op-log rollback has no git equivalent) or make
git pretend to have concepts it doesn't.

### `VcsToolkit.Forge` — the forge-agnostic facade

The forge counterpart of `Core`: one `Forge` handle runs the PR/MR lifecycle
that GitHub, GitLab, and Gitea all share — auth, repo view, list/view/create/
comment/edit/merge/ready/close, checks, issues, releases — returning plain
result types that never mention which forge produced them. It depends on all
three forge clients (the point where those families converge), and it carries
the one piece of security-relevant classification logic that belongs at this
layer rather than lower: `ForgeKind.OfRemoteUrl`, which maps a git remote's
host to a forge kind for the handful of recognized public SaaS hosts
(`github.com`, `gitlab.com`, `gitea.com`, `codeberg.org`) using an anchored
match (exact host or a genuine `*.domain` subdomain) so a lookalike host like
`gitlab.com.attacker.net` cannot masquerade as GitLab. Some operations
(`repoView`, `prMarkReady`, `prChecks`, `releaseView`, …) are `Unsupported` on
Gitea because `tea` simply has no equivalent command; `Forge.Supports` lets a
caller branch on that before calling rather than discovering it via a runtime
error.

### `VcsToolkit.TestKit` — throwaway sandboxes, intentionally dependency-free

`TestKit` gives a test a real, disposable git/jj repository to work against: a
self-cleaning `TempDir`, `GitSandbox`/`JjSandbox` scenario builders, and a
seeded `BareRemote` to clone/fetch/push against — synchronous and raising on
failure (it is test fixture code, not library code, so "throw on the
unexpected" is the right default here, unlike everywhere else in this stack).
It is placed off to the side in the dependency diagram on purpose: it has *no*
dependency on any other VcsToolkit package, not even `CliSupport`, so that it
can be a test-time dependency of the test project for **any** other package
without ever creating a build cycle (a test project for `Git` needs `TestKit`;
`TestKit` must therefore not need `Git`).

### `VcsToolkit.Watch` — filesystem-watch a repository into typed events

`Watch` builds on `Core` to turn raw filesystem churn into typed
domain events. `RepoWatcher` watches the `.git`/`.jj` state directory (and
optionally the working tree) with a `FileSystemWatcher`, debounces the burst
of writes a single VCS operation produces, then re-queries `Repo.Snapshot` and
diffs it against the previous snapshot to yield typed `RepoEvent`s
(`HeadMoved`, `BranchSwitched`, `BranchCreated`/`Deleted`,
`WorkingCopyChanged`, and upstream/ahead-behind/operation/conflict changes).
The re-query-and-diff design, rather than trying to interpret raw FS events
directly, is deliberate: it is what makes the watcher robust to the noisy
implementation details of how git/jj actually write refs (temp-file renames,
`index.lock` churn) — those look like arbitrary bursts of file activity at the
FS layer, but resolve to a small, meaningful diff once re-queried through
`Core`. This is the layer a prompt, a status bar, or a TUI would build on.

### `VcsToolkit.Mcp` / `VcsToolkit.Mcp.Server` — the agent-facing tool surface

`Mcp` is the hermetically-testable core of a Model Context Protocol server: it
depends on both facades (`Core` and `Forge`) and exposes their operations as a
catalogue of named, agent-callable `repo_*`/`forge_*` tools
(`VcsMcpServer` + `Catalog.callTool`), gated by a `WriteGate` write policy —
read tools are always available, but a mutating tool only runs when the server
was started with `--allow-write` (every mutation) or `--allow-tools` naming it
explicitly. Keeping this policy and the dispatcher in a library with no
dependency on the actual MCP SDK is what makes it testable without spinning up
a real MCP transport. `Mcp.Server` is the thin binary on top: it wires
`VcsMcpServer` to the `ModelContextProtocol` SDK over stdio, and is the one
place that layers on operational hardening a library shouldn't impose by
itself — a git client with repo hooks/config disabled, and a per-command
timeout. Splitting the SDK dependency into the binary, rather than pulling it
into `Mcp`, is what keeps the tool-dispatch logic testable in-process.

## Cross-cutting design principles

These patterns repeat across every wrapper client rather than living in one
place, because each client independently drives a real external CLI and each
independently has to solve the same problems that come with that.

**Real CLIs as subprocesses, never a library binding.** Every backend/forge
client drives the actual installed `git`/`jj`/`gh`/`glab`/`tea` binary as a
subprocess through ProcessKit's `IProcessRunner`, rather than binding to
`libgit2` or an SDK. `ManagedClient` (in `CliSupport`) is the shared layer this
runs through: it builds `Command`s, applies defaults (timeout, env, a
cancellation token), and executes them via the runner's capture/parse verbs
(`Run`, `Output`, `Probe`, `Parse`, …). The upside this buys is fidelity —
whatever the installed CLI actually does (its exact conflict-marker text, its
exact exit codes, its exact JSON shape) is what the toolkit sees and models,
with no separate reimplementation of git/jj/forge semantics to keep in sync.

**Totally tolerant parsers — never throw on arbitrary CLI output.** Every
parser fed by CLI text is written to be *total*: given genuinely arbitrary
input, it returns a best-effort value or an explicit `Error`, never an
exception. `Diff.parseDiff` walks a unified diff section by section and
tolerates a malformed hunk header. `TextParse.parseUInt64Or0` reads a numeric
CLI field as `0` rather than throwing on a malformed token. `Json`'s field
readers (`strOr`, `strOpt`, …) treat an absent, `null`, or wrong-kind JSON
field as an empty value rather than raising `KeyNotFoundException`/
`InvalidOperationException`, which is the contract every forge DTO parser is
built on. The underlying reason is the same for all of them: a CLI's output
format is not a contract the toolkit controls, and a version bump, locale
change, or edge case in the driven tool must degrade to an approximate parse
rather than crash the caller.

**Argv-injection guards.** Any value a caller supplies that ends up in a
positional CLI argument slot is checked by `rejectFlagLike` before the command
is ever built: empty/whitespace-only, a value starting with `-` (which the
driven CLI would parse as a flag rather than the intended positional value),
or a value containing a NUL byte are all refused up front. `checkFlags`
(in `Wrappers`) applies this to a whole list of `(what, value)` pairs at once,
short-circuiting on the first refusal, so a client method can guard every
caller-supplied string it forwards with one call. Values that are consumed
verbatim as a flag's *value* (`-m <message>`) are exempt, since they can never
be reinterpreted as a flag by the CLI's own parser.

**Credential provisioning — tokens through the environment, never argv.**
`Secret` wraps a credential value and redacts itself on every
`ToString()`/format call, so it cannot leak into a log line by accident; the
underlying value is reachable only via the explicit `Expose()` call at the
point of use. `ICredentialProvider` resolves a `Credential` for a
`CredentialRequest` (which service, which host) just-in-time, with `Ok None`
meaning "defer to the driven CLI's own ambient auth" — never an error — and an
empty/whitespace secret is treated the same way, so a misconfigured provider
degrades to ambient auth instead of overriding a working login with nothing.
Resolved secrets never touch argv: `ManagedClient.WithTokenEnv` injects a
forge token as an environment variable (`GH_TOKEN`/`GITLAB_TOKEN`), and
`Credentials.gitCredentialHelper` builds a git `credential.helper` snippet
that references the secret only by environment-variable *name* inside the
helper script, with the actual value passed through the child process's
environment — plus a host check inside the helper itself, so a redirect or
submodule pointing at a different host during a clone never receives the
credential.

**Error classification.** `RepoError` (`Core`) and `ForgeError` (`Forge`) wrap
the underlying `ProcessError` from a failed CLI invocation and add
intent-revealing classifier members instead of asking a caller to pattern-match
the wrapped error's internals: `IsTransient` (a momentary io/spawn hiccup worth
a blind retry), `IsNotFound` (the CLI binary itself isn't installed/on `PATH`,
a setup problem rather than a repository error), `IsLockContention` (another
process held the one repo-wide lock — safe to retry because nothing ran),
`IsTransientFetchError` (DNS/connection-level failure on a fetch, worth a
higher-level retry), and `IsMergeConflict`/`IsNothingToCommit` for the two
common "this failure is actually expected data" cases. Both error types are
treated as extensible (mirroring the Rust `#[non_exhaustive]` model): callers
that pattern-match them are expected to keep a wildcard arm so a future
variant doesn't become a compile break.

**Detached cleanup — cancellation-safe rollback.** A handful of operations
need to clean up after themselves even when the operation that triggered the
cleanup was itself cancelled or had already burned through its timeout budget
— for instance aborting a trial merge after `Core.TryMerge` probes for
conflicts. Rather than reuse that operation's own (possibly already-fired)
cancellation token for the cleanup, `Git.MergeAbortDetached`/
`IsMergeInProgressDetached` and their `Jj` rollback counterpart build a
*fresh* `CancellationTokenSource` on a fixed, self-contained timeout
(`MergeAbortCleanupTimeout` in `Git`, mirrored by `Jj`'s own rollback timeout)
and run the cleanup on that instead. This is what makes the cleanup
unconditional: a cancelled or timed-out caller still gets its trial merge
rolled back, because the rollback's own budget is independent of whatever
budget the caller had already exhausted.

**Mechanics vs. policy layering for shared CLI parsing.** `CliSupport` and
`Diff` split shared parsing code by what kind of thing is being shared, not by
which client happens to use it: `CliSupport` (`Wrappers`, `RemoteUrl`) owns
CLI-process *mechanics* — argv guards, output capture, wrapping a parse
failure into a `ProcessError` — while `Diff` (`TextParse`) owns pure,
*total text/number parsing* with no process-execution concept at all. Where a
piece of shared logic is security-sensitive (host classification for
`ForgeKind.OfRemoteUrl`, the credential-helper host scoping in
`Credentials.httpsHost`), the low-level *mechanics* of splitting a URL's
authority live in the shared `RemoteUrl` module, but the *policy* decision —
which hosts count as trusted, how brackets/zone-ids are treated — stays local
to the call site that has the security context to make that call, rather than
being generalized into shared code that different callers might rely on with
different (and possibly wrong) assumptions.

**Path anchoring.** `Core.Repo` promises that a `paths` list it accepts (e.g.
`LogPaths`, `CommitPaths`) is always **repo-root-relative**, regardless of
whether the handle is bound to `Root` or to a subdirectory (`Cwd` ≠ `Root`) —
matching the root-relative paths `ChangedFiles` already reports, so a path
taken from one call scopes the exact same file in another. Git and jj satisfy
that contract through two different mechanisms, and `Core` deliberately keeps
each backend's own mechanism rather than forcing one tool's convention onto
the other: git resolves a `-- <pathspec>` relative to the *command's working
directory*, so the git branch of `Core` runs the command from `Root` itself
(not the handle's possibly-different `Cwd`) to honour the root-relative
contract. jj instead resolves paths through `root-file:"<path>"` filesets
(`JjFileset`, `VcsToolkit.Jj/Types.fs`) — a fileset expression that is
self-anchoring to the workspace root by construction — so the jj branch can
pass the handle's `Cwd` through unchanged (it only needs to select which
workspace the query runs against; the path itself is already root-anchored by
the fileset syntax).

## Extension points

The typed surface intentionally does not try to cover every operation git,
jj, or a forge CLI can perform — it covers the common, well-modelled cases,
and leaves a small number of deliberate escape hatches for everything else:

- **Raw command escape hatches.** `Repo.Git`/`Repo.Jj` (each `Some` only for
  the backend that handle is bound to) return the underlying client for
  anything not on `Core`'s common surface. Below that, `Git`/`Jj` themselves
  expose `.Run(args)` (require success, return trimmed stdout) and
  `.RunRaw(args)` (never errors on a non-zero exit; hands back the captured
  result) for an arbitrary CLI invocation the typed API doesn't model at all.
  Both are explicitly **unguarded**: neither `Run` nor `RunRaw` runs the argv
  guard `checkFlags` applies on the typed surface, so they must never be fed
  untrusted input — on `Jj` in particular, its own `--config`/alias mechanism
  can reach code execution, so an unguarded raw invocation is a genuine
  RCE surface, not just a correctness concern. On `Jj`, a caller reaching
  for `Run`/`RunRaw` directly is also responsible for passing
  `--ignore-working-copy` itself when the invocation is read-only (the typed,
  read-only surface does this automatically; the raw escape hatch does not,
  since it has no way to know the caller's intent).
- **Construction seams: `OpenWith`/`FromGit`/`FromJj`/`At`.** `Repo.Open`
  auto-detects the backend and builds a plain client, but `Repo.OpenWith`
  takes an injected factory for whichever backend gets detected — for a
  caller that needs a pre-configured (e.g. hardened, timeout-bound) client
  without reimplementing detection and path handling. `Repo.FromGit`/
  `Repo.FromJj` build a handle directly from an already-constructed client,
  for a test seam or any case where the backend is already known. `.At(dir)`
  (on `Repo`, `Git`, `Jj`, `GitHub`, `GitLab`, `Gitea`, and `Forge`) returns a
  sibling handle re-anchored to a different directory while sharing the
  original handle's underlying client — useful for a monorepo or a
  worktree/workspace the caller has already resolved a path to.
- **Client configuration: `WithRunner`/`WithRetry`/`WithCredentials`.** Every
  wrapper client's `Create` builds a client on the real, job-backed
  `IProcessRunner`; `WithRunner` swaps in an alternative runner (a
  `ScriptedRunner` fake in tests, or any other `IProcessRunner`
  implementation) without changing anything else about the client's shape.
  `WithRetry(policy)` opts a client into lock-contention retry (off by
  default); `WithCredentials(provider)` attaches an `ICredentialProvider` (off
  by default, meaning ambient CLI auth). All three return a new client value
  rather than mutating in place, so a caller can derive several
  differently-configured clients from one starting point.
