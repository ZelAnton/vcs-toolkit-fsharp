# Agent interface roadmap

## Status and intent

This roadmap evolves VcsToolkit from an MCP-first agent integration into a
transport-neutral agent interface. The existing F# libraries remain the source of
truth for Git, Jujutsu, and forge semantics. A new outcome-oriented command-line
interface becomes the primary executable contract for local agents; an Agent Skill
teaches workflows over that contract; MCP remains a supported adapter rather than
the product's only agent-facing entry point.

The executable and package name is now confirmed as `vcs-agent`, with
`VcsToolkit.Agent` as the reusable library and `VcsToolkit.Agent.Server` as the thin
global-tool adapter. Contract v1 and the read-only `probe`, `inspect`, and `changes`
outcomes and the exact-path checked `commit` mutation are implemented; the remaining outcomes below are reserved in the taxonomy and
return structured `unsupported` until their delivery phases land. The exact current
contract is documented in [vcs-agent v1 contract](agent-interface.md).

## Problem statement

The current `vcs-mcp` server exposes a broad, low-level `repo_*` / `forge_*` tool
catalogue. An agent that also has a shell already knows `git`, `jj`, `gh`, `glab`,
and `tea`, so it can bypass MCP instead of discovering, selecting, and sequencing
many individual tools. The MCP `WriteGate` is a useful policy inside one server
process, but it is not a security boundary when the host still permits unrestricted
VCS mutations through the shell.

The desired interface must therefore optimize for actual agent behavior:

- offer a familiar executable that is easy to select from a shell-capable harness;
- express user outcomes rather than mirror every wrapped CLI method;
- return bounded, versioned, machine-readable results and structured failures;
- preserve the typed parsing, validation, credentials, cancellation, progress, and
  process-tree containment already implemented by VcsToolkit and ProcessKit;
- teach selection, sequencing, verification, and honest fallback through a Skill;
- keep MCP available for hosts where MCP is the appropriate transport;
- measure whether the new interface improves selection and completion instead of
  assuming that packaging alone changes model behavior.

## Architectural decision

### Add a reusable outcome library and a separate `vcs-agent` tool

The transport-neutral application contract belongs in this workspace because it
composes `VcsToolkit.Core`, `VcsToolkit.Forge`, and the typed backend clients. A
small `VcsToolkit.Agent` library should own versioned outcome DTOs and orchestration;
a thin `VcsToolkit.Agent.Server` executable should package that contract as the
`vcs-agent` .NET global tool. The initial contract task may refine these project
names, but it must preserve the reusable-library / thin-adapter boundary.

The new projects must follow the workspace's existing MSBuild contract: cross-project
dependencies use `<Reference>` and `AssemblySearchPaths`, never `ProjectReference`
or `HintPath`; `Directory.Build.props` supplies canonical project-directory
properties; and `VcsToolkit.slnx` carries explicit `BuildDependency` ordering.
No new NuGet package is assumed. JSON, argument parsing, and packaging should reuse
the framework and centrally managed dependencies already present unless a separate
approval is obtained.

The tool is an application facade, not another general VCS wrapper. Its initial
surface should remain small and outcome-oriented:

- `probe` — report the agent contract, tool version, available backends, and optional
  supervisor compatibility without mutating a repository;
- `inspect` — return repository, working-copy, remote, forge, authentication, and
  capability facts in one bounded result;
- `changes` — return a summary or bounded structured diff;
- `commit` — commit exactly named paths and return before/after revision evidence;
- `publish` — perform the checked push and PR/MR publication handshake;
- `ci status` / `ci wait` — report CI for the intended revision and distinguish a
  terminal conclusion from a still-running state;
- conflict operations only after the core workflow is proven; they should reuse the
  existing typed conflict models rather than expose raw marker editing.

There is deliberately no general raw-command escape hatch in this facade.
Unsupported work returns a structured `unsupported` result so the Skill can make a
visible, auditable fallback to a lower-level CLI.

### Depend on ProcessKit through public VcsToolkit seams

The typed clients already run their real subprocesses through ProcessKit and
`VcsToolkit.CliSupport.ManagedClient`, including cancellation, deadlines, output
budgets, progress observation, retry policy, graceful cancellation for network
operations, and hermetic test doubles. Every `git` / `jj` / forge subprocess used by
an agent outcome must preserve that route; the new tool must not introduce direct
`System.Diagnostics.Process` paths for those operations.

`VcsToolkit.Agent` may use the public ProcessKit API directly for top-level
cancellation, deadlines, error classification, or composed work not already covered
by `Core` / `Forge`. A missing primitive must first be demonstrated against the
current public ProcessKit and VcsToolkit surfaces. Only a genuine generic gap should
be proposed upstream; this workspace must not fork containment or teardown semantics
locally.

