# vcs-agent v1 contract

`vcs-agent` is the outcome-oriented command-line interface over the reusable
`VcsToolkit.Agent` library. The library owns the transport-neutral contract and
application outcomes; `VcsToolkit.Agent.Server` is only the argv/stdout/stderr/exit-code
adapter packaged as a .NET global tool.

The implemented operations are the read-only `probe`, `inspect`, `changes`, `ci status`, and
`ci wait` outcomes plus the checked `commit` and `publish` mutations. The broader delivery
sequence is documented in the [agent interface roadmap](agent-interface-roadmap.md).

## Installation

After the first release:

```sh
dotnet tool install --global vcs-agent
```

To evaluate the current source tree:

```sh
dotnet pack VcsToolkit.slnx --configuration Release --output ./artifacts
dotnet tool install --global vcs-agent --version 0.1.0 --add-source ./artifacts
```

## Operations

| Command | Envelope operation | v1 availability | Mutating |
|---|---|---|---|
| `probe` | `probe` | supported | no |
| `inspect` | `inspect` | supported | no |
| `changes` | `changes` | supported | no |
| `commit` | `commit` | supported | yes |
| `publish` | `publish` | supported | yes |
| `ci status` | `ci.status` | supported | no |
| `ci wait` | `ci.wait` | supported | no |

There is no general raw-command operation. An unavailable backend or forge capability returns
typed `unsupported`; an unrecognized or incomplete command returns `invalid-input` instead.

## Probe

```sh
vcs-agent probe
vcs-agent probe --output-budget 4096
```

`probe` reports the contract and tool versions, the complete operation taxonomy and
current availability, the Git/Jujutsu and forge families the outcome layer is designed
to compose, and the optional ProcessKit-CLI supervision protocol. It does not inspect
the working directory, a repository, installed executables, environment variables, or
the network. That property is asserted at the server boundary by
`VcsToolkit.Agent.Server.Tests`; the exact response bytes are committed as a golden in
`VcsToolkit.Agent.Tests`.

The response has this shape (the actual `toolVersion` comes from the built tool's
assembly metadata):

```json
{
	"contractVersion": "1",
	"operation": "probe",
	"status": "success",
	"terminal": true,
	"data": {
		"kind": "probe",
		"toolName": "vcs-agent",
		"toolVersion": "0.1.0",
		"operations": [
			{ "name": "probe", "availability": "supported", "mutating": false },
			{ "name": "inspect", "availability": "supported", "mutating": false }
		],
		"backends": ["git", "jj"],
		"forges": ["github", "gitlab", "gitea"],
		"supervisor": {
			"mode": "processkit-cli-run",
			"lifecycleProtocol": "jsonl-v1",
			"required": false
		}
	},
	"error": null,
	"warnings": [],
	"fallbackReason": null
}
```

The example shortens the `operations` array for readability; the real `probe` contains
every row from the operation table above in that stable order.

## Inspect

```sh
vcs-agent inspect --repo .
vcs-agent inspect --repo ./worktree --output-budget 16384
```

`inspect` opens the explicit repository through `VcsToolkit.Core.Repo` and returns one
typed snapshot containing:

- the canonical repository root and detected `git` or `jj` backend;
- the current revision and optional branch;
- dirty, changed-file, conflict, operation, and optional upstream/ahead/behind facts;
- configured remote names and URLs;
- forge status, kind, authentication, CLI version, and the supported PR, issue, and
  release capabilities.

Forge status is `absent` when no remote identifies a forge, `unsupported` when a remote
identifies an unknown forge family, `unauthenticated` when the known forge CLI reports no
active identity, and `available` when it is authenticated. Known forge CLI failures remain
typed errors; they are not converted to absent capabilities.

The reusable API accepts an `InspectRequest` and a `CancellationToken`. Its
`WithOutputLimit` member sets the same limit used for backend capture and final JSON
rendering by the executable.

## Changes

```sh
vcs-agent changes --repo . --view summary
vcs-agent changes --repo . --view diff --output-budget 32768
```

`changes` defaults to `summary`. Summary mode reports changed paths and a separately scoped
`diffStat` aggregate. On Git, `paths` includes untracked entries while `diffStat` describes
the tracked `HEAD` diff; on Jujutsu, the working-copy diff includes automatically tracked new
files. Keeping those values in separate typed fields prevents consumers from treating the Git
stat count as the size of the broader path list. `--view diff` reports parsed unified-diff
files, headers, hunks, and typed context/addition/deletion lines. Both modes compose the
backend-neutral `VcsToolkit.Core.Repo` facade and work with Git and Jujutsu.

