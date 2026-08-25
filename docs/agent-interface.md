# vcs-agent v1 contract

`vcs-agent` is the outcome-oriented command-line interface over the reusable
`VcsToolkit.Agent` library. The library owns the transport-neutral contract and
application outcomes; `VcsToolkit.Agent.Server` is only the argv/stdout/stderr/exit-code
adapter packaged as a .NET global tool.

The first implemented operation is `probe`. The other v1 names are reserved now so an
agent can distinguish a planned capability from an unknown command without relying on
human-readable diagnostics. The broader delivery sequence is documented in the
[agent interface roadmap](agent-interface-roadmap.md).

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
| `inspect` | `inspect` | planned; returns `unsupported` | no |
| `changes` | `changes` | planned; returns `unsupported` | no |
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
			{ "name": "inspect", "availability": "planned", "mutating": false }
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

`--output-budget <bytes>` sets the maximum UTF-8 byte count retained on stdout. The
default is 65,536 bytes and the minimum accepted value is 512 bytes. If a complete result
would exceed the budget, `vcs-agent` discards that result and returns a complete
`output-limit` error envelope instead. Its error object sets `truncated: true` and reports
both `limitBytes` and the complete result's `requiredBytes`; partial operation data is never
presented as valid JSON.

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

Envelope error and warning text passes through the contract redactor before serialization.
It removes URL userinfo, bearer values, and named token/password/secret/API-key/
authorization values. The tool parser does not echo unknown argv or machine-local paths in
its errors. Redaction has a regression test with credentialed URLs and authorization data.

Future repository and forge outcomes must compose the typed `VcsToolkit.Core`,
`VcsToolkit.Forge`, and `VcsToolkit.CliSupport` seams, which in turn execute through
ProcessKit. `VcsToolkit.Agent` deliberately exposes no raw-command escape hatch and
production code contains no direct `System.Diagnostics.Process` launch path. See the
[architecture guide](architecture.md) for the package boundary.

For longer-running outcomes, callers may compose the executable through the published
ProcessKit-CLI binary contract:

```text
processkit-cli run [supervision options] -- vcs-agent <operation> [arguments]
```

`probe` reports `processkit-cli-run` / `jsonl-v1` as compatible but optional. The two tools
remain independently packaged executables; `vcs-agent` does not load ProcessKit-CLI plugins
or implementation assemblies.

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
