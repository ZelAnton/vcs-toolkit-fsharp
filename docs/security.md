---
title: Security model
category: Documentation
index: 4
---

# Security model

VcsToolkit's strongest guarantees are on its **typed methods**: they shape a fixed command,
guard caller-controlled positional arguments, keep supported credentials out of argv, and turn
untrusted CLI output into typed results without parser exceptions. Those guarantees stop at the
raw `Run`/`RunRaw` escape hatches and do not make the driven CLI, repository, remote, or operating
system account trustworthy. In particular, never pass agent-, repository-, or network-controlled
argv to an escape hatch.

This page is the deployment-oriented security view of the
[cross-cutting design principles](architecture.md) and the
[CLI command coverage index](command-index.md). It describes what the library enforces, what it
only makes easier, and what remains the caller's policy decision.

## Trust boundaries

| Boundary | Treat as untrusted | VcsToolkit guarantee | Caller responsibility |
|---|---|---|---|
| Repository | `.gitmodules`, repository config, hooks, `.gitattributes`, filters, worktree files, and VCS metadata | Typed operations constrain their own argv; a hardened Git profile overrides selected execution-bearing config and environment inputs | Limit filesystem/process privileges, decide whether checkout/submodule execution is acceptable, and isolate repositories whose contents are hostile |
| Remote | Remote URL, fetched objects, redirect targets, submodule URLs, and server responses | Network operations are non-interactive; recognized HTTPS Git credentials can be scoped to the requested host | Allow-list protocols and hosts, scope provider results, constrain network access, and validate content before using it outside the repository |
| Argv | Positional values and values that may look like flags, including empty strings and NUL bytes | Typed methods apply the appropriate positional/refspec/path guard before spawning | Do not assume flag-value slots are content-safe for a different downstream interpreter; never forward untrusted argv to `Run`/`RunRaw` |
| CLI output | stdout, stderr, JSON, templates, paths, remote names, and error text | Parsers are total/tolerant and failures are returned as typed errors with classifiers | Treat parsed values as untrusted data; authorize any subsequent file, network, or UI action separately |
| Forge credentials | Tokens returned by a provider or ambient CLI login | Supported GitHub/GitLab tokens are injected through environment variables, never argv | Protect the child environment and process account, return credentials only for the requested service/host, and avoid ambient credentials broader than the served repository needs |

The toolkit is a subprocess wrapper, not a security sandbox. A successful parse says that output
was structurally understood; it does not establish that a remote, commit, path, or message is
benign or authentic. Likewise, classifiers such as `IsTransient`, `IsLockContention`, and
`IsMergeConflict` turn process failures into useful control-flow hints; they are not authorization
decisions. Retry only the operation the classifier describes, with a bounded policy, and never use
matched stderr text as proof of remote identity or content safety.

## Typed surface and escape hatches

