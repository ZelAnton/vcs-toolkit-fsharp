# `vcs-mcp` — the Model Context Protocol server

`vcs-mcp` is a [Model Context Protocol](https://modelcontextprotocol.io/) server that drives a
single git/jj repository — and, optionally, its GitHub/GitLab/Gitea forge — through the typed
operations of `VcsToolkit.Core` and `VcsToolkit.Forge`. It speaks MCP over stdio, so an MCP
client (an IDE, a CLI tool, or any other MCP-capable host) launches it as a subprocess and calls
its tools instead of shelling out to raw `git`/`jj`/`gh`/`glab`/`tea` commands.

The server binary lives in `VcsToolkit.Mcp.Server`; its hermetically-testable core (the tool
catalogue, the dispatcher, the write policy, argument parsing) lives in the `VcsToolkit.Mcp`
library, which the binary wires to the official `ModelContextProtocol` SDK.

## Installation

`vcs-mcp` ships as a **.NET global tool**:

```sh
dotnet tool install --global vcs-mcp
```

This requires the .NET 10.0 SDK or later on `PATH`. The `vcs-mcp` package is self-contained: it
bundles every `VcsToolkit.*` assembly it needs inside the package itself (a framework-dependent
`dotnet pack`/`publish` of the tool project), so `dotnet tool install` does not need to restore
sibling NuGet dependencies separately. Update or remove it the same way as any other global tool:

```sh
dotnet tool update    --global vcs-mcp
dotnet tool uninstall --global vcs-mcp
```

Once installed, the `vcs-mcp` command is on `PATH`. The server itself drives whatever VCS/forge
CLIs the repository needs (`git`, `jj`, `gh`, `glab`, `tea`) as subprocesses — install and
authenticate the ones you intend to use before pointing an MCP client at the server.

## Running

```sh
vcs-mcp [OPTIONS]
```

The server speaks MCP over stdio (JSON-RPC on stdin/stdout); it is meant to be launched by an
MCP client, not run interactively. `vcs-mcp --help` prints the same reference below.

### CLI flags

| Flag | Argument | Default | Semantics |
|---|---|---|---|
| `--repo <path>` | a filesystem path | current directory (`.`) | The repository to serve. Opened once at startup via `Repo.OpenWith` with a hardened, timeout-bound client (git or jj auto-detected); a failure to open it is a fatal startup error. |
| `--forge <github\|gitlab\|gitea>` | one of the three literals | none (auto-detect) | Force the forge used by every `forge_*` tool. When omitted, the forge is auto-detected from the repository's `origin` remote (see "Forge auto-detection" below); if detection also finds nothing, `forge_*` tools fail with an invalid-params error explaining no forge is configured. An unrecognized value is a fatal startup error. |
| `--allow-write` | (flag, no argument) | off | Enable **every** mutating tool. Takes precedence over `--allow-tools` when both are given. |
| `--allow-tools <name,...>` | a comma-separated list of tool names (repeatable — later occurrences add to the allowed set) | empty (no mutating tool allowed) | Enable only the named mutating tools. Read tools are unaffected — they are always available regardless of this flag. Names are validated up front, at parse time, against the fixed list of mutating tool names (`WriteTools.all`, the same names used for `Destructive`/`ReadOnly` hints below); an unrecognized name is a fatal startup error rather than a silently-inert entry — **this validation runs regardless of `--allow-write`**, so an invalid name in `--allow-tools` still fails startup even when `--allow-write` is also given. Only the *effective write policy* ignores a syntactically-valid `--allow-tools` list once `--allow-write` is present (which grants every mutating tool outright). |
| `--timeout <seconds>` | a whole, non-negative number of seconds | `120` | Per-command deadline applied to every git/jj/forge-CLI subprocess the server spawns. `--timeout 0` disables the deadline entirely (no per-command timeout). An absurdly large value is clamped to `Int32.MaxValue` seconds rather than overflowing — for all practical purposes equivalent to "no timeout". A non-numeric or negative value is a fatal startup error. |
| `--output-budget <bytes>` | a whole, non-negative number of bytes | `200000` | Truncates the large-content read tools (`repo_show_file`, `repo_annotate`) past this many UTF-8 bytes, snapping to a full character boundary and appending a trailing `[truncated: showing N of M bytes]` marker. Content within the budget passes through byte-for-byte unchanged. `--output-budget 0` disables the cap entirely. Clamped the same way `--timeout` is for an absurdly large value. A non-numeric or negative value is a fatal startup error. |
| `-h`, `--help` | (flag, no argument) | — | Print the usage text and exit `0` without opening a repository or starting the server. |

An unrecognized flag, or a flag missing its required value, is a fatal startup error (the
process prints a message to stderr and exits `1`); it never silently starts with a
partially-parsed configuration — **unless `-h`/`--help` appears earlier in the argument list**.
Argument parsing stops at the first `-h`/`--help` it reaches, left-to-right, printing the usage
text and exiting `0` without validating anything after it; a bad flag placed *before* `--help`
is still fatal, but the same bad flag placed *after* it is never reached.

### Example invocations

```sh
# Serve the repository at ./my-repo with every mutating tool enabled
vcs-mcp --repo ./my-repo --allow-write

# Read-only by default; force the forge to GitHub with a 60s per-command timeout
vcs-mcp --repo ./my-repo --forge github --timeout 60

# Read-only, plus just enough write access to commit and push
vcs-mcp --repo ./my-repo --allow-tools repo_commit,repo_push
```

## Forge auto-detection

Unless `--forge` names one explicitly, the server tries to detect the forge from the
repository's `origin` remote at startup. Which command it runs depends on the repository's
detected *backend* (`repo.Kind`), not on whether a `.git` directory happens to exist — and a
valid `.jj` marker always wins over `.git` during backend detection, so a **git-colocated jj
repo is detected as `Jj`**, not `Git`:

- On a **git-backed** repo (`.git`-only, no `.jj`), it asks `git remote get-url origin`.
- On a **jj-backed** repo — colocated with git or not — it instead runs `jj git remote list
  --ignore-working-copy --color never` and parses the `origin <url>` line out of the raw
  output. A colocated repo's `.git` directory is never consulted directly for this; jj is the
  source of truth for the remote list on every jj-backed repo.
- If neither finds an `origin` remote (or the command itself fails), no forge is configured;
  every `forge_*` tool then fails with an invalid-params error naming `--forge` as the fix.

The detected (or forced) URL is classified by `ForgeKind.OfRemoteUrl`, a security-hardened host
matcher: it recognizes `github.com`, `gitlab.com`, and `gitea.com`/`codeberg.org` — or a proper
subdomain of one of them (`*.github.com`, etc.) — via an **anchored** suffix match, so a
lookalike host such as `github.com.attacker.net` or `notgithub.com` does **not** match. It also
guards IPv6-bracket authorities (`[::1]:443`) against being spoofed by a bracketed hostname or a
zone-id suffix, and folds only ASCII letters when comparing (never a Unicode case fold that
could complete a spoof). A host it does not recognize (a self-hosted GitLab/Gitea instance, an
on-prem GitHub Enterprise Server, …) is **not** auto-detected — pass `--forge` explicitly for
those.

Once resolved, the forge client (`gh`/`glab`/`tea`) carries the same `--timeout` as the repo
client.

## Write policy (`WriteGate`) and the per-repo write lock

Every tool is either a **read tool** (a query — always available, regardless of `--allow-write`/
`--allow-tools`) or a **mutating (write-gated) tool**. The fixed set of mutating tool names is
`WriteTools.all` (see the tool reference below for the full list); it is the single source of
truth both for what `--allow-tools` accepts and for which tools this gate covers.

The gate itself, `WriteGate`, is one of three states, chosen once at startup from the CLI flags
and never changed at runtime:

- **`None`** (the default — neither `--allow-write` nor `--allow-tools` given): no mutating tool
  is callable. Every mutating tool call is refused up front with an invalid-params error naming
  the disabled tool and how to enable it.
- **`All`** (`--allow-write`): every mutating tool is callable.
- **`Set of Set<string>`** (`--allow-tools a,b,c`, one or more times): only the named tools are
  callable; every other mutating tool is still refused. `--allow-write` overrides this when both
  are given.

Each mutating tool method checks `WriteGate.Allows <its own name>` before doing anything else —
the gate is enforced per-call, not as a one-time startup switch that could be bypassed by some
other code path.

**Per-repo write lock.** In addition to the gate, the server holds a single `SemaphoreSlim(1,
1)` (`writeLock`) that serializes every tool call that touches the *local working copy*: all
`repo_*` mutations, the local-checkout-affecting forge tools (`forge_pr_checkout`, and
**every** `forge_pr_merge`/`forge_pr_close` call — the lock is taken unconditionally for both,
not only when `delete_branch` is set; `delete_branch=true` is what can actually switch/delete the
local checkout, but the server holds the same lock regardless, to keep the locking decision
simple and race-free), and `repo_try_merge` (a real trial merge that materializes content before
rolling itself back, so it needs the same isolation). An MCP host can dispatch tool calls
concurrently; without this lock, two working-copy mutations could interleave (e.g. a
`repo_try_merge` probe's materialize-then-rollback racing a `repo_commit`).
**Remote-only forge writes** (`forge_issue_create`, `forge_issue_close`, `forge_issue_reopen`, `forge_issue_comment`,
`forge_pr_create`, `forge_pr_comment`, `forge_pr_edit`, `forge_pr_mark_ready`, `forge_pr_review`)
do **not** take this local lock — they only touch the remote forge, and the forge's own server
serializes concurrent requests on its side. `forge_pr_merge`/`forge_pr_close` are **not** in this
remote-only group, even when called without `delete_branch`: both always take the local lock (see
above).

The write lock is purely a **local concurrency guard**, not a cross-process or cross-machine
lock: it only serializes calls within one running `vcs-mcp` process against one repository. It
provides no protection against a second `vcs-mcp` instance, or a human, mutating the same
working copy at the same time.

## Tool reference

Every tool takes a single JSON object of named arguments (its `inputSchema`, generated from the
metadata below) and returns either a JSON result string or an MCP tool error. Argument tables
below list the argument name, JSON type, and whether it's required; optional arguments may be
omitted entirely.

Legend: **R/W** = read tool (always available) or write tool (gated, see above); **Destructive**
= the call, or one of its optional parameters, can irrecoverably discard data; **Idempotent** =
calling twice with the same arguments leaves the same end state as calling once. Every write
tool additionally requires `--allow-write`, or `--allow-tools` naming it.

### `repo_*` — repository tools (git/jj, via `VcsToolkit.Core`)

#### Reads (always available)

| Tool | Purpose | Arguments |
|---|---|---|
| `repo_snapshot` | A batched snapshot of the repo state: branch, upstream, ahead/behind, HEAD, dirtiness, change count, conflict, and operation state. | — |
| `repo_info` | Which backend (git/jj), the repository root, the working directory, and the configured forge (if any). | — |
| `repo_status` | The working-copy changes (added/modified/deleted/renamed paths). | — |
| `repo_diff_stat` | Aggregate insertion/deletion/file counts for the working copy. | — |
| `repo_branches` | Local branch (git) / bookmark (jj) names. | — |
| `repo_current_branch` | The current branch/bookmark (null when detached/unset). | — |
| `repo_conflicts` | Paths with unresolved merge conflicts (repo-relative, `/`-separated). | — |
| `repo_worktrees` | Attached worktrees (git) / workspaces (jj). | — |
| `repo_show_file` | The content of a file at a revision, subject to `--output-budget` (default 200000 bytes; a truncated read appends `[truncated: showing N of M bytes]`). UTF-8-decoded text only — a non-UTF-8 byte is replaced with U+FFFD and does not round-trip, so this is for text files, not byte-exact binary reads. `rev` is passed through as-is to the backend (git commit-ish or jj revset — not cross-backend portable). | `rev` (string, required), `path` (string, required) |
| `repo_log` | Up to `max` commits reachable from `revspec_or_revset` (git revspec, e.g. `"HEAD"`, or jj revset, e.g. `"@"`), most-recent-first. `author`/`date` are null on jj (its typed log doesn't surface authorship/timestamp). | `revspec_or_revset` (string, required), `max` (integer, required) |
| `repo_annotate` | Per-line authorship of a file at a revision (git blame / jj file annotate), as a JSON array of lines, subject to the same `--output-budget` truncation as `repo_show_file`. **A truncated result is not guaranteed to be valid JSON** — see "Output-size budget" below. `rev` is passed through as-is (git commit-ish or jj revset). | `path` (string, required), `rev` (string, optional — omit to annotate the working copy / `@`) |

#### Writes (gated)

`repo_try_merge` is write-gated but not destructive: it spawns a **real** trial merge that
materializes content in order to detect conflicts, then attempts to roll itself back — like
`repo_checkout`, materializing content is why it needs write access. The rollback is not
best-effort: on both backends a failed rollback is surfaced as an error rather than silently
returning a probe result that would misdescribe the on-disk state. Rollback failure is rare but
possible — most concretely on jj, where the rollback is refused (rather than clobbering
unrelated work) if the op log has diverged from the captured restore point because a *concurrent*
`jj` operation ran against the same repository while the probe was in flight. In that case the
call fails with an internal error and the materialized probe change is left in the working copy
instead of being cleaned up; the catalogue's `Idempotent: yes` hint below assumes a successful
rollback and does **not** hold for that failure path — do not retry a failed `repo_try_merge`
call purely because it is marked idempotent. On a rollback-failure error, inspect `repo_status`/
`repo_conflicts`/`repo_snapshot` (and, on git, `repo_abort_in_progress`) before deciding whether
it is safe to retry or clean up manually.

| Tool | Purpose | Arguments | Destructive | Idempotent |
|---|---|---|---|---|
| `repo_try_merge` | Probe whether merging `source` into the current work would conflict, without leaving a trace when the rollback succeeds (see the rollback-failure caveat above). | `source` (string, required) | no | yes (assumes rollback succeeded — see caveat above) |
| `repo_commit` | Commit exactly the given paths with a message. | `paths` (array of strings, required), `message` (string, required) | no | no |
| `repo_checkout` | Switch the working copy to a branch/bookmark/revision (git checkout / jj edit). | `reference` (string, required) | no | yes |
| `repo_fetch` | Fetch from the default remote (git fetch / jj git fetch). | — | no | yes |
| `repo_push` | Push an existing branch/bookmark to origin. Fast-forward-only (no `force` argument, so a diverged remote is refused rather than overwritten). | `branch` (string, required) | no | yes |
| `repo_create_worktree` | Create a worktree/workspace at `path` on a new `branch` from `base`. | `path` (string, required), `branch` (string, required), `base` (string, required) | no | no |
| `repo_remove_worktree` | Remove the worktree/workspace at `path` (the main worktree is always refused). | `path` (string, required), `force` (boolean, optional — force removal despite uncommitted changes) | **yes** | no |
| `repo_rebase` | Rebase the current work onto `onto` (git rebase / jj rebase -d); rewrites the current branch's commits onto a new base. | `onto` (string, required) | **yes** | no |
| `repo_abort_in_progress` | Abort the in-progress operation, if any (git: merge/rebase `--abort`; jj: a no-op); discards the paused state's partial progress. Reports the fresh post-call operation state. | — | **yes** | yes |
| `repo_continue_in_progress` | Continue the in-progress operation after conflict resolution (git: `commit --no-edit` for a merge / `rebase --continue`; jj: a no-op). Reports the fresh post-call operation state (`Conflict` when unresolved paths still block, `Clear` when finished). | — | no | no |
| `repo_delete_branch` | Delete a local branch (git) / bookmark (jj). `force` (git only) deletes even an unmerged branch, discarding its unique commits. | `name` (string, required), `force` (boolean, optional) | **yes** | no |
| `repo_rename_branch` | Rename a local branch (git) / bookmark (jj). Preserves the commits. | `old_name` (string, required), `new_name` (string, required) | no | no |
| `repo_new_child` | Start new work on top of `reference` **without** modifying it (git checkout / jj new) — unlike `repo_checkout`, does not rewrite `reference` in place on jj. | `reference` (string, required) | no | no |

### `forge_*` — forge tools (GitHub/GitLab/Gitea, via `VcsToolkit.Forge`)

Every `forge_*` tool requires a configured forge (`--forge`, or a successfully auto-detected
`origin` remote); otherwise it fails with an invalid-params error. Tools marked "Unsupported on
Gitea" (or another forge) fail with an invalid-params error naming the unsupported operation on
that forge, rather than silently degrading.

#### Reads (always available)

| Tool | Purpose | Arguments |
|---|---|---|
| `forge_auth_status` | Whether the forge CLI reports an authenticated session. | — |
| `forge_repo_view` | The repository/project on the configured forge. **Unsupported on Gitea.** | — |
| `forge_info` | The forge's identity and flat capability map. | — |
| `forge_pr_list` | Pull/merge requests on the configured forge, open by default and capped at 100 by default. **Unsupported on Gitea for every state** (`tea pr list --output json` does not work against the real CLI). | `state` (string, optional — `open`/`closed`/`merged`/`all`, default `open`), `limit` (integer, optional, default 100) |
| `forge_pr_view` | A single pull/merge request by number. | `number` (integer, required — GitLab uses the project-scoped iid) |
| `forge_pr_for_branch` | Pull/merge requests whose source branch is `source_branch`, in any state, regardless of target branch — the "after pushing, find my PR" query. Returns a list; an empty list means none currently match. **Unsupported on Gitea** (`tea pr list --output json` does not work against the real CLI). | `source_branch` (string, required) |
| `forge_pr_checks` | The PR/MR's coarse CI status. **Unsupported on Gitea.** | `number` (integer, required) |
| `forge_issue_list` | Issues on the configured forge, open by default and capped at 100 by default. **Unsupported on Gitea for every state** (`tea issues list --output json` does not work against the real CLI). | `state` (string, optional — `open`/`closed`/`all`, default `open`), `limit` (integer, optional, default 100) |
| `forge_issue_view` | A single issue by number, with body and URL filled. | `number` (integer, required — GitLab uses the project-scoped iid) |
| `forge_release_list` | Releases on the configured forge, newest first (up to 100). | — |
| `forge_release_view` | A single release by tag. **Unsupported on Gitea** — filter `forge_release_list` instead. | `tag` (string, required) |

#### Writes (gated)

| Tool | Purpose | Arguments | Destructive | Idempotent |
|---|---|---|---|---|
| `forge_issue_create` | Open an issue, returning the CLI's output (the URL on success). | `title` (string, required), `body` (string, required) | no | no |
| `forge_issue_close` | Close an issue (reopenable). | `number` (integer, required) | no | yes |
| `forge_issue_reopen` | Reopen a closed issue. | `number` (integer, required) | no | yes |
| `forge_issue_comment` | Post a comment to an existing issue, returning the CLI's output. | `number` (integer, required), `body` (string, required) | no | no |
| `forge_pr_create` | Open a pull/merge request, returning the CLI's output (the URL on success). | `title` (string, required), `body` (string, required), `source` (string, optional — defaults to the current branch), `target` (string, optional — defaults to the repo default) | no | no |
| `forge_pr_merge` | Merge a pull/merge request with a strategy (`merge`/`squash`/`rebase`). `auto`/`delete_branch` are GitHub-only — refused as Unsupported on GitLab/Gitea if set. `delete_branch=true` deletes the source branch. | `number` (integer, required), `strategy` (string, required), `auto` (boolean, optional), `delete_branch` (boolean, optional) | **yes** | no |
| `forge_pr_close` | Close a pull/merge request without merging. `delete_branch` is GitHub-only (refused as Unsupported on GitLab/Gitea) and also deletes the source branch. | `number` (integer, required), `delete_branch` (boolean, optional) | **yes** | yes |
| `forge_pr_mark_ready` | Mark a draft pull/merge request as ready for review. **Unsupported on Gitea.** | `number` (integer, required) | no | yes |
| `forge_pr_comment` | Post a comment to an existing pull/merge request, returning the CLI's output. | `number` (integer, required), `body` (string, required) | no | no |
| `forge_pr_edit` | Edit a pull/merge request's title and/or body (at least one required). | `number` (integer, required), `title` (string, optional), `body` (string, optional) | no | yes |
| `forge_pr_checkout` | Check out a pull/merge request's branch into the local working copy (`gh pr checkout` / `glab mr checkout` / `tea pr checkout`). Holds the per-repo write lock — it mutates the local working tree. | `number` (integer, required) | no | yes |
| `forge_pr_review` | Submit a review on a pull/merge request: `approve`, `request_changes`, or `comment`. `body` is required for `request_changes`/`comment`, optional for `approve`. `request_changes` is Unsupported on GitLab; `comment` is Unsupported on GitLab and Gitea (use `forge_pr_comment` there instead). | `number` (integer, required), `kind` (string, required), `body` (string, optional) | no | no |
| `forge_release_delete` | Delete a release by tag. **Unsupported on Gitea.** | `tag` (string, required) | **yes** | no |

## Output-size budget

`repo_show_file` and `repo_annotate` are the two tools whose output can be arbitrarily large
(a full file's content, or a full per-line annotation of one). Both are subject to
`--output-budget` (default 200000 bytes; `0` disables it): content is measured in UTF-8 bytes,
truncated at a full character boundary if it exceeds the budget, and a trailing
`[truncated: showing N of M bytes]` marker is appended so a truncated read is never mistaken for
the complete file. Content within the budget passes through byte-for-byte unchanged — the budget
never rewrites or re-encodes a read that already fits.

**`repo_annotate` and JSON validity.** The budget is applied uniformly to the *raw serialized
text* the tool would otherwise return — for `repo_show_file` that text is the plain file content,
so truncating it is harmless, but for `repo_annotate` that text is already a serialized JSON
array. Truncation is character-boundary-safe, not JSON-structure-aware: it can cut the array in
the middle of an element, and the trailing `[truncated: showing N of M bytes]` marker is plain
text appended after that cut, not part of any JSON value. A truncated `repo_annotate` result is
therefore **not guaranteed to be valid JSON**, even though an untruncated one always is — a
caller that needs to parse the result should either raise `--output-budget` (or disable it with
`--output-budget 0`) for that call's repository, or detect the trailing marker and treat a
truncated response as pre-formatted text rather than feeding it to a JSON parser.

## Errors

Internally the server classifies every tool failure into one of two kinds, and surfaces each on
a **different** MCP error channel, so a client can tell them apart programmatically without
pattern-matching the message text:

- **Invalid params** — the caller's input/request was refused: a bad or missing argument, an
  unknown tool, a disabled write tool (not covered by `--allow-write`/`--allow-tools`), an
  unsupported forge operation on the configured forge, or no forge configured at all. This is
  raised as a JSON-RPC **protocol** error: the tool call returns no normal result at all —
  instead the server responds with a JSON-RPC error carrying `McpErrorCode.InvalidParams` (the
  standard invalid-params code) and the human-readable message. Most MCP client SDKs surface
  this as a failed/thrown call rather than a `CallToolResult`.
- **Internal error** — a backend (git/jj/forge CLI) or other internal execution failure, e.g. a
  spawn failure or an unexpected CLI exit. This is returned **inside** a normal MCP tool-call
  result with `isError: true` and a single text content block carrying the human-readable
  message — the MCP convention for execution errors, so the model sees the detail and can
  self-correct.

So the two kinds are distinguishable on the wire: an invalid-params refusal arrives as a
JSON-RPC protocol error (a failed request with an error code), whereas an internal execution
failure arrives as a successful request whose result has `isError: true`. A client that needs to
react differently to "your input was wrong" versus "something failed on the backend" can switch
on that channel instead of parsing the message text. Either way, the server process itself keeps
running and the MCP session stays open for the next call.

## Example MCP client configuration

Most MCP clients read a `mcpServers` block naming the command to launch and its arguments. For
example, to serve the current repository read-only, with a 60-second per-command timeout:

```json
{
  "mcpServers": {
    "vcs-toolkit": {
      "command": "vcs-mcp",
      "args": ["--repo", ".", "--timeout", "60"]
    }
  }
}
```

To also allow committing and pushing (but nothing else destructive):

```json
{
  "mcpServers": {
    "vcs-toolkit": {
      "command": "vcs-mcp",
      "args": [
        "--repo", "/path/to/repo",
        "--allow-tools", "repo_commit,repo_push",
        "--forge", "github"
      ]
    }
  }
}
```

Adjust the block's exact top-level shape (some clients nest it differently, or take `command`/
`args` under a different key) to match your specific MCP client's configuration format; the
`command`/`args` values themselves are the same regardless.
