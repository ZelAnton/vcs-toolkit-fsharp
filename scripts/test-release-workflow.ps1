#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Verifies that release artifacts are validated before irreversible publication.

.DESCRIPTION
    Statically checks the release workflow ordering from pack through package validation,
    checksum generation, and the NuGet publication pivot. The validation command covers the
    complete artifacts directory, preserving checks for every library and global tool.
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workflowPath = Join-Path $RepoRoot '.github/workflows/release.yml'
$workflow = Get-Content -Raw -LiteralPath $workflowPath

$markers = [ordered]@{
    Pack = 'run: dotnet pack VcsToolkit.slnx --no-build --configuration Release --output ./artifacts'
    Validation = 'run: ./scripts/validate-packages.ps1 -PackagesDir ./artifacts'
    Checksums = '- name: Generate SHA256SUMS'
    Publication = '- name: Push to NuGet.org (irreversible pivot)'
}

$positions = [ordered]@{}

foreach ($entry in $markers.GetEnumerator()) {
    $first = $workflow.IndexOf($entry.Value, [StringComparison]::Ordinal)
    $last = $workflow.LastIndexOf($entry.Value, [StringComparison]::Ordinal)

    if ($first -lt 0) {
        throw "Release workflow is missing the $($entry.Key) marker: $($entry.Value)"
    }

    if ($first -ne $last) {
        throw "Release workflow contains more than one $($entry.Key) marker: $($entry.Value)"
    }

    $positions[$entry.Key] = $first
}

if (-not (
        $positions.Pack -lt $positions.Validation -and
        $positions.Validation -lt $positions.Checksums -and
        $positions.Checksums -lt $positions.Publication
    )) {
    throw 'Release workflow must pack, validate all artifacts, generate checksums, then publish to NuGet.org.'
}

Write-Host 'OK: release package validation precedes checksums and NuGet publication.' -ForegroundColor Green
