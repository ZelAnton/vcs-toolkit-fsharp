# Security Policy

## Supported versions

Before the first release, security fixes are applied to `main`; consumers of a
source build should update to the latest commit. After releases begin, fixes
will target the latest released version of **VcsToolkit**. Older versions will
not be maintained — upgrade to the latest release to receive fixes.

## Reporting a vulnerability

**Do not open a public issue for security vulnerabilities.**

Report privately through GitHub's
[private vulnerability reporting](https://github.com/ZelAnton/vcs-toolkit-fsharp/security/advisories/new)
(repository **Security → Advisories → Report a vulnerability**). If that is
unavailable, contact the maintainer listed on the
[ZelAnton](https://github.com/ZelAnton) profile.

Please include:

- a description of the vulnerability and its impact;
- steps to reproduce (a minimal proof of concept is ideal);
- the affected version, tag, or source commit.

You can expect an initial acknowledgement within a few days. Once a fix is
ready, it is applied to `main`; after public releases begin, a patched version
is also published to NuGet.org before the advisory is disclosed.

## Automated scanning

Dependencies are audited against the NuGet advisory database on every restore
(`NuGetAudit`/`NuGetAuditMode=all`, configured in
[`Directory.Build.props`](Directory.Build.props)), and
[Dependabot](.github/dependabot.yml) keeps GitHub Actions and NuGet packages
current.

> **No CodeQL.** GitHub CodeQL has no F# support, so this repository ships no
> CodeQL workflow. Static hygiene relies instead on `TreatWarningsAsErrors` and
> Fantomas formatting checks in CI. If you want deeper static analysis, wire up
> F# analyzers (e.g. the [Ionide analyzers](https://github.com/ionide/ionide-analyzers))
> through `Directory.Build.props`.