The reusable API selects the same representations through `ChangesRequest.Summary` and
`ChangesRequest.StructuredDiff`. `ChangesData` is a discriminated union, so a summary and a
structured diff cannot coexist or both be absent. `WithOutputLimit` and the supplied
`CancellationToken` have the same semantics as the CLI flags and cancellation boundary.

## Commit

```sh
vcs-agent commit --repo . --path src/App.fs --path tests/App.Tests.fs --message "Update app"
vcs-agent commit --repo ./worktree --path docs/guide.md --message "Update guide" --output-budget 16384
```

The CLI requires an explicitly supplied, non-empty `--repo` before it opens a repository;
unlike `inspect` and `changes`, which intentionally default an omitted `--repo` to the process
working directory, `commit` never uses that default. It also requires a non-empty NUL-free message and one or more repeated `--path`
values in the forward-slash, repository-root-relative form returned by
`changes`. Empty, duplicate, rooted, backslash, empty-segment, `.` and `..` traversal paths are rejected as
`invalid-input` before the repository is opened or any backend command runs. Every selected
path must be present in the preflight changed-path set. Selecting the new path of a reported
rename expands before mutation to the old/new backend path pair; a rename that cannot be
represented by that pair is refused before `Repo.CommitPaths`. On Git, a newly untracked path is
reported by `changes`, but the existing `Repo.CommitPaths` / `git commit --only` contract
requires a path already known to Git; asking to commit an untracked path therefore returns a
structured `backend` failure without adding it implicitly. Jujutsu automatically tracks new
working-copy files.

After validation, `commit` reads the current snapshot and changed paths, refuses conflicts or
another in-progress repository operation, and proves that the prospective complete success
envelope fits the requested stdout budget. Only then does it invoke the existing
`Repo.CommitPaths`; it does not select or switch a backend, branch, or bookmark. Its only path
expansion is the preflight old/new pair for one selected rename. Postflight requires the
branch/bookmark identity to remain unchanged, every selected backend path to leave the changed
set, and the unrelated changed-path set to remain identical. On Git, the observed candidate must
be the single direct child of the source revision (or the sole root of an unborn repository), and
`paths` is read from that candidate's own parent-to-candidate diff. On Jujutsu, it is read from the
created revision's own parent diff. The observed path set must equal the exact backend path set
before success.

Commit data binds the outcome to the canonical `root`, `backend`, `sourceRevision`, and
`sourceBranch`. It distinguishes logical `requestedPaths` from the `backendPaths` sent to
`Repo.CommitPaths`; `paths` comes from the observed created-revision diff rather than echoing the
request. `observedRevision` and `observedBranch` record bounded postflight facts;
`observedCreatedRevision` is present only after backend-specific evidence shows that the relevant
revision identity changed. `createdRevision` is populated only after the direct-revision proof
above and an exact match between its own observed paths and `backendPaths`. `completion` is
`verified` on success.

When unrelated dirt remains, the envelope includes an `unrelated-changes-preserved` warning. A
backend failure, timeout, cancellation, or failed postflight is terminal and structured. Once a
mutating call can have started, bounded best-effort postflight uses an independent cleanup token
and returns the same commit data with `completion: "ambiguous"`; it never upgrades an observed
candidate to `createdRevision` without exact revision-path verification. A caller can inspect that
evidence before a replay, and a replay after a completed mutation stops with `invalid-input`
instead of committing unrelated work. `VcsToolkit.Agent.Tests` proves these behaviors with
hermetic no-spawn/timeout/path-mismatch checks and real Git/Jujutsu commit and rename sandboxes.

The reusable API uses `CommitRequest.Create(repositoryPath, paths, message)`, optional
`WithOutputLimit`, and a `CancellationToken`; its result and the CLI renderer share the same
v1 envelope and full-output budget.

## Publish

```sh
vcs-agent publish --repo . --branch feature --remote origin \
  --revision 0123456789abcdef0123456789abcdef01234567 \
  --forge github --account alice --target main --title "Add feature" --body "Details"
```

`publish` requires every identity input shown above except the optional body. Singleton options
cannot be repeated. The revision must be a full 40- or 64-hex commit id, the selected local
branch/bookmark must resolve to that revision, and the named remote must exist and identify the
selected public forge host. The authenticated GitHub or GitLab username must match `--account`.
Gitea and an unclassifiable/self-hosted remote return typed `unsupported` where the available CLI
surface cannot prove that identity. Checked Jujutsu publication currently supports only an
explicit `origin` remote; another named Jujutsu remote is refused instead of being silently
substituted.

