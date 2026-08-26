# ProcessKit-CLI request: nested POSIX teardown ownership

## Observed contract gap

VcsToolkit's cross-binary proof starts one detached ProcessKit-CLI run whose payload is a
second ProcessKit-CLI run. The inner run owns a long-lived fixture and descendant. Cancelling
the outer run must leave all three exact PID/start-time identities gone before the proof can
pass.

Published ProcessKit-CLI `v0.3.3` documents two relevant limits in its
[control-plane membership semantics](https://github.com/ZelAnton/ProcessKit-CLI/blob/v0.3.3/docs/control-plane.md#what-member-means-per-mechanism)
and [platform matrix](https://github.com/ZelAnton/ProcessKit-CLI/blob/v0.3.3/docs/platform-support.md#mechanism-and-abrupt-cleanup):

- `process_group` inspection enumerates tracked group leaders, not every contained descendant;
- abrupt runner-death cleanup is `direct_child_only` on Linux's process-group fallback and
  `none` on macOS.

The proof therefore cannot infer descendant absence from `inspect.members`, and it cannot
recover an inner-owned process group after the outer boundary has already terminated the
inner runner. This is a generic nested-supervisor ownership gap, not a VCS-specific behavior.

## Minimal reproducer

1. Start a detached outer run with a two-second grace period.
2. Use an inner ProcessKit-CLI run as its payload, with a one-second grace period.
3. Have the inner payload start a child that starts a 60-second descendant without calling
   `setsid` or otherwise escaping its process group.
4. Record exact PID/start-time identities for the inner runner, child, and descendant.
5. Confirm both roots through `inspect`; on `process_group`, treat leader-only member
   enumeration as the published behavior.
6. Cancel only the outer run, wait for its terminal lifecycle, and poll all recorded exact
   identities for disappearance.

The executable reproducer is the `nested-containment-teardown` scenario in
`scripts/test-vcs-agent-processkit.ps1`; the fixture is
`scripts/vcs-agent-processkit-fixture.ps1`.

## Verified matrix

| Platform | Published mechanism | Result |
| --- | --- | --- |
| Windows | `job_object` | The supervised proof passes and exact identities disappear. |
| GitHub-hosted Ubuntu | `process_group` fallback | [CI job `97892097013`](https://github.com/ZelAnton/vcs-toolkit-fsharp/actions/runs/32875432090/job/97892097013) retained the exact descendant identity after emergency teardown. |
| GitHub-hosted Apple Silicon macOS | `process_group` | [CI job `97892096958`](https://github.com/ZelAnton/vcs-toolkit-fsharp/actions/runs/32875432090/job/97892096958) retained the exact fixture-root identity after emergency teardown. |

Both POSIX failures came from exact PID/start-time checks, not from a stale PID-only lookup.
The source run is CI run `32875432090` for VcsToolkit commit
`ad2873f97209a81226c422a2a3a024ec8ef46175`.

## Requested semantics

Provide a published, machine-detectable guarantee for nested runs that ensures outer normal
cancellation cannot orphan the inner run's owned containment boundary. A consumer must be able
to require that guarantee during preflight and must fail closed when the obtained mechanism
cannot provide it.

An additive capability token plus corresponding lifecycle/teardown behavior is sufficient;
an extension or plugin API is not required. Other acceptable outcomes are a cooperative
shutdown handoff between nested runners, or explicit rejection of unsupported nested
process-group composition before payload launch. The contract must remain generic to process
supervision and must not require VcsToolkit-specific knowledge.

Until that guarantee is published, the VcsToolkit proof continues to execute the scenario and
fail on any surviving exact identity. It does not skip or reinterpret the cleanup failure as a
pass.
