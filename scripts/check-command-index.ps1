#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Fails if the wrapper API and docs/command-index.md drift in either direction.

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

    For each wrapper, it extracts the method rows from that wrapper's dedicated section and
    compares them with the wrapper's approved public API. This catches both a public method
    with no row and a stale row whose method no longer exists. It does not attempt to verify
    that the "Runs" column is accurate — a human still owns the row's content.

    Exits 1 (listing every issue) on any asymmetric wrapper drift, on a stale exclusion (a name
    in $excludedMembers that no longer exists on any wrapper's approved surface — most likely a
    typo or a rename this script should instead be catching), or if a wrapper's approved-API file
    / class block / docs/command-index.md itself is missing. Exits 0 when every wrapper's public
    method set matches its section in the index.

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
    'VcsToolkit.Git'    = @{ File = 'VcsToolkit.Git.approved.txt'; Type = 'Git'; Section = 'git' }
    'VcsToolkit.Jj'     = @{ File = 'VcsToolkit.Jj.approved.txt'; Type = 'Jj'; Section = 'jj' }
    'VcsToolkit.GitHub' = @{ File = 'VcsToolkit.GitHub.approved.txt'; Type = 'GitHub'; Section = 'gh' }
    'VcsToolkit.GitLab' = @{ File = 'VcsToolkit.GitLab.approved.txt'; Type = 'GitLab'; Section = 'glab' }
    'VcsToolkit.Gitea'  = @{ File = 'VcsToolkit.Gitea.approved.txt'; Type = 'Gitea'; Section = 'tea' }
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
$methodRowPattern = '(?m)^\|\s*`(?<name>[A-Za-z_]\w*)`\s*\|'

foreach ($wrapperName in $wrappers.Keys) {
    $info = $wrappers[$wrapperName]
    $approvedPath = Join-Path $approvedDir $info.File

    $sectionPattern = "(?ms)^## $([regex]::Escape($info.Section)) \([^\r\n]*\)\r?\n(.*?)(?=^## |\z)"
    $sectionMatch = [regex]::Match($indexText, $sectionPattern)

    if (-not $sectionMatch.Success) {
        $failures.Add("${wrapperName}: dedicated '## $($info.Section)' section not found in docs/command-index.md")
        continue
    }

    $documentedMethodNames = [System.Collections.Generic.SortedSet[string]]::new()
    foreach ($rowMatch in [regex]::Matches($sectionMatch.Groups[1].Value, $methodRowPattern)) {
        [void]$documentedMethodNames.Add($rowMatch.Groups['name'].Value)
    }
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

        if (-not $documentedMethodNames.Contains($name)) {
            $failures.Add("${wrapperName}: public method '$name' has no ``$name`` row in its '$($info.Section)' section of docs/command-index.md")
        }
    }

    foreach ($name in $documentedMethodNames) {
        if (-not $methodNames.Contains($name)) {
            $failures.Add("${wrapperName}: docs/command-index.md has stale ``$name`` row in its '$($info.Section)' section; method is absent from the approved API surface")
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

Write-Host 'OK: every wrapper section matches its approved public API surface.' -ForegroundColor Green
exit 0