### Compose with ProcessKit-CLI; do not turn it into a plugin host

ProcessKit-CLI is a single-purpose process supervisor. Its supported integration
surface is its executable contract, reserved exit-code range, and versioned JSONL
lifecycle stream. The initial integration therefore uses executable composition:

```text
processkit-cli run [supervision options] -- vcs-agent <operation> [arguments]
```

This gives a long-running `publish` or `ci wait` workflow a durable run id, bounded
capture, hard/idle deadlines, lifecycle JSONL, inspection, cancellation, and
out-of-band supervision. Short, already-contained queries such as `inspect` can run
`vcs-agent` directly. Inside either form, VCS children remain managed through
ProcessKit by the VcsToolkit clients.

Do not add dynamic-library loading, .NET plugin discovery, or domain-specific VCS
commands to ProcessKit-CLI. Do not depend on its implementation assemblies. An
upstream request is justified only if interoperability testing demonstrates a
concrete gap that cannot be solved by its published binary contract without
duplicating lifecycle semantics or losing required evidence. Any such request must
be additive and generic to process supervision, not specific to VcsToolkit.

## Contract principles

### Machine output

The first implementation task must define and golden-test a versioned envelope. The
exact field names are part of that task, but the contract must distinguish:

- contract version and operation;
- success, unsupported, denied, invalid-input, backend, forge, authentication,
  timeout, cancellation, output-limit, and external-command failures;
- repository root, backend, forge kind, and relevant before/after revision identity;
- result data from bounded typed DTOs;
- warnings and a machine-readable fallback reason;
- terminal versus still-running state for CI and supervised operations.

Machine output goes to stdout; diagnostics go to stderr. Secrets, credentialed URLs,
and machine-local paths not required by the result are redacted. Large content is
refused or explicitly budgeted, never silently truncated into valid-looking JSON.
The implementation should reuse the repository's existing F# JSON conventions and
must keep all externally visible discriminated-union cases stable under the declared
contract-version policy.

### Mutation policy

The CLI is not a substitute for host permissions, but it must make safe behavior the
easy behavior:

- mutations require an explicit operation and explicit repository;
- `commit` accepts an exact non-empty logical path set, expands a reported rename to one
  old/new backend pair before mutation, and preserves unrelated changes through
  `Repo.CommitPaths`;
- a late mutating failure is explicitly ambiguous and retains bounded preflight plus
  best-effort postflight identity without claiming an unverified created revision;
- push/publication reports the local revision, remote revision, forge/account
  identity, and resulting PR/MR;
- CI success is accepted only for the intended revision and a terminal conclusion;
- destructive or remote-publishing operations expose their intent in machine output;
- no command silently changes backend, forge, account, branch, or fallback path;
- capability differences are explicit: an unavailable operation on Git, Jujutsu,
  GitHub, GitLab, or Gitea returns structured `unsupported` rather than a partial
  imitation;
- unsupported operations fail structurally before the Skill considers raw CLI use.

### Skill behavior

Ship one umbrella Skill first, not one Skill per command. Its trigger description
must cover repository inspection, change preparation, publication, CI verification,
and conflict handling while excluding ordinary file search and editing.

The Skill must:

1. prefer `vcs-agent` when an operation is supported;
2. run `probe` / `inspect` before the first mutation in a workflow;
3. preserve unrelated workspace state and select exact paths;
4. use ProcessKit-CLI supervision for operations whose duration or descendant risk
   warrants lifecycle evidence;
5. verify the resulting local revision, remote revision, PR/MR, and terminal CI as
   applicable;
6. use raw `git` / `jj` / forge CLIs only after `unsupported`, a missing executable,
   or a documented need for exact low-level diagnostic output;
7. report the fallback reason rather than silently bypassing the preferred interface.

The Skill cannot enforce this boundary by itself. Hosts that require prohibition of
raw VCS mutations must enforce that with their command policy, sandbox, or approvals.

## Evaluation strategy

Before optimizing Skill metadata, maintain a versioned golden prompt corpus with
expected routing and outcome evidence:

- direct prompts that name VcsToolkit or `vcs-agent`;
- indirect prompts such as "commit only these files" or "publish and wait for CI";
- negative prompts such as source search or file editing that should not invoke the
  VCS interface;
- unsupported prompts that should fall back visibly;
- mutation prompts with unrelated dirty files;
- Git, Jujutsu, GitHub, GitLab, and Gitea variants where capabilities differ;
- Windows, Linux, and macOS path/process cases where the executable contract can
  differ accidentally.

Record at least:

- preferred-interface selection rate;
- false activation rate on negative prompts;
- raw-CLI bypass rate and classified fallback reason;
- invalid command/argument rate;
- number of calls needed to complete the outcome;
- preservation of unrelated workspace state;
- exact-revision publication and terminal-CI correctness;
- denied or unsafe mutation attempts.

The corpus and result schema must be usable without a live model in ordinary CI.
Live-model evaluations are an opt-in evidence tier whose results are recorded
separately, not a nondeterministic merge gate.

The versioned v1 baseline lives in evals/vcs-agent/. Its corpus uses
selectedInterface for preferred-interface choice, shouldActivate for negative
prompt precision, an explicit fallbackReason for every raw-CLI route,
commandValid and maxCalls for command/call quality, and evidence fields for
unrelated-change preservation, exact-revision publication, terminal CI for that
revision, and unsafe-mutation denial. The normalized result derives the selection,
false-activation, raw-fallback, invalid-command, preservation, publication,
terminal-CI, denial, and call-count metrics from those fields.

Ordinary validation is fully offline:

    pwsh ./scripts/record-vcs-agent-eval.ps1
    pwsh ./scripts/check-vcs-agent-eval.ps1
    pwsh ./scripts/test-vcs-agent-eval.ps1

The recorder contains no timestamp or machine path and emits runs in corpus order,
so identical inputs are byte-reproducible. The checker rejects schema/format drift,
stale metrics, incomplete or reordered runs, and expectation mismatches. Optional
live observations belong under the ignored evals/vcs-agent/live-results/ tier and
must be passed explicitly; the ordinary CI job never reads that directory.

## Delivery phases

### Phase 0 — Evidence and contract

Implemented: the offline evaluation baseline, v1 envelope/error/exit/output/redaction and
compatibility contract, project/tool names, package/build wiring, and deterministic `probe`
are committed and covered by golden/hermetic validation.

- Establish the golden prompt corpus, routing policy, metrics, and repeatable result
  recorder.
- Freeze the v1 executable/project names, command taxonomy, JSON/error envelope, exit
  behavior, output budgets, and compatibility policy.
- Record the ProcessKit and ProcessKit-CLI boundaries above as an architecture
  decision backed by their current public contracts.
- Define how the new projects participate in central package management, assembly
  lookup, solution build ordering, global-tool packaging, API snapshots, docs, and
  release artifacts without introducing an unapproved dependency.

Exit condition: the interface can be implemented and evaluated without depending on
undocumented behavior or an undecided extension-host design.

### Phase 1 — Read-only CLI

Implemented: `probe`, typed repository and forge `inspect`, and summary or structured-diff
`changes` are available through the reusable library and thin global-tool adapter. Their
Git/Jujutsu, forge-status, cancellation, redaction, and output-budget behavior is covered by
golden and scripted-runner tests.

- Add the reusable outcome library, thin global-tool project, and `probe`.
- Implement `inspect` and `changes` over `VcsToolkit.Core` / `VcsToolkit.Forge`.
- Cover Git and Jujutsu, forge-present and forge-absent repositories, unsupported
  capabilities, output ceilings, redaction, cancellation, and scripted runners.

Exit condition: an agent can understand a repository and its changes through a
small, stable JSON surface without calling raw VCS commands.

### Phase 2 — ProcessKit-CLI interoperability

Implemented against the published ProcessKit-CLI `v0.3.3` executable contract. The proof
pins release-asset SHA-256 digests, fail-closed probes every surface it uses, installs the
packed `vcs-agent` tool, and validates independent agent-result and JSONL lifecycle streams.

- The preflight requires schema v1, reserved exit range `100-119`, run/deadline/capture/
  detach surfaces, control cancellation/kill/inspection/waiting, resource-summary capability,
  and embedded event-schema validation before it launches a payload.
- The cross-binary matrix covers successful and non-success agent exits, overall and idle
  timeout classification, bounded capture, detached cancellation, fail-closed detached cleanup,
  and nested ProcessKit-CLI containment teardown of a live PID/start-time descendant identity
  on Windows, Linux, and the published Apple Silicon macOS target.
- Fixtures use disposable system-temp directories and validate every JSONL stream with the
  published schema. Completed streams require one terminal `runner_exit`, no cleanup read/kill
  errors, and no independently confirmed survivor. The POSIX process-group fallback may report
  an already-killed, not-yet-reaped known PID in its terminal member snapshot; that narrow case
  additionally requires exact PID/start-time disappearance plus bounded machine-readable
  confirmation that the run is no longer registered. The inner stream may be nonterminal when
  the outer boundary ends it, so teardown then depends on the independently confirmed outer
  cleanup plus exact identity checks.
  Failure-path cleanup capability-checks and verifies kill/wait plus terminal lifecycle and
  exact identity disappearance before deleting scratch evidence; any unconfirmed cleanup fails
  closed and retains that evidence path.