The shared `rejectFlagLike` guard rejects a caller-controlled positional argument when it is
empty/whitespace-only, begins with `-`, or contains NUL. `checkFlags` applies that check to all
relevant positional values in one typed operation. Some operations use stronger, context-specific
shaping instead: an end-of-options `--`, literal pathspec transport, typed numeric values, exact
Jujutsu bookmark patterns, or refspec validation. The architecture guide's
"Cross-cutting design principles" section explains the common rule; the command index records the
actual argv contract method by method. The
[`rejectFlagLike` doc comment](https://github.com/ZelAnton/vcs-toolkit-fsharp/blob/main/src/VcsToolkit.CliSupport/Classify.fs#L86-L109)
is the source of truth for the base guard.

These checks prevent a positional value from becoming an option to the driven CLI. They are not a
general shell, revset, fileset, URL, regex, template, or content sanitizer. A typed method remains
responsible for any grammar it deliberately accepts in a flag-value slot.

`Git.Run`/`Git.RunRaw`, `Jj.Run`/`Jj.RunRaw`, and the corresponding bound `*At` members are
deliberate untyped escape hatches. They accept arbitrary argv and do **not** call `checkFlags`.
`RunRaw` only changes exit handling—it returns captured output even for a non-zero exit—and adds no
validation. Raw Git options such as config or executable redirectors can change what Git runs.
Jujutsu is especially sensitive: its `--config` and alias mechanism can turn a raw invocation into
command execution, as the [`Jj.Run` doc comment](https://github.com/ZelAnton/vcs-toolkit-fsharp/blob/main/src/VcsToolkit.Jj/Jj.fs#L241-L258)
warns. Use a raw method only when all argv elements are application-owned constants or have been
validated against a closed allow-list. The [command index](command-index.md) lists both modeled
commands and examples that require an escape hatch.

## Secrets and credentials

`Secret` redacts itself as `***` from `ToString()` and formatting. Reading the value requires an
explicit `Expose()` at the use site. This is an accidental-log-leak defense, not secure memory:
the value is still a managed string and is not scrubbed from RAM. See the
[`Secret` API contract](https://github.com/ZelAnton/vcs-toolkit-fsharp/blob/main/src/VcsToolkit.CliSupport/Secret.fs#L3-L23).

`ICredentialProvider` resolves a `CredentialRequest` just in time. The request identifies the
service and, when known, the remote host. `Ok None` means "use ambient CLI authentication"; an
empty returned secret is treated the same way, while `Error` aborts the operation. Providers
should therefore make an explicit service-and-host decision rather than return one powerful token
for every request.

For GitHub and GitLab, `ManagedClient.WithTokenEnv` supplies the secret through `GH_TOKEN`,
`GH_ENTERPRISE_TOKEN`, or `GITLAB_TOKEN` as appropriate. Tokens never appear in argv or command
observer events. Git HTTPS credentials use an inline `credential.helper` whose argv contains only
environment-variable names. For a recognized HTTPS authority, the helper answers only a matching
`host=` request and only the `get` action, so a redirect or submodule on a different host does not
receive the superproject credential. A different host must trigger a separately scoped provider
decision or ambient authentication. If a URL has no safely recognized HTTPS host, do not inject a
shared credential unless an unscoped helper is acceptable; SSH authentication remains ambient.

Environment-only transport prevents routine argv/process-list disclosure, but the secret is still
visible to the child process and potentially to same-account or privileged process inspection.
Run long-lived services under a dedicated account, pass only the credentials they need, and keep
command logs free of environment dumps.

## Hardened Git client

`Git.Hardened()` is `Git.Create().Harden()`. The profile is intended for repositories the caller
did not create. It:

- removes inherited `GIT_*` redirectors that can change the repository, object store, config,
  executable lookup, SSH command, askpass program, diff/editor programs, templates, or pathspec
  interpretation;
- skips system config and defaults `GIT_TERMINAL_PROMPT=0`;
- injects higher-precedence config pins for `core.hooksPath=/dev/null`,
  `core.fsmonitor=false`, and an empty `core.sshCommand`.

These pins disable repository hooks and prevent repository config from re-enabling fsmonitor or
choosing an SSH command for this client. Other repository configuration is still read; `Harden()`
is a targeted execution-surface profile, not a blanket replacement for all Git configuration.

The full list lives in the
[`Harden` doc comment and implementation](https://github.com/ZelAnton/vcs-toolkit-fsharp/blob/main/src/VcsToolkit.Git/Git.fs#L1770-L1824).
The config-pin mechanism requires Git 2.31 or newer; older Git silently ignores those pins even
though environment scrubbing still applies. A deployment that depends on hardening must verify
`Git.Capabilities()` at startup and fail closed below that floor.

The `vcs-mcp` binary always opens a Git-backed repository with `Git.Hardened()` and applies its
configured per-command timeout. A direct library caller must choose `Git.Hardened()` explicitly;
`Repo.Open` and `Git.Create()` are not hardened defaults. `SubmoduleUpdate` likewise uses the
client it is called on—it pins `GIT_TERMINAL_PROMPT=0`, but it does not call `Harden()` itself.

Hardening narrows Git's execution surface; it does not prove repository content safe, restrict the
process filesystem, create a network allow-list, or set `protocol.*.allow`. It also should not be
treated as a substitute for isolating checkouts whose `.gitattributes` or nested content may
activate behavior outside the pins above.

## Submodule execution

`SubmoduleList` and `SubmoduleStatus` are reads. `SubmoduleUpdate` is different: it materializes
and executes nested repositories. The exact argv is recorded in the
[Git submodule section of the command index](command-index.md), and the architecture guide's
"Submodule reads vs. submodule execution" section gives the design rationale.

Important consequences are:

- `--init` clones URLs from the superproject's untrusted `.gitmodules`; recursive updates repeat
  the decision for nested `.gitmodules`;
- VcsToolkit does not set `protocol.*.allow`. For an untrusted superproject, the caller must start
  from `protocol.allow=never` and explicitly enable only required transports, for example HTTPS
  (and SSH only when the deployment genuinely needs it). Do not enable `file` or `ext` merely to
  make an unknown repository work;
- checkout can encounter clean/smudge filter drivers from `.gitattributes`, hooks, and fsmonitor
  configuration in every materialized repository. The hardened profile pins hooks and fsmonitor
  and scrubs known execution redirectors, but the operation must still run in a disposable,
  least-privileged sandbox when nested content is hostile;
- `GIT_TERMINAL_PROMPT=0` makes missing authentication fail instead of hanging a daemon;
- recognized HTTPS credential-helper host scoping still applies. A submodule on another host does
  not receive the credential scoped to the superproject host.

If the deployment cannot enforce the protocol policy and execution isolation, do not offer
`SubmoduleUpdate --init` for untrusted repositories. Listing metadata is not equivalent to
materializing it.

## Jujutsu raw commands and read-only queries

Typed Jujutsu methods apply their documented guards and, on a `Jj.ReadOnly()` view, add the global
`--ignore-working-copy` flag to read/query commands. That flag matters because an ordinary jj read
may snapshot filesystem changes and record a new operation. Mutating methods are unaffected by the
read-only view.

Raw `Jj.Run`/`RunRaw` has no additional safety layer: argv is unguarded, aliases and `--config`
remain an RCE surface, and read intent cannot be inferred. A caller using raw methods for a
low-level worktree/workspace query that the typed API does not cover must add
`--ignore-working-copy` explicitly, before the subcommand, and retain `--color never` when parsing
text. Prefer a typed method whenever the [jj coverage table](command-index.md) contains one. For a
missing command, keep the complete raw argv application-owned; accepting an arbitrary command or
option list from an MCP request, repository file, or remote response is unsafe.

Jj has no `Harden()` counterpart because it has no repository-local hooks. Its Git remote
authentication is ambient, however, so the service account's Git credential helpers and SSH agent
still define the credential boundary.

## `vcs-mcp` deployment

The [MCP server guide](mcp-server.md) is the full flag and tool reference. Its security controls
are intentionally small and explicit:

- `WriteGate.None` is the default: all mutating tools are rejected.
- `WriteGate.All` is selected by `--allow-write` and enables every mutation.
- `WriteGate.Set` is selected by one or more `--allow-tools` values and enables only the named
  mutating tools. Unknown names fail startup.
- A per-repository `SemaphoreSlim(1, 1)` serializes local working-copy mutations within one server
  process. It is not a cross-process, cross-machine, or human-user lock.
- Remote-only forge writes such as `forge_issue_create`, `forge_issue_comment`,
  `forge_pr_create`, and `forge_pr_comment` do not take the local lock. Operations that may affect
  the checkout, including PR checkout/merge/close, do.
- `--timeout <seconds>` applies a deadline to each spawned git/jj/forge command. Keep a finite,
  non-zero value for services; `--timeout 0` disables the deadline.
- Git-backed repositories use the hardened client described above. The server exposes the typed
  catalog, not raw `Run`/`RunRaw`.

These controls are not authentication, authorization between MCP users, an OS sandbox, a network
policy, or a distributed repository lock. The MCP host decides who can launch or call the stdio
server. Repository and forge output is also untrusted model input: the gate prevents disabled tool
execution, but it cannot make instructions embedded in commit messages, files, issues, or PRs
trustworthy.

Use one server process and one dedicated worktree per trust principal or automation job. Give that
process a least-privileged filesystem account, a constrained network path, narrowly scoped forge
credentials, and no unrelated SSH agent. Coordinate with other processes separately. Set an
`--output-budget` as a resource limit and enable command diagnostics only to a protected sink;
command observation excludes secret environment values, but repository paths and argv can still
be sensitive.

## Safe embedding checklist

### Trusted repository in a daemon

1. Use typed methods; inventory every raw call against the [command index](command-index.md) and
   prove its argv is application-owned.
2. Set a finite `DefaultTimeout` and propagate cancellation for every request.
3. Implement `ICredentialProvider` so it checks both `CredentialService` and `Host`; return
   `Ok None` when ambient auth is deliberately preferred.
4. Run the daemon under an account that cannot read unrelated repositories or credentials.
5. Treat returned paths, URLs, commit text, and error text as untrusted before using them in a
   subsequent command, file access, HTML page, or log.

### Untrusted repositories or superprojects

1. Require Git 2.31 or newer and construct the Git client with `Git.Hardened()`; verify the
   capability floor at startup.
2. Deny protocols by default and explicitly allow only the transports the deployment needs.
3. Disable `SubmoduleUpdate --init` unless the same execution has both the protocol allow-list and
   a disposable least-privileged sandbox with restricted filesystem and network access.
4. Do not call `Run`/`RunRaw` with values originating in repository config/content, a remote, or an
   agent request.
5. Keep host-specific credentials separate and make unknown/unrecognized hosts resolve to no
   credential, not to a shared fallback token.
6. Use a fresh worktree or clone per job; do not let an untrusted checkout share mutable state with
   a trusted automation workspace.

### `vcs-mcp`

1. Start without write flags and confirm required reads work under `WriteGate.None`.
2. If writes are necessary, prefer the smallest `--allow-tools` set; use `--allow-write` only when
   the MCP client is authorized for every catalogued mutation.
3. Set a finite `--timeout` and a finite `--output-budget`; test that an intentionally slow command
   times out and an oversized read is truncated as expected.
4. Run one server against one dedicated worktree and prevent a second server or human process from
   mutating it concurrently; the in-process semaphore cannot coordinate them.
5. Provide only host-scoped, least-privileged forge credentials to the server account. Remember
   that remote-only forge writes can run concurrently because they do not take the local lock.
6. For an untrusted Git repository, verify the installed Git floor and add the caller-owned
   protocol/network policy even though `vcs-mcp` already selects the hardened Git client.
7. Sandbox the server process and treat all tool output as hostile content before an agent acts on
   it. `WriteGate` limits tools; it does not defend the model from prompt injection in repository
   or forge data.
