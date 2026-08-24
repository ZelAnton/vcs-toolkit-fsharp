#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Regression tests for the hermetic vcs-agent evaluation recorder and checker.

.DESCRIPTION
    Exercises the tracked golden documents, byte-for-byte recorder reproducibility,
    invalid recorder input, an expectation mismatch, and rejected format drift.
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'vcs-agent-eval-common.ps1')

$recorder = Join-Path $RepoRoot 'scripts/record-vcs-agent-eval.ps1'
$checker = Join-Path $RepoRoot 'scripts/check-vcs-agent-eval.ps1'
$fixtures = Join-Path $RepoRoot 'evals/vcs-agent/fixtures'
$offline = Join-Path $RepoRoot 'evals/vcs-agent/offline'
$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $tempBase ('vcs-agent-eval-' + [guid]::NewGuid().ToString('N')))
)
if (-not $testRoot.StartsWith($tempBase, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to create the test directory outside the system temporary directory.'
}
[void][System.IO.Directory]::CreateDirectory($testRoot)

function Invoke-EvalScript {
    param(
        [Parameter(Mandatory)] [string] $Script,
        [Parameter(Mandatory)] [object[]] $Arguments
    )

    $output = & pwsh -NoLogo -NoProfile -File $Script @Arguments 2>&1 | Out-String
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

function Assert-EvalSuccess {
    param(
        [Parameter(Mandatory)] [pscustomobject] $Result,
        [Parameter(Mandatory)] [string] $ExpectedText
    )

    if ($Result.ExitCode -ne 0) {
        throw "Expected exit code 0, got $($Result.ExitCode). Output: $($Result.Output)"
    }
    if ($Result.Output -notmatch [regex]::Escape($ExpectedText)) {
        throw "Expected output to contain '$ExpectedText'. Output: $($Result.Output)"
    }
}

function Assert-EvalFailure {
    param(
        [Parameter(Mandatory)] [pscustomobject] $Result,
        [Parameter(Mandatory)] [string] $ExpectedText
    )

    if ($Result.ExitCode -eq 0) {
        throw "Expected a non-zero exit code. Output: $($Result.Output)"
    }
    if ($Result.Output -notmatch [regex]::Escape($ExpectedText)) {
        throw "Expected failure output to contain '$ExpectedText'. Output: $($Result.Output)"
    }
}

try {
    $baseline = Invoke-EvalScript $checker @('-RepoRoot', $RepoRoot)
    Assert-EvalSuccess $baseline 'OK checker schema=v1'

    $firstResult = Join-Path $testRoot 'results-first.json'
    $secondResult = Join-Path $testRoot 'results-second.json'
    $firstRecord = Invoke-EvalScript $recorder @('-RepoRoot', $RepoRoot, '-OutputPath', $firstResult)
    $secondRecord = Invoke-EvalScript $recorder @('-RepoRoot', $RepoRoot, '-OutputPath', $secondResult)
    Assert-EvalSuccess $firstRecord 'OK recorder schema=v1'
    Assert-EvalSuccess $secondRecord 'OK recorder schema=v1'
    $firstBytes = [System.IO.File]::ReadAllBytes($firstResult)
    $secondBytes = [System.IO.File]::ReadAllBytes($secondResult)
    if ([System.Convert]::ToBase64String($firstBytes) -cne [System.Convert]::ToBase64String($secondBytes)) {
        throw 'Recorder output is not byte-for-byte reproducible.'
    }
    $generatedCheck = Invoke-EvalScript $checker @('-RepoRoot', $RepoRoot, '-ResultsPath', $firstResult)
    Assert-EvalSuccess $generatedCheck 'mismatches=0'

    $invalidRecord = Invoke-EvalScript $recorder @(
        '-RepoRoot', $RepoRoot,
        '-ObservationsPath', (Join-Path $fixtures 'invalid-observations.v1.json'),
        '-OutputPath', (Join-Path $testRoot 'invalid-results.json')
    )
    Assert-EvalFailure $invalidRecord 'observations.schemaVersion has unsupported value'

    $observations = Read-VcsAgentEvalJson (Join-Path $offline 'observations.v1.json') 'test observations'
    $mismatchPatch = Read-VcsAgentEvalJson (Join-Path $fixtures 'checker-mismatch.patch.v1.json') 'mismatch patch'
    $targetRun = @($observations.runs | Where-Object { $_.scenarioId -ceq $mismatchPatch.scenarioId })
    if ($targetRun.Count -ne 1) {
        throw 'Mismatch fixture did not select exactly one observation run.'
    }
    $targetRun[0].($mismatchPatch.property) = $mismatchPatch.value
    $mismatchObservations = Join-Path $testRoot 'mismatch-observations.json'
    $mismatchResults = Join-Path $testRoot 'mismatch-results.json'
    Write-VcsAgentEvalJson $observations $mismatchObservations
    $mismatchRecord = Invoke-EvalScript $recorder @(
        '-RepoRoot', $RepoRoot,
        '-ObservationsPath', $mismatchObservations,
        '-OutputPath', $mismatchResults
    )
    Assert-EvalFailure $mismatchRecord 'ERROR recorder schema=v1'
    if ($mismatchRecord.Output -notmatch [regex]::Escape('mismatches=1')) {
        throw "Expected recorder mismatch diagnostics to contain 'mismatches=1'. Output: $($mismatchRecord.Output)"
    }
    $mismatchCheck = Invoke-EvalScript $checker @(
        '-RepoRoot', $RepoRoot,
        '-ResultsPath', $mismatchResults
    )
    Assert-EvalFailure $mismatchCheck 'expectation mismatch in scenario'

    $driftPatch = Read-VcsAgentEvalJson (Join-Path $fixtures 'format-drift.patch.v1.json') 'format drift patch'
    $drifted = Read-VcsAgentEvalJson $firstResult 'generated result'
    $drifted | Add-Member -NotePropertyName $driftPatch.property -NotePropertyValue $driftPatch.value
    $driftedResult = Join-Path $testRoot 'format-drift-results.json'
    Write-VcsAgentEvalJson $drifted $driftedResult
    $driftCheck = Invoke-EvalScript $checker @(
        '-RepoRoot', $RepoRoot,
        '-ResultsPath', $driftedResult
    )
    Assert-EvalFailure $driftCheck 'format drift'

    Write-Output 'OK: vcs-agent evaluation recorder/checker regression fixtures passed.'
}
finally {
    $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
    if (-not $resolvedTestRoot.StartsWith($tempBase, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a test directory outside the system temporary directory.'
    }
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}

exit 0
