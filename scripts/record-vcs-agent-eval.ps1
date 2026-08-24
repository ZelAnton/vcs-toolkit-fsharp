#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Normalizes offline vcs-agent observations into deterministic evaluation results.

.DESCRIPTION
    Validates the versioned schema, corpus, and observation documents without invoking a
    model, VCS executable, forge, or network. Runs are emitted in corpus order and JSON is
    written as UTF-8 without BOM with LF line endings, so identical inputs are byte-stable.
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $SchemaPath,
    [string] $CorpusPath,
    [string] $ObservationsPath,
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'vcs-agent-eval-common.ps1')

if ([string]::IsNullOrWhiteSpace($SchemaPath)) {
    $SchemaPath = Join-Path $RepoRoot 'evals/vcs-agent/schema/eval.v1.schema.json'
}
if ([string]::IsNullOrWhiteSpace($CorpusPath)) {
    $CorpusPath = Join-Path $RepoRoot 'evals/vcs-agent/corpus.v1.json'
}
if ([string]::IsNullOrWhiteSpace($ObservationsPath)) {
    $ObservationsPath = Join-Path $RepoRoot 'evals/vcs-agent/offline/observations.v1.json'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $RepoRoot 'evals/vcs-agent/offline/results.v1.json'
}

try {
    $schema = Read-VcsAgentEvalJson $SchemaPath 'schema'
    $corpus = Read-VcsAgentEvalJson $CorpusPath 'corpus'
    $observations = Read-VcsAgentEvalJson $ObservationsPath 'observations'
    Assert-VcsAgentEvalSchemaDocument $schema
    Assert-VcsAgentEvalCorpusDocument $corpus
    Assert-VcsAgentEvalObservationsDocument $observations
    Assert-VcsAgentEvalSchemaMatch $CorpusPath $SchemaPath 'corpus'
    Assert-VcsAgentEvalSchemaMatch $ObservationsPath $SchemaPath 'observations'
    $results = New-VcsAgentEvalResultDocument $corpus $observations
    Write-VcsAgentEvalJson $results $OutputPath
    Assert-VcsAgentEvalSchemaMatch $OutputPath $SchemaPath 'results'
    $mismatchCount = @($results.runs | Where-Object { -not $_.expectationMatched }).Count
    $diagnostic = "schema=v1 corpus=$($corpus.corpusVersion) scenarios=$($results.runs.Count) mismatches=$mismatchCount"
    if ($mismatchCount -gt 0) {
        [Console]::Error.WriteLine("ERROR recorder $diagnostic")
        exit 1
    }
    Write-Output "OK recorder $diagnostic"
    exit 0
}
catch {
    [Console]::Error.WriteLine("ERROR recorder $($_.Exception.Message)")
    exit 1
}