Before the first remote mutation, the outcome records the canonical root, detected backend,
forge/account, source branch/bookmark, remote, requested local revision, and observed remote
revision. Git publishes the exact revision with an explicit revision-to-branch refspec. After the
push (or when the remote was already exact), it fetches the named remote and requires the observed
remote ref to equal the requested revision. A mismatch cannot produce success.

The workflow then queries open PRs/MRs for the exact source and target pair. One existing match is
returned with disposition `existing`; when there is no match, it creates one and queries again; multiple matches
are an error. This makes a retry after an already-completed push or PR/MR creation idempotent and
prevents duplicate creation. A late push, fetch, or forge error carries bounded pre/post evidence
with `completion: "ambiguous"`; only a proven remote revision plus one exact PR/MR yields
`completion: "verified"`.

The reusable form is `Agent.publish (PublishRequest.Create(...)) cancellationToken`. It applies
the same validation, cancellation, output-budget, and evidence rules before returning an
`AgentEnvelope`.

## Exact-revision CI

```sh
vcs-agent ci status --repo . --branch feature --remote origin \
  --revision 0123456789abcdef0123456789abcdef01234567 \
  --forge github --account alice

vcs-agent ci wait --repo . --branch feature --remote origin \
  --revision 0123456789abcdef0123456789abcdef01234567 \
  --forge gitlab --account alice --poll-seconds 5 \
  --deadline-seconds 1800 --inactivity-seconds 600
```

Both commands require the same explicit repository, branch/bookmark, remote, full revision,
forge, and account identity. Preflight fetches the selected remote and proves that its selected ref
is at the requested revision before consulting CI. GitHub uses `gh run list --commit <revision>`;
GitLab uses the project pipelines API with its `sha` filter. Every returned run is checked again
for both the exact revision and selected branch. Gitea exact-revision CI is typed `unsupported`.

CI data distinguishes `no-runs`, `pending`, terminal `success`, `failure`, `cancelled`, and
`skipped`, plus a terminal structured `revision-mismatch` error if the forge returns another
revision or branch. Unknown completed conclusions fail closed as `failure`; only completed/passing
evidence is `success`. `ci status` returns one observation. `ci wait` polls the same source until a
terminal state, caller cancellation, the overall deadline, or the period without a changed state or
run signature reaches the inactivity deadline. Timeout and cancellation preserve the last bounded
CI observation when one exists.

The reusable requests are `CiStatusRequest.Create(...)` and `CiWaitRequest.Create(...)`.
`CiWaitRequest` additionally exposes `WithPolling`, `WithDeadline`, and
`WithInactivityDeadline`; all three durations must be positive.

## Envelope and stream rules

Every invocation writes exactly one LF-terminated JSON document to stdout. Property order
and spelling are golden-tested. The top-level fields are:

- `contractVersion`: currently the string `"1"`;
- `operation`: the canonical operation name;
- `status`: `success` or `error`;
- `terminal`: whether this outcome is terminal rather than still running;
- `data`: operation-specific typed data, including bounded ambiguous commit/publication evidence
  and the last CI observation on a bounded wait stop; otherwise `null` on error;
- `error`: structured error details, or `null` on success;
- `warnings`: bounded `{ code, message }` diagnostics that do not change status;
- `fallbackReason`: a machine-readable reason for visible lower-level fallback, or
  `null` when no fallback is indicated.

Human diagnostics never replace the envelope. On error, stderr contains only the bounded
label `vcs-agent: <error-code>` and a newline; success leaves stderr empty.

## Output budgets

`--output-budget <bytes>` sets the maximum UTF-8 byte count retained on stdout and the
capture budget passed to repository and forge clients. The default is 65,536 bytes and the
minimum accepted value is 512 bytes. Backend overflow is classified as `output-limit`
before partial content can become typed operation data. The reusable `inspect`, `changes`, and `commit`
APIs measure the complete envelope at their return boundary; if it would exceed the same
budget, they return a complete
`publish`, `ci status`, and `ci wait` apply the same reusable-API and renderer boundary. An
oversized complete outcome becomes an `output-limit` error envelope. Its error object sets `truncated: true` and reports
both `limitBytes` and the complete result's `requiredBytes`; partial operation data is never
presented as a valid typed result or valid JSON. Before commit mutation, the prospective budget
includes both verified-success and compact ambiguous-failure evidence, so a late failure can
retain bounded evidence rather than discovering an undersized envelope after mutation. Rendering
preserves that same typed outcome.

