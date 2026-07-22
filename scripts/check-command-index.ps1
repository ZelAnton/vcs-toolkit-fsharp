#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Fails if a public wrapper method has no row in docs/command-index.md.

.DESCRIPTION
    docs/command-index.md is a hand-maintained reverse index: one table row per typed method
    on each wrapper client (`Git`, `Jj`, `GitHub`, `GitLab`, `Gitea`), naming the exact
    subcommand/flags it runs. It drifts whenever a wrapper gains a public method and nobody
    remembers to add its row.

    This script re-derives, from each wrapper's approved public-API surface
    (tests/VcsToolkit.PublicApi.Tests/ApprovedApi/*.approved.txt — the same file the
    ApiApprover tests already keep in sync with the real API, so this script never needs its
    own reflection step), the set of public methods that should have a row: every `public`
    member of the wrapper's own client type (`Git`, not `GitAt` — the `.At(dir)` bound view
    mirrors the client 1:1 and is deliberately not re-documented as separate rows; see the
    index's "How to read this" section) that isn't a configuration/construction member (see
    $excludedMembers below — `DefaultTimeout`, `WithRetry`, `Create`, `At`, …).

    For each expected method name, it checks that docs/command-index.md contains at least one
    literal `` `MethodName` `` occurrence (Markdown inline-code, exact case) — i.e. some row
    somewhere in the index still names that method. It does not attempt to verify the row is
    under the "correct" wrapper section or that the "Runs" column is accurate — a human still
    owns the row's content; this only catches a public method with NO row at all.

    Exits 1 (listing every issue) on any wrapper method missing a doc reference, on a stale
    exclusion (a name in $excludedMembers that no longer exists on any wrapper's approved
    surface — most likely a typo or a rename this script should instead be catching), or if a
    wrapper's approved-API file / class block / docs/command-index.md itself is missing.
    Exits 0 when every public wrapper method is referenced somewhere in the index.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of this script's directory.

.PARAMETER IndexPath
    Path to docs/command-index.md. Defaults to `<RepoRoot>/docs/command-index.md`.

.EXAMPLE
    pwsh ./scripts/check-command-index.ps1
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $IndexPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($IndexPath)) {
    $IndexPath = Join-Path $RepoRoot 'docs' 'command-index.md'
}

$approvedDir = Join-Path $RepoRoot 'tests' 'VcsToolkit.PublicApi.Tests' 'ApprovedApi'

# Wrapper name (for messages) -> approved-API file name + the client type whose block to read.
$wrappers = [ordered]@{
    'VcsToolkit.Git'    = @{ File = 'VcsToolkit.Git.approved.txt'; Type = 'Git' }
    'VcsToolkit.Jj'     = @{ File = 'VcsToolkit.Jj.approved.txt'; Type = 'Jj' }
    'VcsToolkit.GitHub' = @{ File = 'VcsToolkit.GitHub.approved.txt'; Type = 'GitHub' }
    'VcsToolkit.GitLab' = @{ File = 'VcsToolkit.GitLab.approved.txt'; Type = 'GitLab' }
    'VcsToolkit.Gitea'  = @{ File = 'VcsToolkit.Gitea.approved.txt'; Type = 'Gitea' }
}

# Members that configure or construct the client rather than shape a CLI argv — outside the
# "one row = one method that runs a subcommand" contract docs/command-index.md documents (see
# its "How to read this" section), so deliberately not expected to have a row. Not every name
# here exists on every wrapper (e.g. `WithHost` is GitHub-only, `Harden`/`Hardened` are
# git-only, `ReadOnly` is jj-only) — this is a shared superset. Keep in sync when a wrapper's
# configuration surface changes; $unusedExclusions below catches a stale entry.
$excludedMembers = [System.Collections.Generic.HashSet[string]]::new(
    [string[]](
        'At', 'Create', 'WithRunner',
        'DefaultTimeout', 'DefaultEnv', 'DefaultEnvRemove', 'DefaultCancelOn',
        'WithRetry', 'WithObserver', 'WithCredentials', 'WithToken', 'WithEnvToken', 'WithHost',
        'Harden', 'Hardened', 'ReadOnly'
    )
)

if (-not (Test-Path -LiteralPath $IndexPath)) {
    Write-Host "Command index not found: $IndexPath" -ForegroundColor Red
    exit 1
}
$indexText = Get-Content -Raw -LiteralPath $IndexPath

$failures = [System.Collections.Generic.List[string]]::new()
$excludedSeen = [System.Collections.Generic.HashSet[string]]::new()

foreach ($wrapperName in $wrappers.Keys) {
    $info = $wrappers[$wrapperName]
    $approvedPath = Join-Path $approvedDir $info.File

    if (-not (Test-Path -LiteralPath $approvedPath)) {
        $failures.Add("${wrapperName}: approved API file not found: $approvedPath")
        continue
    }

    $approvedText = Get-Content -Raw -LiteralPath $approvedPath

    # Extract the client type's own block, stopping at its class-closing `    }` — NOT the
    # "<Type>At" bound-view sibling that follows it (`\b` after the type name keeps `class Git`
    # from also matching `class GitAt`).
    $classPattern = "(?ms)public sealed class $($info.Type)\b.*?\r?\n    \{\r?\n(.*?)\r?\n    \}"
    $classMatch = [regex]::Match($approvedText, $classPattern)

    if (-not $classMatch.Success) {
        $failures.Add("${wrapperName}: could not find a 'public sealed class $($info.Type)' block in $($info.File)")
        continue
    }

    $body = $classMatch.Groups[1].Value
    $methodNames = [System.Collections.Generic.SortedSet[string]]::new()

    foreach ($line in ($body -split '\r?\n')) {
        if ($line -notmatch '^\s{8}public\s') { continue }

        # The method name immediately precedes its own argument-list `(`, optionally through a
        # generic-arity suffix (`Transaction<T>(`). The generic-suffix body excludes `.` as well
        # as parens: a bare type parameter name (`T`) never contains a dot, but a qualified
        # return/parameter TYPE (`Microsoft.FSharp.Core.FSharpResult<...>`) always does — without
        # excluding it, `[^()]*` greedily spans from an EARLIER `Task<` in the return type all the
        # way to the real method's own `(`, misreporting the method name as "Task".
        $nameMatch = [regex]::Match($line, '(?<name>[A-Za-z_]\w*)(?:<[^().]*>)?\(')
        if ($nameMatch.Success) {
            [void]$methodNames.Add($nameMatch.Groups['name'].Value)
        }
    }

    foreach ($name in $methodNames) {
        if ($excludedMembers.Contains($name)) {
            [void]$excludedSeen.Add($name)
            continue
        }

        $needle = '`' + $name + '`'
        if ($indexText.IndexOf($needle, [System.StringComparison]::Ordinal) -lt 0) {
            $failures.Add("${wrapperName}: public method '$name' has no ``$name`` reference in docs/command-index.md")
        }
    }
}

# An exclusion that matched nothing on any wrapper's approved surface is very likely a
# typo/stale rename this script should have caught as a real method instead of silently
# excluding nothing.
$unusedExclusions = $excludedMembers | Where-Object { -not $excludedSeen.Contains($_) }
foreach ($u in $unusedExclusions) {
    $failures.Add("check-command-index.ps1: excluded member '$u' matched no wrapper's approved API surface -- stale exclusion (typo/rename)?")
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "Command index drift check FAILED ($($failures.Count) issue(s)):" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}

Write-Host 'OK: every public wrapper method is referenced in docs/command-index.md.' -ForegroundColor Green
exit 0
