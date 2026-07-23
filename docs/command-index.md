# CLI command coverage index

The per-package guides ([architecture.md](architecture.md), [examples.md](examples.md))
document the typed surface **from the method outward** — "here's `PrMerge`, here's what it
runs." This page inverts that: **from the CLI command inward** — "I know `git rebase --onto`
/ `jj parallelize` / `gh api`; is it covered by a typed method, or do I need the escape
hatch?" Each table row is one typed method and the exact subcommand/flags it runs, sourced
directly from the wrapper's client type (`Git`/`Jj`/`GitHub`/`GitLab`/`Gitea` in
`src/VcsToolkit.*/*.fs`) — the same source [architecture.md](architecture.md) and
[examples.md](examples.md) document, cross-checked directly against the implementation so a
method those guides haven't caught up to yet still shows up here.

This index doubles as a **map of the untyped surface**: everything a wrapper's `Run`/`RunRaw`
escape hatch reaches but no typed method models yet is a candidate for a future typed method.

## How to read this

- **Runs** — the argv the method builds, elided to the load-bearing flags (see the linked
  source file for the full contract: option types, error classification, argv-injection
  guards — every method placing a caller-supplied value in a bare positional slot rejects an
  empty or `-`-leading value before spawning; see each wrapper's own doc comment for the exact
  guard).
- Every client (`Git`, `Jj`, `GitHub`, `GitLab`, `Gitea`) also exposes a `.At(dir)`
  **cwd-bound view** (`GitAt`, `JjAt`, `GitHubAt`, `GitLabAt`, `GiteaAt`): its methods mirror
  the client 1:1, only dropping the leading `dir` parameter (`git.At(dir).Status()` is
  `git.Status(dir)`). The tables below list each method **once**, on the unbound client — the
  bound view is not a separate row.
- **Not modeled** sections per wrapper list commands **consciously left untyped** — reachable
  only through that wrapper's `Run`/`RunRaw` escape hatch. Each wrapper's CLI has far more
  surface than any table below or its "not modeled" list enumerates in full (git alone ships
  well over a hundred subcommands); the lists name the ones a consumer is most likely to look
  for. **Anything not in a table above it is, by definition, unmodeled** — go to the escape
  hatch.
