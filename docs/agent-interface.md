# vcs-agent v1 contract

`vcs-agent` is the outcome-oriented command-line interface over the reusable
`VcsToolkit.Agent` library. The library owns the transport-neutral contract and
application outcomes; `VcsToolkit.Agent.Server` is only the argv/stdout/stderr/exit-code
adapter packaged as a .NET global tool.

The implemented read-only operations are `probe`, `inspect`, and `changes`. The other
v1 names are reserved now so an agent can distinguish a planned capability from an
unknown command without relying on human-readable diagnostics. The broader delivery
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
| `commit` | `commit` | planned; returns `unsupported` | yes |
| `publish` | `publish` | planned; returns `unsupported` | yes |
| `ci status` | `ci.status` | planned; returns `unsupported` | no |
| `ci wait` | `ci.wait` | planned; returns `unsupported` | no |

There is no general raw-command operation. A declared but unavailable outcome returns
`unsupported` before any VCS command can run, with `fallbackReason` set to
`operation-not-implemented`. An unrecognized or incomplete command returns
`invalid-input` instead.

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

## Envelope and stream rules

Every invocation writes exactly one LF-terminated JSON document to stdout. Property order
and spelling are golden-tested. The top-level fields are:

- `contractVersion`: currently the string `"1"`;
- `operation`: the canonical operation name;
- `status`: `success` or `error`;
- `terminal`: whether this outcome is terminal rather than still running;
- `data`: operation-specific typed data, or `null` on error;
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
before partial content can become typed operation data. The reusable `inspect` and `changes`
APIs measure the complete envelope at their return boundary; if it would exceed the same
budget, they discard that result and return a complete
`output-limit` error envelope instead. Its error object sets `truncated: true` and reports
both `limitBytes` and the complete result's `requiredBytes`; partial operation data is never
presented as a valid typed result or valid JSON. Rendering preserves that same typed outcome.

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

Success exits 0. Tests enumerate all ten error mappings and require each exit code to be
distinct.

## Redaction and process boundary

Every string in inspect and changes data, envelope errors, and warnings passes through the
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

The supervised process exit preserves a child exit unchanged. Consequently, `0` and the
`vcs-agent` error range `20`–`29` retain their direct meanings. ProcessKit-CLI owns the
separate reserved range `100`–`119`; for the boundaries used here, `106` is an overall or
idle timeout (distinguished by `timeout.reason`) and `108` is control-plane cancellation.
The terminal `runner_exit` record repeats the final `code`, its `source`, and the optional
`child_code`. A valid completed proof also requires `cleanup_finished.remaining = 0` with
no read or kill error.

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
- run-id cancellation, hard kill, live inspection, and waiting with terminal outcome
  reporting;
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
are alive. The teardown proof records PID/start-time identities before cancellation, requires
the outer terminal lifecycle to report zero survivors without read/kill errors, and confirms
that those exact identities are gone afterward.
Every lifecycle stream is checked by the published binary's embedded schema and for a clean
terminal record. The `vcs-agent-supervision` CI matrix executes this proof on the published
Windows, Linux, and Apple Silicon macOS targets and uploads the install/proof JSON evidence.
An unsupported OS/architecture or missing published asset is a hard, structured installer
failure rather than a skipped proof.

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