The library and server golden/hermetic tests parse the bounded response again as JSON and
assert that its UTF-8 byte count is within the requested limit.

## Errors and exit codes

The error code is part of contract v1 and maps to one stable process exit code:

| Error code | Exit | Meaning |
|---|---:|---|
| `unsupported` | 20 | Declared outcome is unavailable for this implementation or capability. |
| `denied` | 21 | Policy refused an otherwise known operation. |
| `invalid-input` | 22 | Command or operation input is malformed or incomplete. |
| `backend` | 23 | Git/Jujutsu facade failure. |
| `forge` | 24 | GitHub/GitLab/Gitea facade failure. |
| `authentication` | 25 | Required forge identity or credential is unavailable. |
| `timeout` | 26 | A configured deadline expired. |
| `cancellation` | 27 | The caller cancelled the operation. |
| `output-limit` | 28 | Complete output would exceed the stdout budget. |
| `external-command` | 29 | A typed underlying CLI execution failed outside the narrower categories. |
| `revision-mismatch` | 30 | Local, remote, or forge evidence belongs to another revision/ref. |

Terminal success exits 0. A successful non-terminal CI observation (`pending` or `no-runs`) exits
10 with the normal JSON envelope and empty stderr. Tests enumerate all error mappings and require
each exit code to be distinct.

## Redaction and process boundary

Every string in inspect, changes, commit, publication, and CI data, envelope errors, and warnings passes through the
contract redactor again at final serialization. It removes URL userinfo, bearer values, and
named token/password/secret/API-key/authorization values from remote URLs, paths, revision
text, forge metadata, and diff content even when a caller constructs a public envelope
directly. The tool parser does not echo unknown argv or machine-local paths in its errors.
Regression tests cover credentialed URLs and authorization data at the API and wire
boundaries.

Repository and forge outcomes compose the typed `VcsToolkit.Core`, `VcsToolkit.Forge`, and
`VcsToolkit.CliSupport` seams, which in turn execute through ProcessKit.
`VcsToolkit.Agent` deliberately exposes no raw-command escape hatch and production code
contains no direct `System.Diagnostics.Process` launch path. See the
[architecture guide](architecture.md) for the package boundary.

For longer-running outcomes, callers may compose the executable through the published
ProcessKit-CLI binary contract:

```text
processkit-cli run [supervision options] -- vcs-agent <operation> [arguments]
```

`probe` reports `processkit-cli-run` / `jsonl-v1` as compatible but optional. The two tools
remain independently packaged executables; `vcs-agent` does not load ProcessKit-CLI plugins
or implementation assemblies.

### Direct and supervised execution

Run short read-only operations directly when the caller already owns their lifetime:

```sh
vcs-agent inspect --repo .
```

Use ProcessKit-CLI when the caller needs a durable lifecycle stream, explicit deadlines,
out-of-band cancellation, bounded capture, or an additional containment boundary:

```sh
processkit-cli run \
  --jsonl ./run/events.jsonl \
  --capture-dir ./run/capture \
  --capture-max-bytes 64k \
  --no-echo \
  -- vcs-agent inspect --repo .
```

The agent result is the one LF-terminated JSON document in `capture/stdout.log`. The
supervisor lifecycle is the separate `events.jsonl` stream. Validate that stream with the
same published binary before consuming it:

```sh
processkit-cli events --file ./run/events.jsonl --validate
```

The supervised process exit preserves a child exit unchanged. Consequently, `0`, non-terminal
`10`, and the `vcs-agent` error range `20`–`30` retain their direct meanings. ProcessKit-CLI owns the
separate reserved range `100`–`119`; for the boundaries used here, `106` is an overall or
idle timeout (distinguished by `timeout.reason`) and `108` is control-plane cancellation.
The terminal `runner_exit` record repeats the final `code`, its `source`, and the optional
`child_code`. A valid completed proof requires no cleanup read or kill error. It normally also
requires `cleanup_finished.remaining = 0`. On the POSIX process-group fallback, ProcessKit-CLI
`v0.3.3` can snapshot an already-killed but not-yet-reaped root as `remaining = 1` immediately
before `runner_exit`. The executable proof accepts that narrow terminal observation only when
every reported PID matches a pre-recorded PID/start-time identity, all exact identities are
gone, and a bounded machine-readable `inspect` confirms that the run is no longer registered.

