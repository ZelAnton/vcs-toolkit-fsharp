#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Regression tests for scripts/check-command-index.ps1.

.DESCRIPTION
    Runs the drift checker against temporary copies of the approved API snapshots and index.
    The fixtures deliberately exercise both directions of drift and the wrapper-scoping rule.
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$checkScript = Join-Path $RepoRoot 'scripts' 'check-command-index.ps1'
$sourceApprovedDir = Join-Path $RepoRoot 'tests' 'VcsToolkit.PublicApi.Tests' 'ApprovedApi'
$sourceIndexPath = Join-Path $RepoRoot 'docs' 'command-index.md'
$testRoot = Join-Path $RepoRoot ('scripts/.check-command-index-test-' + [guid]::NewGuid().ToString('N'))

function Copy-Fixture {
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'docs') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'tests/VcsToolkit.PublicApi.Tests/ApprovedApi') -Force | Out-Null
    Copy-Item -LiteralPath $sourceIndexPath -Destination (Join-Path $testRoot 'docs/command-index.md')
    Copy-Item -Path (Join-Path $sourceApprovedDir '*.approved.txt') -Destination (Join-Path $testRoot 'tests/VcsToolkit.PublicApi.Tests/ApprovedApi')
}

function Invoke-Check {
    $output = & pwsh -NoLogo -NoProfile -File $checkScript -RepoRoot $testRoot 2>&1 | Out-String
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

function Assert-Check {
    param(
        [Parameter(Mandatory)] [int] $ExpectedExitCode,
        [Parameter(Mandatory)] [string] $ExpectedMessage
    )

    $result = Invoke-Check
    if ($result.ExitCode -ne $ExpectedExitCode) {
        throw "Expected check exit code $ExpectedExitCode, got $($result.ExitCode). Output:`n$($result.Output)"
    }
    if ($result.Output -notmatch [regex]::Escape($ExpectedMessage)) {
        throw "Expected check output to contain '$ExpectedMessage'. Output:`n$($result.Output)"
    }
}

try {
    Copy-Fixture

    $baseline = Invoke-Check
    if ($baseline.ExitCode -ne 0) {
        throw "The unmodified fixture must pass. Output:`n$($baseline.Output)"
    }

    # Reverse direction: leave the documented row in place but remove both Git.Run
    # signatures from Git's approved class. The checker must report the stale Git row.
    $gitApprovedPath = Join-Path $testRoot 'tests/VcsToolkit.PublicApi.Tests/ApprovedApi/VcsToolkit.Git.approved.txt'
    $gitApprovedText = Get-Content -Raw -LiteralPath $gitApprovedPath
    $gitClassPattern = '(?ms)(public sealed class Git\b.*?\r?\n    \{\r?\n)(.*?)(\r?\n    \})'
    $gitClassMatch = [regex]::Match($gitApprovedText, $gitClassPattern)
    if (-not $gitClassMatch.Success) {
        throw 'Could not locate the Git approved class in the test fixture.'
    }
    $gitBody = [regex]::Replace($gitClassMatch.Groups[2].Value, '(?m)^        public .*?\bRun\([^\r\n]*\) \{ \}\r?\n', '')
    $gitApprovedText = $gitApprovedText.Substring(0, $gitClassMatch.Groups[2].Index) + $gitBody + $gitApprovedText.Substring($gitClassMatch.Groups[2].Index + $gitClassMatch.Groups[2].Length)
    Set-Content -LiteralPath $gitApprovedPath -Value $gitApprovedText -NoNewline
    Assert-Check -ExpectedExitCode 1 -ExpectedMessage "VcsToolkit.Git: docs/command-index.md has stale ``Run`` row"

    # Forward direction: leave Git.Run approved but remove its row from the Git section.
    Copy-Fixture
    $indexPath = Join-Path $testRoot 'docs' 'command-index.md'
    $indexText = Get-Content -Raw -LiteralPath $indexPath
    $gitSectionPattern = '(?ms)(^## git \([^\r\n]*\)\r?\n)(.*?)(?=^## |\z)'
    $gitSectionMatch = [regex]::Match($indexText, $gitSectionPattern)
    if (-not $gitSectionMatch.Success) {
        throw 'Could not locate the Git section in the test fixture.'
    }
    $gitSection = [regex]::Replace($gitSectionMatch.Groups[2].Value, '(?m)^\| `Run` \|.*(?:\r?\n|$)', '')
    $indexText = $indexText.Substring(0, $gitSectionMatch.Groups[2].Index) + $gitSection + $indexText.Substring($gitSectionMatch.Groups[2].Index + $gitSectionMatch.Groups[2].Length)
    Set-Content -LiteralPath $indexPath -Value $indexText -NoNewline
    Assert-Check -ExpectedExitCode 1 -ExpectedMessage "VcsToolkit.Git: public method 'Run' has no ``Run`` row in its 'git' section"

    # Wrapper scope: Git.Run remains documented, but GitHub.Run is removed. A global
    # A global lookup would incorrectly pass GitHub.Run because Git's row still exists.
    Copy-Fixture
    $indexText = Get-Content -Raw -LiteralPath $indexPath
    $githubSectionPattern = '(?ms)(^## gh \([^\r\n]*\)\r?\n)(.*?)(?=^## |\z)'
    $githubSectionMatch = [regex]::Match($indexText, $githubSectionPattern)
    if (-not $githubSectionMatch.Success) {
        throw 'Could not locate the GitHub section in the test fixture.'
    }
    $githubSection = [regex]::Replace($githubSectionMatch.Groups[2].Value, '(?m)^\| `Run` \|.*(?:\r?\n|$)', '')
    $indexText = $indexText.Substring(0, $githubSectionMatch.Groups[2].Index) + $githubSection + $indexText.Substring($githubSectionMatch.Groups[2].Index + $githubSectionMatch.Groups[2].Length)
    Set-Content -LiteralPath $indexPath -Value $indexText -NoNewline
    Assert-Check -ExpectedExitCode 1 -ExpectedMessage "VcsToolkit.GitHub: public method 'Run' has no ``Run`` row in its 'gh' section"

    Write-Host 'OK: command-index drift regression cases passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