- A method already reachable through a facade (`VcsToolkit.Core`'s `Repo`, `VcsToolkit.Forge`'s
  `Forge`) is not repeated here — this index is the wrapper-level wiring the facades dispatch
  to; see [Facade escape-hatch routers](#facade-escape-hatch-routers) for how a facade caller
  drops back to the wrapper level.

## git (`VcsToolkit.Git` — the `git` binary)

Client: `Git` / `GitAt` (`src/VcsToolkit.Git/Git.fs`). See
[architecture.md](architecture.md) (the "`VcsToolkit.Git` / `VcsToolkit.Jj`" section)
for this client's place in the layering.

### Status, log, branches, revisions

| Method | Runs | Notes |
|---|---|---|
| `Status` | `status --porcelain=v1 -z` | parsed `StatusEntry list` |
| `StatusText` | `status --porcelain=v1` | raw text |
| `StatusTracked` | `status --porcelain=v1 -z --untracked-files=no` | tracked-only dirtiness |
| `BranchStatus` | `status --porcelain=v2 --branch -z` | combined branch + WT snapshot; `GIT_OPTIONAL_LOCKS=0` so a watcher's poll doesn't retrigger itself |
| `ConflictedFiles` | `diff --name-only --diff-filter=U -z` | repo-relative, NUL-safe paths |
| `CurrentBranch` | `symbolic-ref --quiet --short HEAD` | `None` only on a detached HEAD |
| `Branches` | `branch --no-column --no-color` | current one flagged |
| `Log` | `log <revspec> -n<max> -z --format=…` | |
| `LogPaths` | `--literal-pathspecs log <revspec> -n<max> -z --format=… -- <paths>` | scoped to paths; non-empty required; chunks across several within-budget calls above `ArgvPathBudget` |
| `RevParse` | `rev-parse --verify <rev>` | full hash |
| `RevParseShort` | `rev-parse --short <rev>` | abbreviated hash |
| `ResolveCommit` | `rev-parse --verify <rev>^{commit}` | peels annotated tags |
| `IsUnborn` | `rev-parse --verify -q HEAD` | fresh repo, no commits |
| `CommonDir` | `rev-parse --git-common-dir` | stable across worktrees |
| `GitDir` | `rev-parse --git-dir` | this worktree's git dir |
| `ResolvedGitDir` | `GitDir`, resolved to an absolute path | |
| `RemoteHeadBranch` | `symbolic-ref --quiet refs/remotes/origin/HEAD` | `None` when unset |
| `BranchExists` | `show-ref --verify --quiet refs/heads/<name>` | |
| `RemoteBranchExists` | `ls-remote origin refs/heads/<name>` | fully-qualified ref |
| `RemoteUrl` | `remote get-url <remote>` | |
| `Remotes` | `remote -v` | parsed `Remote list`; one entry per remote |
| `Upstream` | `symbolic-ref --quiet --short HEAD` then `rev-parse --abbrev-ref --symbolic-full-name @{u}` | `None` on no upstream; error on detached HEAD |
| `RemoteBranches` | `ls-remote --heads <remote>` | no fetch |
| `IsMerged` | `branch --merged <target> --no-column --no-color` | |
| `SetUpstream` | `branch --set-upstream-to=<upstream> <branch>` | |
| `DeleteBranch` | `branch -d` (`-D` if forced) | |
| `RenameBranch` | `branch -m <old> <new>` | |
| `RevListCount` | `rev-list --count <range>` | |
| `DiffRangeIsEmpty` | `diff --quiet <range>` | |
| `DiffStat` | `diff --shortstat <range>` | C-locale so the English-keyed parser survives a non-English git |
| `StagedIsEmpty` | `diff --cached --quiet` | |
| `EmptyTreeOid` | `hash-object -t tree --stdin` (empty stdin) | this repo's actual empty-tree id (SHA-1 or SHA-256) |
| `DiffIsEmpty` | `diff --quiet` | tracked files only |
| `IsRebaseInProgress` | probes `rebase-merge/` or `rebase-apply/` markers under the resolved git dir | no subcommand |
| `IsAmInProgress` | probes the `rebase-apply/applying` marker under the resolved git dir | no subcommand |
| `IsMergeInProgress` | probes the `MERGE_HEAD` marker under the resolved git dir | no subcommand |
| `IsMergeInProgressDetached` | probes the `MERGE_HEAD` marker under the resolved git dir | fresh cancellation budget for cleanup paths |
| `IsCherryPickInProgress` | probes the `CHERRY_PICK_HEAD` marker under the resolved git dir | no subcommand |
| `IsRevertInProgress` | probes the `REVERT_HEAD` marker under the resolved git dir | no subcommand |
| `IsBisectInProgress` | probes the `BISECT_LOG` marker under the resolved git dir | no subcommand |

### Staging & committing

| Method | Runs | Notes |
|---|---|---|
| `Add` | `--literal-pathspecs add -- <paths>` | large sets go via `--pathspec-from-file=- --pathspec-file-nul` stdin |
| `Commit` | `commit -m <message>` | staged index |
| `CommitPaths` | `--literal-pathspecs commit [--amend] -m <message> --only -- <paths>` | via `CommitPaths` spec; stdin transport for large sets |
| `LastCommitMessage` | `log -1 --format=%B` | full message |
| `Init` | `init` | |

### Checkout, worktrees, tags, clone, config, show

| Method | Runs | Notes |
|---|---|---|
| `Checkout` | `checkout <reference> --` | trailing `--` keeps a would-be pathspec from silently restoring a file |
| `CheckoutDetach` | `checkout --detach <commit>` | |
| `CreateBranch` | `branch <name>` | no switch |
| `WorktreeList` | `worktree list --porcelain` | |
| `WorktreeAdd` | `worktree add [-b <branch>] [--no-checkout] <path> [<commit-ish>]` | via `WorktreeAdd` spec |
| `WorktreeRemove` | `worktree remove [--force] <path>` | |
| `WorktreeMove` | `worktree move <from> <to>` | |
| `WorktreePrune` | `worktree prune` | |
| `CloneRepo` | `clone [--branch b] [--depth d] [--bare] <url> <dest>` | via `CloneSpec`; dirless, absolute `dest` |
| `TagCreate` | `tag <name> [<rev>]` | lightweight |
| `TagCreateAnnotated` | `tag -a <name> -m <message> [<rev>]` | via `AnnotatedTag` |
| `TagList` | `tag --list --no-column` | |
| `TagDelete` | `tag -d <name>` | |
| `ShowFile` | `show <rev>:<path>` | UTF-8-decoded, lossy on non-UTF-8 content |
| `ShowFileBytes` | `show <rev>:<path>` | verbatim bytes — byte-exact for non-UTF-8 content |
| `ConfigGet` | `config --get <key>` | `None` when unset |
| `ConfigSet` | `config -- <key> <value>` | `--` end-of-options separator, since a value may legitimately start with `-` |
| `RemoteAdd` | `remote add <name> <url>` | |
| `RemoteSetUrl` | `remote set-url <name> <url>` | |
| `Blame` | `blame --line-porcelain [<rev>] -- <path>` | |

### Diff

| Method | Runs | Notes |
|---|---|---|
| `Diff` | layered on `DiffText` | parsed `FileDiff list` |
| `DiffText` | `diff <target> --no-color --no-ext-diff -M --src-prefix=a/ --dst-prefix=b/` | untrimmed; `WorkingTree` diffs against `HEAD` (or the empty tree on an unborn repo) |

### Fetch, push, merge, rebase, sequencer, stash

| Method | Runs | Notes |
|---|---|---|
| `Fetch` | `fetch --quiet` | prompt-off, retried 3× on transient failure |
| `FetchFrom` | `fetch --quiet <remote>` | same retry |
| `FetchBranch` | `fetch --quiet origin refs/heads/<b>:refs/remotes/origin/<b>` | same retry |
| `Push` | `push [-u] <remote> <refspec>` | via `GitPush`; refspec restricted to a plain branch or `local:remote` (no force/delete/multi-ref) |
| `MergeSquash` | `merge --squash <branch>` | |
| `MergeCommit` | `merge [--no-ff] [-m <msg> \| --no-edit] <branch>` | via `MergeCommit` spec |
| `MergeNoCommit` | `merge --no-commit [--squash \| --no-ff] <branch>` | via `MergeNoCommit` spec; dry-run pattern |
| `MergeAbort` | `merge --abort` | |
| `MergeAbortDetached` | `merge --abort`, same argv as `MergeAbort` | fresh cancellation budget, for cleanup paths |
| `MergeContinue` | `commit --no-edit` | editor suppressed |
| `ResetMerge` | `reset --merge` | squash-safe undo |
| `ResetHard` | `reset --hard <rev>` | destructive |
| `Rebase` | `rebase <onto>` | editor suppressed; **not** `rebase --onto <upstream> <onto>` — see "not modeled" |
| `RebaseAbort` | `rebase --abort` | |
| `RebaseContinue` | `rebase --continue` | editor suppressed |
| `RebaseSkip` | `rebase --skip` | mainly the apply-backend's "nothing to commit" stop |
| `AmAbort` | `am --abort` | restores the pre-`am` HEAD |
| `CherryPick` | `cherry-pick <rev>` | conflict detected via `ConflictedFiles` |
| `CherryPickAbort` | `cherry-pick --abort` | |
| `CherryPickContinue` | `cherry-pick --continue` | editor suppressed; can stop again on the next commit's conflict |
| `Revert` | `revert --no-edit <rev>` | |
| `RevertAbort` | `revert --abort` | |
| `RevertContinue` | `revert --continue` | editor suppressed |
| `BisectReset` | `bisect reset` | ends a bisect session; no `--continue` |
| `StashPush` | `stash push [--include-untracked]` | |
| `StashPop` | `stash pop` | |
| `StashList` | `stash list -z --format=%gd%x1f%H%x1f%gs` | parsed `StashEntry list`, newest first |
| `StashApply` | `stash apply stash@{<index>}` | applies without dropping; index resolved at operation time |
| `StashDrop` | `stash drop stash@{<index>}` | drops without applying; index resolved at operation time |
| `SwitchWithStash` | composed: `stash push -u` → `checkout` → `stash pop --index` (or a bare `checkout` when nothing to save) | data-loss-safe: brackets the push with a stash-depth check so a bare pop never grabs an unrelated entry |
| `Clean` | `clean [-d] [-x] [-X] [-n] [-f]` | via `Clean` spec; parsed `CleanEntry list`; refuses to spawn without `DryRun`/`Force` set |

### Submodules

| Method | Runs | Notes |
|---|---|---|
| `SubmoduleList` | `config --file .gitmodules --list -z` | parsed `Submodule list` (name/path/url/branch); **empty and spawn-free** when there is no `.gitmodules` (probed on disk first) — a pure read, no nested-repo execution |
| `SubmoduleStatus` | `submodule status` | parsed `SubmoduleStatus list` with a typed `SubmoduleState` (`-`/`+`/`U`/space); a read — no nested-repo materialization |
| `SubmoduleUpdate` | `submodule update [--init] [--recursive] [--depth <n>] [-- <paths>]` | via `SubmoduleUpdate` spec; `GIT_TERMINAL_PROMPT=0`. **Materializes and EXECUTES nested repositories** — pair with `Harden` + a `protocol.*` allow-list for an untrusted superproject; see `architecture.md` |

### Discovery & raw escape hatches

| Method | Runs | Notes |
|---|---|---|
| `Version` | `--version` | |
| `Capabilities` | `--version`, parsed (`git ≥ 2.31` floor for the `Harden` config pins; unrelated to any other method) | |
| `Run` | `git <args>` in the process cwd (client) or the bound `dir` (`GitAt`) | |
| `RunRaw` | like `Run`, never errors on a non-zero exit | |

### git — not modeled (examples) → escape hatch

`add -p`/interactive staging, `am`/`apply` (patch application other than the in-progress-`am`
probes above), `archive`, `bundle`, `describe`, `difftool`/`mergetool`, `fsck`, `gc`, `grep`,
`ls-files`/`ls-tree`, `merge-base`, `mv`/`rm` (path staging goes through `Add`), `notes`,
`rebase --onto` (a three-way rebase onto an explicit upstream — only the plain `rebase <onto>`
form is typed), `reflog`, `replace`, `reset` (soft/mixed — only `--hard`/`--merge` are typed),
`send-email`, `shortlog`, `sparse-checkout`, `submodule` (only `list`/`status`/`update` are
typed, as `SubmoduleList`/`SubmoduleStatus`/`SubmoduleUpdate` above — `add`/`deinit`/`foreach`/
`sync`/`set-branch`/`set-url`/`absorbgitdirs` go through the escape hatch), `subtree`,
`verify-commit`/`verify-tag`. Reach any of these through `Run`/`RunRaw`.

## jj (`VcsToolkit.Jj` — the `jj` binary)

Client: `Jj` / `JjAt` (`src/VcsToolkit.Jj/Jj.fs`). See
[architecture.md](architecture.md) (the "`VcsToolkit.Git` / `VcsToolkit.Jj`" section)
for this client's place in the layering. Every bookmark/remote name a caller supplies is
matched with jj's `exact:` string-pattern prefix (never a bare name), so a `*`/`?`/`[]` in a
caller-supplied name can't fan a mutation out across every matching ref.

### Status, log, describe, bookmarks

| Method | Runs | Notes |
|---|---|---|
| `Status` | `diff -r @ --summary` | resolves the workspace root first; snapshots the WC |
| `StatusText` | `status` (human text) | |
| `Log` | `log -r <revset> -n<max> --no-graph -T <template>` | up to `max`, newest first |
| `LogPaths` | `log -r <revset> -n<max> --no-graph -T <template> <filesets>` | non-empty filesets required |
| `CurrentChange` | `Log(dir, "@", 1)`, reduced to one `Change` | |
| `CurrentBookmark` | `log -r @ --no-graph --limit 1 -T <bookmarks-template>` | local bookmark on `@`, if exactly one; `None` when none |
| `Trunk` | `log -r trunk() --no-graph --limit 1 -T <bookmarks-template>` | `None` when unresolved |
| `Describe` | `describe -m <message>` | on `@` |
| `DescribeRev` | `describe -r <revset> -m <message>` | arbitrary revision |
| `NewChange` | `new -m <message>` | on top of the WC |
| `NewChild` | `new <parent>` | undescribed child; unlike `Edit`, `parent` itself is left untouched |
| `Bookmarks` | `bookmark list -T <template>` | snapshots the WC first |
| `BookmarksAll` | `bookmark list -a -T <template>` | local + remote-tracking |
| `ReachableBookmarks` | `log -r 'heads(::@ & bookmarks())' --no-graph -T <template>` | local bookmarks nearest to `@` |
| `BookmarkTrack` | `bookmark track exact:<name>@<remote>` | remote also rejected if it contains a glob metacharacter |
| `BookmarkSet` | `bookmark set <name> -r <revision>` | |
| `BookmarkCreate` | `bookmark create <name> -r <revision>` | |
| `BookmarkRename` | `bookmark rename <old> <new>` | |
| `BookmarkDelete` | `bookmark delete exact:<name>` | |
| `BookmarkMove` | `bookmark move exact:<name> --to <rev> [--allow-backwards]` | |

### Diff, query, conflicts, files

| Method | Runs | Notes |
|---|---|---|
| `Diff` | layered on `DiffText` | parsed `FileDiff list` |
| `DiffText` | `diff -r <spec> --git` | verbatim |
| `DiffSummary` | `diff -r (<from>)..(<to>) --summary` | per-file, resolves the workspace root first |
| `DiffStat` | `diff -r <revset> --stat` | |
| `CommitCount` | `log -r <revset> --no-graph -T <count-template>` | one id per line |
| `IsConflicted` | `log -r <revset> --no-graph --limit 1 -T <conflict-template>` | |
| `HasWorkingCopyConflict` | `IsConflicted(dir, "@")` | |
| `ResolveList` | `file list -r <revset> -T <conflicted-paths-template>` | lossless paths; empty output on no conflicts (never errors) |
| `TemplateQuery` | `log -r <revset> --no-graph [--limit n] -T <template>` | untrimmed raw stdout |
| `Description` | `TemplateQuery(dir, revset, "description", Some 1)`, trimmed | newest commit of a multi-commit revset |
| `Evolog` | `evolog -r <revset> --no-graph --limit <max> -T <template>` | newest predecessor first |
| `FileAnnotate` | `file annotate [-r <revset>] -T <template> --color never -- <path>` | plain path, not a fileset |
| `FileShow` | `file show -r <revset> root-file:"<path>"` | UTF-8-decoded, lossy on non-UTF-8 content |
| `FileShowBytes` | `file show -r <revset> root-file:"<path>"` | verbatim bytes |

### Rebase, squash/split, merging, sparse

| Method | Runs | Notes |
|---|---|---|
| `Rebase` | `rebase -d <onto>` (jj's default `-b @`) | whole descendant closure — not git's `rebase` semantics |
| `RebaseBranch` | `rebase -b <branch> -d <dest>` | explicit branch |
| `Edit` | `edit <revset>` | moves the WC |
| `SquashInto` | `squash --into <into> [--use-destination-message]` | |
| `CommitPaths` | `commit -m <message> <filesets>` | non-empty filesets required |
| `SquashPaths` | `squash --from <from> --into <into> [--use-destination-message] <filesets>` | via `SquashPaths` spec |
| `SplitPaths` | `split -m <message> <filesets>` | non-empty filesets required (else opens jj's interactive editor) |
| `Absorb` | `absorb [--from <revset>] <filesets>` | empty filesets absorbs everything |
| `SparseSet` | `sparse set --clear --add <p>…` | empty list clears the WC |
| `NewMerge` | `new -m <message> <p1> <p2> …` | multiple parents |
| `Duplicate` | `duplicate <revset>` | |
| `Abandon` | `abandon <revset>` | |

### Git integration, workspaces, operation log

| Method | Runs | Notes |
|---|---|---|
| `GitFetch` | `git fetch` | retried 3× |
| `GitFetchFrom` | `git fetch --remote exact:<remote>` | same retry |
| `GitFetchBranch` | `git fetch --remote origin -b exact:<branch>` | same retry |
| `GitPush` | `git push [-b exact:<bookmark>]` | |
| `GitImport` | `git import` | colocated-repo sync |
| `GitClone` | `git clone <url> <dest> --colocate\|--no-colocate` | dirless, absolute `dest` |
| `GitRemoteList` | `git remote list --ignore-working-copy` | parsed `Remote list`; always ignores the WC, regardless of `ReadOnly` |
| `OpHead` | `op log --no-graph --limit 1 -T id.short()` | capture before a risky sequence |
| `OpLog` | `op log --no-graph --limit <n> -T <template>` | newest first |
| `OpRestore` | `op restore <id>` | |
| `OpUndo` | `op undo` | |
| `WorkspaceList` | `workspace list -T <template>` | |
| `WorkspaceRoot` | `workspace root [--name <name>]` | |
| `WorkspaceAdd` | `workspace add --name <name> -r <base> [--sparse-patterns <mode>] <path>` | via `WorkspaceAdd` spec |
| `WorkspaceForget` | `workspace forget <name>` | |
| `WorkspaceRoots` | fan-out of `workspace root --name <n>` per name | bounded to 8 concurrent calls |
| `RollbackTo` | `op log` (bounded probe) then `op restore <id>` if not diverged | shared rollback protocol behind `Transaction`; fresh cancellation budget |
| `Transaction` | `OpHead` capture, run the closure, `RollbackTo` on error | op-log-rollback scope around a mutation sequence |

### Discovery & raw escape hatches

| Method | Runs | Notes |
|---|---|---|
| `Root` | `root` | |
| `Version` | `--version` | |
| `Capabilities` | `--version`, parsed | |
| `Run` | `jj <args>` in the process cwd (client) or the bound `dir` (`JjAt`); **unguarded** | |
| `RunRaw` | like `Run`, never errors on a non-zero exit; **unguarded** | |

### jj — not modeled (examples) → escape hatch

`config` (`get`/`set`/`list`/`edit` — none of jj's `config` subcommand is typed on this
client), `debug`, `file chmod`/`file track`/`file untrack`, `fix`,
`git init`, `git remote add`/`remove`/`rename`/`set-url` (only `git remote list` is typed, as
`GitRemoteList`), `interdiff`, `next`/`prev`, `parallelize`, `resolve` (interactive; only the
non-interactive listing is typed, as `ResolveList`), `simplify-parents`, `util`. Reach any of
these through `Run`/`RunRaw` — note the doc comment's warning that `Run`/`RunRaw` are
**unguarded**: jj's `--config`/`--config-toml` and user-defined aliases can reach code
execution, so never forward untrusted argv there.

## gh (`VcsToolkit.GitHub` — the GitHub CLI)

Client: `GitHub` / `GitHubAt` (`src/VcsToolkit.GitHub/GitHub.fs`). See
[architecture.md](architecture.md) (the "`VcsToolkit.GitHub` / `VcsToolkit.GitLab` / `VcsToolkit.Gitea`" section).

| Method | Runs | Notes |
|---|---|---|
| `AuthStatus` | `auth status` | exit code only; unscoped across hosts |
| `AuthStatusFor` | `auth status --hostname <host>` | scoped to a `GitHubHost` |
| `RepoView` | `repo view --json …` | |
| `Api` | `api <endpoint>` | raw REST/GraphQL body; flag-guarded endpoint |
| `PrList` | `pr list --state <state> --limit <limit> --json …` | via `PrListOptions`; open PRs ≤100 by default |
| `PrListForBranch` | `pr list --head <head> [--base <base>] --state all --limit 100 --json …` | any state; the 2-arg overload omits `--base` |
| `PrView` | `pr view <n> --json …` | |
| `PrCreate` | `pr create --title … --body … [--head …] [--base …]` | via `PrCreate`; returns the URL |
| `PrMerge` | `pr merge <n> --merge\|--squash\|--rebase [--auto] [--delete-branch]` | via `PrMerge` |
| `PrMarkReady` | `pr ready <n>` | |
| `PrClose` | `pr close <n> [--delete-branch]` | |
| `PrCheckout` | `pr checkout <n>` | mutates the working copy |
| `PrChecks` | `pr checks <n> --json …` | branches on `CheckRun`'s bucket; a checkless PR reads as an empty list |
| `PrReview` | `pr review <n> --approve\|--request-changes\|--comment [--body <body>]` | via `ReviewAction` |
| `PrComment` | `pr comment <n> --body <body>` | returns the comment URL |
| `PrEdit` | `pr edit <n> [--title <title>] [--body <body>]` | via `PrEdit`; ≥1 field required |
| `PrFeedback` | `pr view <n> --json reviews,comments` | |
| `PrDiff` | `pr diff <n>` | parsed `FileDiff list` |
| `IssueList` | `issue list --state <state> --limit <limit> --json …` | via `IssueListOptions`; open ≤100 by default |
| `IssueView` | `issue view <n> --json …` | |
| `IssueCreate` | `issue create --title <t> --body <b>` | returns the issue URL |
| `IssueClose` | `issue close <n>` | |
| `IssueReopen` | `issue reopen <n>` | |
| `IssueComment` | `issue comment <n> --body <body>` | returns the comment URL |
| `RunList` | `run list --limit <n> [--branch <b>] --json …` | Actions runs, newest first |
| `RunView` | `run view <id> --json …` | id is `WorkflowRun`'s database id |
| `RunWatch` | `run watch <id>`, then `run view <id>` | **blocks** until the run finishes; stdout capture bounded to the last 256 lines/256 KiB |
| `WorkflowDispatch` | `workflow run <workflow> [--ref <ref>] [--raw-field key=value …]` | via `WorkflowDispatch`; inputs go through `--raw-field` (never `--field`, whose `@`-syntax reads local files) |
| `RunRerun` | `run rerun <id> [--failed]` | via `RerunScope` |
| `RunCancel` | `run cancel <id>` | |
| `ReleaseList` | `release list --limit 100 --json …` | `Body`/`Url` not fetched — use `ReleaseView` |
| `ReleaseView` | `release view <tag> --json …` | |
| `ReleaseCreate` | `release create <tag> [--title] --notes [--draft] [--prerelease]` | via `ReleaseCreate`; returns the URL |
| `ReleaseDelete` | `release delete <tag> --yes` | confirmation is always supplied |
| `Version` | `--version` | |
| `Capabilities` | `--version`, parsed (`gh ≥ 2.0` floor) | |
| `Run` | `gh <args>` in the process cwd (client) or the bound `dir` (`GitHubAt`); **unguarded** | |
| `RunRaw` | like `Run`, never errors on a non-zero exit; **unguarded** | |

### gh — not modeled (examples) → escape hatch

`browse`, `cache`, `codespace`, `extension`, `gist`, `label`, `org`, `project`, `pr lock`/
`reopen`/`status`, `repo clone`/`create`/`fork`/`edit`/`sync`/`list`, `ruleset`, `search`,
`secret`, `ssh-key`, `variable`, `workflow` (`list`/`view`/`enable`/`disable` — `workflow run`
is modeled as `WorkflowDispatch`). Reach any of these through `Run`/`RunRaw`, or `Api` for a
raw REST/GraphQL call.

## glab (`VcsToolkit.GitLab` — the GitLab CLI)

Client: `GitLab` / `GitLabAt` (`src/VcsToolkit.GitLab/GitLab.fs`). The surface is
**deliberately lean** — auth, project view, and the MR lifecycle — mirroring `GitHub`'s shape,
not its breadth. See
[architecture.md](architecture.md) (the "`VcsToolkit.GitHub` / `VcsToolkit.GitLab` / `VcsToolkit.Gitea`" section).

| Method | Runs | Notes |
|---|---|---|
| `AuthStatus` | `auth status` | exit code only; glab#911 can make this a false positive — see the source doc comment |
| `RepoView` | `repo view --output json` | |
| `Api` | `api <endpoint>` | raw REST/GraphQL body; flag-guarded endpoint |
| `MrList` | `mr list [state flags] --per-page <limit> --output json` | via `MrListOptions`; open ≤100 by default |
| `MrListForBranch` | `mr list --source-branch <branch> --all --output json` | any state |
| `MrView` | `mr view <number> --output json` | `number` is GitLab's `iid` |
| `MrCreate` | `mr create --title … --description … [--source-branch …] [--target-branch …] --yes` | via `MrCreate`; returns the CLI output (URL on success) |
| `MrMerge` | `mr merge <id> --yes --auto-merge=false [--squash\|--rebase]` | via `MergeStrategy`; `--auto-merge=false` overrides glab's default |
| `MrMarkReady` | `mr update <id> --ready` | |
| `MrClose` | `mr close <id>` | |
| `MrCheckout` | `mr checkout <id>` | mutates the working copy |
| `MrComment` | `mr note <id> -m <message>` | body rejected if exactly `-` (glab's stdin/editor sentinel) |
| `MrEdit` | `mr update <id> [--title <title>] [--description <body>] --yes` | via `MrEdit`; ≥1 field required |
| `MrApprove` | `mr approve <id>` | records the current user's approval |
| `MrRevoke` | `mr revoke <id>` | withdraws an approval |
| `MrChecks` | `mr view <id> --output json` (reads `head_pipeline.status`) | bucketed `CiStatus` |
| `MrDiff` | `mr diff <n>` | parsed `FileDiff list` |
| `IssueList` | `issue list [state flags] --per-page <limit> --output json` | via `IssueListOptions`; open ≤100 by default |
| `IssueView` | `issue view <number> --output json` | |
| `IssueCreate` | `issue create --title … --description … --yes` | body rejected if exactly `-`; returns the issue URL |
| `IssueClose` | `issue close <id>` | |
| `IssueReopen` | `issue reopen <id>` | |
| `IssueComment` | `issue note <id> -m <body>` | body rejected if exactly `-` |
| `ReleaseList` | `release list --per-page 100 --output json` | ≤100 |
| `ReleaseView` | `release view <tag> --output json` | |
| `ReleaseCreate` | `release create <tag> [--name …] [--notes …]` | via `ReleaseCreate`; no draft/pre-release (glab has none) |
| `ReleaseDelete` | `release delete <tag> --yes` | confirmation is always supplied |
| `Version` | `--version` | |
| `Capabilities` | `--version`, parsed | |
| `Run` | `glab <args>` in the process cwd (client) or the bound `dir` (`GitLabAt`); **unguarded** | |
| `RunRaw` | like `Run`, never errors on a non-zero exit; **unguarded** | |

### glab — not modeled (examples) → escape hatch

`alias`, `ci` (`status`/`view`/`trace`/`run`/`lint`), `incident`, `label`, `mr rebase`/
`subscribe`/`todo`, `release upload`, `repo archive`/`clone`/`create`/`fork`/`mirror`/
`transfer`, `schedule`, `snippet`, `ssh-key`, `token`, `user`, `variable`, `webhook`. Reach any
of these through `Run`/`RunRaw`, or `Api` for a raw REST/GraphQL call.

## tea (`VcsToolkit.Gitea` — the Gitea/Forgejo CLI)

Client: `Gitea` / `GiteaAt` (`src/VcsToolkit.Gitea/Gitea.fs`). The **narrowest** of the three
forge wrappers — `tea` itself has no single-PR `view`, no current-repo view, no draft toggle
(so no `PrMarkReady`), no PR-checks command, no single-release view, and no `api` escape
hatch; authentication is **ambient only** (`tea login add`, out of band — there is no
`WithToken`/`WithEnvToken` on this client). See
[architecture.md](architecture.md) (the "`VcsToolkit.GitHub` / `VcsToolkit.GitLab` / `VcsToolkit.Gitea`" section).

| Method | Runs | Notes |
|---|---|---|
| `AuthStatus` | `login list --output csv`, non-empty | `tea` has no per-instance auth status |
| `PrList` | `pr list --state <state> --limit <limit> --fields … --output csv` | via `PrListOptions`; tea 0.9.2 has no `--output json` on `pr list` (K-049), so this drives `--output csv` |
| `PrView` | `pr list --state all --limit 50 --page N --fields … --output csv` (paged) + filter | synthesized — `tea` has no single-PR view |
| `PrCreate` | `pr create --title … --description … [--head …] [--base …]` | via `PrCreate`; returns tea's text output, **not** a URL |
| `PrMerge` | `pr merge --style merge\|rebase\|squash <number>` | via `MergeStrategy`; the `--style` flag **must precede** the positional index (K-061) |
| `PrClose` | `pr close <number>` | |
| `PrCheckout` | `pr checkout <number>` | mutates the working copy |
| `PrApprove` | `pr approve <index> [<comment>]` | comment is a bare positional when present |
| `PrReject` | `pr reject <index> <reason>` | Gitea's request-changes review; reason required |
| `PrComment` | `comment <index> <body>` | shared with issues |
| `PrEdit` | *(none — refused before spawning)* | `tea` 0.9.2 has no `pr edit` command at all (K-063); calling this returns a structural `Spawn` error rather than letting an unrecognised `pr edit` silently fall through to `pr list` |
| `IssueList` | `issues list --state <state> --limit <limit> --fields … --output csv` | via `IssueListOptions`; same `--output csv` reason as `PrList` |
| `IssueView` | `issues list --state all --limit 50 --page N --fields … --output csv` (paged) + filter | synthesized — `tea issues <number>` renders Markdown and ignores `--output` |
| `IssueCreate` | `issues create --title … --description …` | returns tea's text output (URL on the final line) |
| `IssueClose` | `issues close <number>` | |
| `IssueReopen` | *(none — refused before spawning)* | `tea` 0.9.2 has no `issues reopen` command |
| `IssueComment` | `comment <index> <body>` | shared with PRs |
| `ReleaseList` | `releases list --limit 100 --output csv` | ≤~50 (Gitea server page cap); same `--output csv` reason |
| `ReleaseCreate` | `release create --tag <tag> [--title …] [--note …] [--draft] [--prerelease]` | via `ReleaseCreate`; returns tea's text output |
| `ReleaseDelete` | *(none — refused before spawning)* | `tea` 0.9.2 has no `release delete` command |
| `Version` | `--version` | |
| `Capabilities` | `--version`, parsed (`tea ≥ 0.9` floor) | |
| `Run` | `tea <args>` in the process cwd (client) or the bound `dir` (`GiteaAt`); **unguarded** | |
| `RunRaw` | like `Run`, never errors on a non-zero exit; **unguarded** | |

There is intentionally **no** `RepoView`, `PrMarkReady`, `PrChecks`, `ReleaseView`, `IssueReopen`, or
`ReleaseDelete` command implementation on `Gitea` — `tea` has no equivalent command; the [`VcsToolkit.Forge`](#facade-escape-hatch-routers)
facade reports these `Unsupported` for the Gitea backend.

### tea — not modeled (examples) → escape hatch

`admin`, `issues labels`, `label`, `login add`/`edit`/`delete` (only `login list`, internally,
via `AuthStatus`), `milestone`, `notification`, `organization`, `releases assets`, `repos
create`/`list`/`delete`, `times`, `whoami`. Reach any of these through `Run`/`RunRaw` — e.g.
flipping a Gitea draft (a `WIP:` title prefix) via `pr edit` at the REST-API level, since `tea`
has no `pr edit` at all.

## Facade escape-hatch routers

`VcsToolkit.Core`'s `Repo` and `VcsToolkit.Forge`'s `Forge` cover only the **portable
intersection** across backends/forges; both expose an escape hatch back to the wrapper level
so dropping to a wrapper-level method (any row above) never needs an extra dependency:

- **`VcsToolkit.Core`** (`src/VcsToolkit.Core/Repo.fs`) — `Repo.Git` / `Repo.Jj` (`Some` only
  for the handle's own backend; the raw client, still `dir`-taking) and `Repo.GitAt` /
  `Repo.JjAt` (the view bound to this handle's `Cwd` — re-anchor with `repo.At(path)` first to
  reach another directory). Its portable `Repo.Remotes()` wraps either `Git.Remotes` or
  `Jj.GitRemoteList` into a facade-owned name/URL DTO. Its portable `Repo.Clone`/`CloneWith`
  (the one associated constructor, since there is no handle yet) wrap either `Git.CloneRepo` or
  `Jj.GitClone` under a unified `CloneOptions` (`CloneKind.Git`/`JjColocated`/
  `JjNonColocated`).
- **`VcsToolkit.Forge`** (`src/VcsToolkit.Forge/Forge.fs`) — `Forge.GitHubClient` /
  `Forge.GitLabClient` / `Forge.GiteaClient` (`Some` only for the handle's own backend), or the
  wrapper client's own `Api`/`Run` for anything beyond that.

A facade operation marked `Unsupported` on a given backend (e.g. a Gitea release-by-tag view)
has **no** wrapper method to drop to either — the CLI itself can't do it; go through the
forge's REST API (`Api`) or your own HTTP client, as the forge table above notes.

## Keeping this index current

A new typed method changes what a row in this index should say. When adding one to a
wrapper's client type, add or update the row in the matching table above — and drop it from
that wrapper's "not modeled" list if it was mentioned there. `scripts/check-command-index.ps1`
(wired into CI, see `.github/workflows/ci.yml`) compares each wrapper's approved public API
 surface (`tests/VcsToolkit.PublicApi.Tests/ApprovedApi/*.approved.txt`) against this file's
 wrapper-specific method rows in both directions, so it fails the build if a public method is
 missing a row or if a row names a method absent from that wrapper's approved API surface.

## See also

- [Architecture](architecture.md) — the package layering, cross-cutting design principles,
  and the escape hatches this index cross-references.
- [Examples cookbook](examples.md) — worked usage of the typed methods this index indexes.
- [MCP server guide](mcp-server.md) — the agent-facing tool surface built on `Repo`/`Forge`.