For out-of-band cancellation, start a named detached run, cancel it, and wait for cleanup:

```sh
processkit-cli run --detach --run-id agent-42 --jsonl ./run/events.jsonl -- vcs-agent inspect --repo .
processkit-cli cancel --run-id agent-42
processkit-cli wait --run-id agent-42 --timeout 15s --report-outcome
```

`wait --report-outcome` may honestly return `status: "unknown"` when a fast run removed its
registry record before the waiter observed it live. It must then leave the outcome fields
null. The caller that created the run still validates the terminal classification from its
owned JSONL file.

### Compatibility preflight and executable proof

The repository pins published ProcessKit-CLI `v0.3.3` release assets and their SHA-256
digests in `scripts/install-processkit-cli.ps1`. Before any payload is launched,
`scripts/test-vcs-agent-processkit.ps1` runs `processkit-cli probe --json` and requires:

- JSONL schema version `1` and the exact reserved exit band `100-119`;
- `run` with JSONL, run id, overall/idle deadlines, grace, bounded capture, overflow policy,
  no-echo, detach, and resource-summary surfaces;
- run-id cancellation, hard kill, live inspection (including machine-readable failures), and
  waiting with terminal outcome reporting;
- file-based lifecycle validation through `events --validate`.

The preflight is fail-closed: an absent token, schema mismatch, exit-band mismatch, wrong
binary identity, malformed report, or nonzero probe exit prevents every scenario from
starting. The proof exercises that rejection path with an intentionally absent surface and
requires the published `PROBE_INCOMPATIBLE` exit `110`. It then installs the packed
`vcs-agent` tool and checks nine real
cross-binary scenarios: success, `invalid-input` exit preservation, overall timeout, idle
timeout after observed output, bounded truncating capture, detached control cancellation,
fail-closed detached cleanup with verified kill/wait recovery, ordinary nested composition,
and outer-cancellation teardown while an inspected inner runner and its long-lived descendant
are alive. The teardown proof records PID/start-time identities before cancellation, rejects
cleanup read/kill errors and unknown reported survivors, and confirms that every exact identity
is gone afterward. On the POSIX process-group fallback, `inspect.members` is used only for its
published tracked-group-leader scope; descendant liveness and cleanup are checked from the
fixture's exact PID/start-time identity instead of waiting for an entry that the contract does
not enumerate. A nonzero POSIX terminal snapshot additionally requires the bounded
machine-readable registry confirmation described above. Every lifecycle stream is checked by
the published binary's embedded schema. A stream that contains `runner_exit` must end with
exactly one such record. ProcessKit-CLI `v0.3.3` does not guarantee that the inner stream reaches
a terminal record when the outer containment boundary ends it; on that branch the proof records
the absent inner terminal explicitly and relies on the independently confirmed outer teardown
plus exact identity-gone checks. The `vcs-agent-supervision` CI
matrix executes this proof on the published Windows, Linux, and Apple Silicon macOS targets and
uploads the install/proof JSON evidence.
An unsupported OS/architecture or missing published asset is a hard, structured installer
failure rather than a skipped proof.

ProcessKit-CLI `v0.3.3` publishes no nested-owner teardown capability for the POSIX
process-group fallback. The current Linux and macOS failure, minimal reproducer, required
generic semantics, and acceptable additive alternatives are recorded in the
[upstream-request draft](processkit-cli-nested-posix-containment-request.md). The executable
proof continues to run and fails closed on a surviving exact identity.

## Compatibility policy

Contract version `1` freezes operation names, field names and meanings, error/fallback
taxonomies, and exit mappings. Additive fields or new operation data may be introduced
without changing the version when existing v1 consumers can ignore them safely. Removing or
renaming a field or case, changing its meaning or type, reusing an exit code, or changing
stdout/stderr framing requires a new contract version. A tool that cannot honor a v1 outcome
must return a v1 structured error rather than a partial imitation.

Packaging is checked by `scripts/validate-packages.ps1`: the `VcsToolkit.Agent` library must
declare its sibling dependencies, and the `vcs-agent` artifact must be a self-contained
DotnetTool with command name `vcs-agent`, entry point `vcs-agent.dll`, bundled sibling
assemblies, README, icon, and XML documentation. The same validation continues to cover every
existing library and `vcs-mcp` artifact.
