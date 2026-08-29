#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Checks saved vcs-agent evaluation results against the versioned golden corpus.

.DESCRIPTION
    Fails closed on schema/format drift, missing or reordered scenarios, changed metrics,
    and any route, command, call-count, outcome, preservation, publication, terminal-CI,
    or denial expectation mismatch. The check is offline and does not inspect live results.
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $SchemaPath,
    [string] $CorpusPath,
    [string] $SkillPath,
    [string] $SkillContractPath,
    [string] $ResultsPath
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
if ([string]::IsNullOrWhiteSpace($ResultsPath)) {
    $ResultsPath = Join-Path $RepoRoot 'evals/vcs-agent/offline/results.v1.json'
}
if ([string]::IsNullOrWhiteSpace($SkillPath)) {
    $SkillPath = Join-Path $RepoRoot 'skills/using-vcs-agent/SKILL.md'
}
if ([string]::IsNullOrWhiteSpace($SkillContractPath)) {
    $SkillContractPath = Join-Path $RepoRoot 'skills/using-vcs-agent/references/contract.v1.json'
}

try {
    $schema = Read-VcsAgentEvalJson $SchemaPath 'schema'
    $corpus = Read-VcsAgentEvalJson $CorpusPath 'corpus'
    $results = Read-VcsAgentEvalJson $ResultsPath 'results'
    Assert-VcsAgentEvalSchemaDocument $schema
    Assert-VcsAgentEvalCorpusDocument $corpus
    Assert-VcsAgentEvalResultsDocument $results
    Assert-VcsAgentEvalSchemaMatch $CorpusPath $SchemaPath 'corpus'
    Assert-VcsAgentEvalSchemaMatch $ResultsPath $SchemaPath 'results'
    Assert-VcsAgentEvalCurrentProvenance $results.provenance $SkillPath $SkillContractPath $CorpusPath 'results'
    Test-VcsAgentEvalResultDocument $corpus $results
    Write-Output "OK checker schema=v1 corpus=$($corpus.corpusVersion) scenarios=$($results.runs.Count) mismatches=0"
    exit 0
}
catch {
    [Console]::Error.WriteLine("ERROR checker $($_.Exception.Message)")
    exit 1
}
