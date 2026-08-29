# vcs-agent evaluation corpus

This directory is the offline routing and evidence baseline for the
vcs-agent interface and the standalone
[`using-vcs-agent` Skill](../../skills/using-vcs-agent/SKILL.md). It measures selection and outcome evidence; it does not invoke
an agent model or claim that the synthetic observations are live-model measurements.

## Versioned documents

- schema/eval.v1.schema.json is the machine-readable v1 schema for corpus,
  observation, and normalized result documents. Every object is closed: unknown
  properties are format drift.
- corpus.v1.json holds direct, indirect, negative, unsupported, and mutating
  scenarios across Git/Jujutsu, GitHub/GitLab/Gitea, and
  Windows/Linux/macOS-specific cases.
- offline/observations.v1.json is the deterministic golden input.
- offline/results.v1.json is the recorded baseline checked in ordinary CI.
- fixtures/ contains negative recorder/checker patches and invalid documents used
  by the regression harness.
- live-results/ is an ignored opt-in tier. Ordinary recording, checking, and CI
  never enumerate or read it.

The corpus stores the expected preferred interface, explicit fallback reason,
command validity, maximum call count, outcome, unrelated-change preservation,
exact-revision publication, terminal CI for that revision, and unsafe-mutation
denial. Normalized results store the observed values, expectation mismatches, and
aggregate rates/counts.

The current corpus has 20 scenarios: 12 supported direct/indirect outcomes select the preferred
interface, four negative source search/read/edit prompts remain inactive, and four unsupported
cases use a classified visible fallback. The recorded baseline has zero expectation mismatches,
including the unrelated-state, exact publication, terminal-CI, and unsafe-denial evidence gates.

The exact-path Git and Jujutsu commit scenarios now correspond to the implemented v1
`commit` outcome. Their synthetic routing baseline remains separate from executable product
evidence: `VcsToolkit.Agent.Tests` supplies hermetic failure checks and real backend sandboxes
that prove unrelated changes are preserved, selected renames expand atomically, and a late
failure returns inspectable ambiguous evidence before a replay can capture unrelated work.
The GitHub and GitLab publication scenarios correspond to the implemented checked `publish`
plus exact-revision CI outcomes. Their required publication/terminal-CI fields remain synthetic
routing expectations; executable Agent, forge-client, and sandbox tests separately prove exact
argv, remote-revision verification, recovery, mismatch, inactivity, and cancellation behavior.

## Offline commands

From the repository root:

    pwsh ./scripts/record-vcs-agent-eval.ps1
    pwsh ./scripts/check-vcs-agent-eval.ps1
    pwsh ./scripts/test-vcs-agent-eval.ps1

After building `vcs-agent`, also validate that the Skill's linked facts and executable examples
match the product contract:

    pwsh ./scripts/check-vcs-agent-skill.ps1 -VcsAgentPath ./src/VcsToolkit.Agent.Server/bin/Release/net10.0/vcs-agent
    pwsh ./scripts/test-vcs-agent-skill.ps1 -VcsAgentPath ./src/VcsToolkit.Agent.Server/bin/Release/net10.0/vcs-agent

The recorder orders runs by the corpus, emits UTF-8 without BOM and LF line endings,
and does not include timestamps or machine paths. Identical inputs therefore produce
byte-identical results. After writing a schema-valid normalized result, the recorder
exits non-zero if it contains an expectation mismatch, leaving the result available
for diagnosis. The checker fails on invalid versions or fields, incomplete or reordered
runs, stale metrics, and any expectation mismatch. All three scripts are local text/JSON
processing only and require no network, VCS/forge executable, or live model.

When intentionally changing the contract, add a new versioned schema and document
set rather than silently changing v1. Regenerate the tracked baseline and run the
regression harness in the same change.

## Optional live evidence

Live-model observations are evidence for later tuning, not a merge gate. Put them
under live-results/, pass their path explicitly to the recorder, and write their
normalized result to a separate explicit path. Never replace
offline/observations.v1.json or offline/results.v1.json with a live run, and review
live files for sensitive or machine-specific content before sharing them.
