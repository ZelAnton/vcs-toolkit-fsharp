# vcs-agent evaluation corpus

This directory stores independent model-forward routing observations and deterministic
offline replay for the vcs-agent interface and the standalone
[`using-vcs-agent` Skill](../../skills/using-vcs-agent/SKILL.md). Ordinary CI never invokes
a model; it verifies the already-recorded observation and its exact input provenance.

## Versioned documents

- schema/eval.v1.schema.json is the machine-readable v1 schema for corpus,
  observation, and normalized result documents. Every object is closed: unknown
  properties are format drift.
- corpus.v1.json holds direct, indirect, negative, unsupported, and mutating
  scenarios across Git/Jujutsu, GitHub/GitLab/Gitea, and
  Windows/Linux/macOS-specific cases.
- outcome-corpus.v1.json is the transport-equivalence golden corpus. Its replays load the exact
  CLI argv and cancellation mode from the corpus, verify each command, complete option set,
  routing classification, error, and exit against the Skill contract, and execute
  `vcs-agent`'s `Main.runWithCancellation` adapter against real repository fixtures. The stdio
  replay launches the built `vcs-mcp`, performs the MCP initialize handshake, and sends the
  matching JSON-RPC `tools/call`: denied, unsupported, and output-limit outcomes compare every
  CallToolResult text-envelope field after removing only insignificant whitespace. MCP request
  cancellation is intentionally recorded as a response-suppressing transport outcome because
  the MCP SDK cancels the handler and pending client task without emitting a CallToolResult; the
  in-process intent-seam replay separately compares its canonical structured cancellation
  envelope with the CLI result. Both replays fail closed on mandatory-scenario, exit/stderr,
  envelope or cancellation transport semantics, adapter identity, and exact SHA-256 provenance.
- offline/observations.v1.json is the model-forward routing observation plus clearly
  separated supplemental command/outcome/evidence fixture fields.
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

The current corpus has 20 scenarios. The routing fields (`shouldActivate`,
`selectedInterface`, and `fallbackReason`) were measured by an independent Codex evaluator with
`fork_turns=none`. That evaluator could read only `SKILL.md` and references routed from it; it was
prohibited from reading corpus expected values or baseline files. Evaluator
`codex-independent-forward-eval/2` matched all 20 intended routes. Its attempt number, start/completion
times, input scope, prohibited input classes, and isolation mode are stored with the observation.
Command validity, call count, outcome, and evidence are not presented as model
measurements: provenance names them as supplemental fixture fields. Observation and result
provenance also includes the exact Skill, reference-contract, and corpus SHA-256 digests, so either
checker rejects a text or identity change until a new blinded observation is recorded.

The exact-path Git and Jujutsu commit scenarios now correspond to the implemented v1
`commit` outcome. Their supplemental outcome evidence remains separate from executable product
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

The recorder does not call a model. It orders the saved forward observations by the corpus, emits UTF-8 without BOM and LF line endings,
and does not include timestamps or machine paths. Identical inputs therefore produce
byte-identical results. After writing a schema-valid normalized result, the recorder
exits non-zero if it contains an expectation mismatch, leaving the result available
for diagnosis. The checker fails on invalid versions or fields, incomplete or reordered
runs, stale metrics, and any expectation mismatch. All three scripts are local text/JSON
processing only and require no network, VCS/forge executable, or model call.

When intentionally changing the contract, add a new versioned schema and document
set rather than silently changing v1. Regenerate the tracked baseline and run the
regression harness in the same change.

## Refreshing the forward observation

After changing `SKILL.md` or a routed reference, use a fresh independent evaluator that has no
conversation fork, can read only the changed Skill and routed references, and cannot read corpus
expected values or existing observations/results. Record only the evaluator's returned activation,
interface, and fallback fields; do not copy expected values into an observation. Update the Skill,
contract, and corpus digests from the exact evaluated bytes, then run the offline recorder and
checker. Keep exploratory or sensitive live artifacts under ignored `live-results/`; review them
before sharing.