- The published `process_group` contract enumerates tracked group leaders rather than every
  descendant, so readiness follows that scope while cleanup still requires every exact identity
  to disappear. Published `v0.3.3` does not guarantee inner-owned group cleanup when outer
  teardown terminates the inner runner; the evidence-backed minimal reproducer and generic
  additive request are recorded in
  [`processkit-cli-nested-posix-containment-request.md`](processkit-cli-nested-posix-containment-request.md).
  No ProcessKit-CLI implementation assembly is linked, and the proof does not skip this gap.

Exit condition: the two tools compose without linking private ProcessKit-CLI code
and without weakening ProcessKit containment.

### Phase 3 — Checked mutations and publication

- Exact-path commit is implemented with fail-before-mutation validation and output-budget
  preflight, before/after revision evidence, unchanged branch/bookmark and unrelated-dirt
  postconditions, and safe replay after an ambiguous completed mutation. Hermetic and real
  Git/Jujutsu sandbox tests are the executable evidence.
- Add checked push and PR/MR publication with explicit account/forge identity.
- Add exact-revision CI status and terminal wait where typed backend capabilities
  support them, returning structured `unsupported` elsewhere.
- Preserve cancellation and inactivity handling inherited from ProcessKit.
- Prove idempotent recovery where a remote step succeeded before a later step failed.

Exit condition: the common "prepare, publish, wait for CI" workflow completes without
raw CLI use on supported backend/forge combinations and cannot claim success for the
wrong revision.

### Phase 4 — Skill and packaging

- Add the umbrella Skill, references, and factual drift tests against the built CLI.
- Evaluate the Skill against the golden corpus and tune its trigger/fallback language.
- Package installation metadata only after the standalone Skill is stable.
- Integrate the `vcs-agent` global tool into the existing pack/release verification
  without weakening checks for the current `VcsToolkit.*` and `vcs-mcp` artifacts.
- Document host-level enforcement separately from Skill guidance.

Exit condition: direct and indirect repository prompts select the intended workflow
at an acceptable measured rate, while negative prompts remain precise.

### Phase 5 — MCP convergence

- Move shared outcome orchestration below both CLI and MCP adapters, with
  `VcsToolkit.Agent` as the transport-neutral boundary rather than duplicating
  workflows inside either executable.
- Reduce MCP discovery noise with capability-aware tool registration.
- Add explicit server instructions and intent-oriented metadata.
- Keep low-level MCP operations only where composition is materially useful; prefer
  outcome tools for common workflows.
- Run the same golden corpus against CLI+Skill and MCP transports.

Exit condition: transport choice does not change semantics, safety checks, or
evidence, and MCP no longer needs to expose unavailable or disallowed tools merely
to advertise them.

## Initial backlog

The first executable tranche is tracked in `.work/Tasks_Queue.md`:

- T-205 — evaluation corpus and routing baseline;
- T-206 — `VcsToolkit.Agent` / `vcs-agent` skeleton and versioned machine contract;
- T-207 — read-only `inspect` and `changes` outcomes;
- T-208 — ProcessKit-CLI composition and nested-containment proof;
- T-209 — exact-path checked commit;
- T-210 — checked publication and exact-revision terminal CI;
- T-211 — umbrella Skill, packaging, and factual/evaluation tests;
- T-212 — MCP convergence over shared outcome services.

The dependency graph in the queue is authoritative. Later phases should be expanded
only after the initial measurements and interoperability proof expose real gaps.

## Upstream decision gates

### ProcessKit

No upstream change is assumed. File a request only when the current public API cannot
provide a generic containment, cancellation, deadline, output, progress, or
observation primitive needed by the outcome service. The request must include a
minimal reproducer, platform matrix, required semantics, and why composition in this
workspace cannot solve it.

### ProcessKit-CLI

No runtime extension API is requested initially. Reconsider only after Phase 2, and
only if all of the following are true:

1. `processkit-cli run -- vcs-agent ...` cannot preserve required lifecycle or result
   evidence;
2. the gap is generic to supervised external tools rather than VCS-specific;
3. an additive binary-contract change is insufficient;
4. the benefit exceeds the compatibility, discovery, trust, packaging, and
   cross-platform costs of an extension mechanism.

Dynamic .NET plugins are explicitly out of scope unless a separate future design
solves API/ABI compatibility, signing and trust, version negotiation, isolation,
installation, unloading, and Windows/Linux/macOS loading behavior. External
executable composition remains the default because it already supplies process
isolation and independent versioning.
