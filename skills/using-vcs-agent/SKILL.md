---
name: using-vcs-agent
description: Use for repository inspection, VCS change review, exact-path commits, Git or Jujutsu publication, pull or merge request recovery, exact-revision terminal CI verification, and conflict diagnosis through vcs-agent. Do not use for ordinary file search, source reading, or editing files without a repository or revision outcome.
---

# Use vcs-agent for repository outcomes

Prefer the versioned `vcs-agent` interface whenever its `probe` capability matrix
supports the requested outcome. This Skill is workflow guidance. The host's sandbox,
command policy, and approval boundary remain responsible for enforcing mutation and
raw-CLI restrictions.

Before selecting flags, interpreting an exit, or falling back, read
[`references/contract.v1.json`](references/contract.v1.json). The repository validates
that reference against the built F# tool and the committed ProcessKit-CLI proof.

## Match only repository outcomes

Use this Skill for repository identity and working-copy inspection, bounded VCS change
review, exact-path commit, exact revision publication, pull or merge request creation or
recovery, exact-revision CI status or waiting, and conflict-oriented diagnosis. Do not
activate it for ordinary source search, file reading, or file editing when the request
does not ask for a repository-state or revision outcome.

## Inspect before the first mutation

1. Run `vcs-agent probe`, then `vcs-agent inspect --repo <PATH>` before the first
   mutation in a workflow. Treat a non-v1 contract or a structured error as data, not as
   permission to reinterpret the response.
2. Record the canonical repository, backend, current revision, source branch or
   bookmark, remote, forge, and authenticated account required by the outcome. Stop on
   an ambiguity instead of silently switching an identity.
3. Run `vcs-agent changes --repo <PATH> --view summary` before selecting work. Preserve
   unrelated state. For a commit, pass one literal repository-relative leaf path per
   `--path`; never substitute a whole-tree stage or commit.

Authorization denial does not suppress Skill activation. Use the read-only
`vcs-agent` probe, inspection, and change-review path to establish the exact repository
outcome, then refuse the mutation. Report the route as `vcs-agent` inspection with a
denied outcome; do not select `none` or raw CLI merely because mutation is prohibited.

## Mutate once and verify

- Immediately before each commit or publication, confirm that the host authorization
  still covers that exact mutation and identity set. Earlier inspection, capability, or
  approval evidence does not authorize a changed path set, revision, remote, or target.
- Commit with the explicit repository, exact selected paths, and message. Accept success
  only when the returned evidence proves the source revision, one created revision, and
  the created revision's own path set. Re-inspect the workspace to confirm unrelated
  changes remain.
- Publish with the exact local revision, source branch or bookmark, remote, forge,
  account, target, title, and body. Accept an existing PR or MR only when the structured
  recovery evidence proves the same repository, source, target, forge, account, and head
  revision.
- After publication, verify the remote revision and PR or MR identity from the result.
  Query or wait for CI using that exact published revision. Accept CI success only when
  every reported run is terminal and belongs to the same revision.
- On mismatch, unknown state, or a late ambiguous failure, stop and report the evidence.
  Do not retry an irreversible step until a fresh `inspect` establishes current state.

## Supervise long or descendant-risk operations

Run `probe`, short `inspect` or `changes`, commit, publish, and ordinary `ci status`
directly when the host already owns their lifetime. Use the supported ProcessKit-CLI
route when an operation is expected to last at least 60 seconds or has concrete
descendant-cleanup risk. A `ci wait` budget of 60 seconds or more meets the duration
threshold.

Before the first supervised command, run `processKitCli.preflight.argvPrefix` from the
reference, appending `processKitCli.preflight.repeatForEachRequiredSurface` once for every
entry in `requiredSurface`. Require every fact in `preflight.success`, choose a unique run id,
then launch the detached `supervisedRunTemplate`. Poll `inspectTemplate` within a bounded host
deadline until readiness or a terminal state. Use `cancelTemplate` for requested cancellation,
`waitTemplate` for the terminal outcome, and `killTemplate` only as bounded fail-closed recovery
when cancellation cannot complete cleanup. Validate with `validateEventsTemplate`. Consume the
agent envelope from bounded capture and the lifecycle from JSONL; success requires the terminal
`runner_exit`, not child output alone.
Readiness is mechanism-aware: `process_group` inspection attests the tracked root or
leader, not every descendant. Track every known PID plus start-identity separately,
extend that set from live inspection, and require exact-identity cleanup. Completion
succeeds only when cleanup reports zero remaining members, `readError=false`,
`killError=false`, and every known exact identity is gone. Any unavailable or malformed
inspection, control, wait, event, or identity evidence fails closed.

## Fall back visibly and narrowly

Raw `git`, `jj`, `gh`, `glab`, or `tea` is permitted only after one of these classified
facts:

- `unsupported`: `vcs-agent` returned `unsupported` or its capability matrix excludes
  the backend, forge, or operation;
- `missing-executable`: the `vcs-agent` executable itself is unavailable;
- `diagnostic-output-required`: exact low-level diagnostic output unavailable from the
  outcome interface is necessary.

Before running raw CLI, emit a structured reason with `fallbackReason`, the observed
evidence, and `nextInterface`. Preserve the tool's more specific fallback fact when it
exists (`operation-not-implemented`, `missing-executable`, `unsupported-backend`,
`unsupported-forge`, or `raw-diagnostic-required`). A denied, invalid-input, backend,
forge, authentication, timeout, cancellation, output-limit, external-command, or
revision-mismatch result is not permission to bypass the interface.

For Gitea publication, activate this Skill and probe first. The v1 publication matrix
excludes Gitea, so report observed `unsupported-forge`, `fallbackReason: unsupported`,
and `nextInterface: raw-cli` before selecting `tea`; do not report `vcs-agent` as the
publication interface merely because it performed the probe.

## Install from this checkout

Build and install the executable first:

```text
dotnet pack VcsToolkit.slnx --configuration Release --output ./artifacts
dotnet tool install --global vcs-agent --version 0.1.0 --add-source ./artifacts
```

For Codex, copy this complete `skills/using-vcs-agent` directory to
`$CODEX_HOME/skills/using-vcs-agent` (or `~/.codex/skills/using-vcs-agent` when
`CODEX_HOME` is unset). Keep the reference beside `SKILL.md`; installing only the
entrypoint breaks the factual contract. Validate the copied directory with the same
repository checker before relying on it.
