#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Exercises positive, standalone-copy, and fail-closed Skill validation paths.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $VcsAgentPath,

    [string] $RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Invoke-Validator {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SkillPath,

        [Parameter(Mandatory = $true)]
        [string] $ContractPath
    )

    $output = & $script:PowerShellPath -NoProfile -File $script:ValidatorPath `
        -VcsAgentPath $script:AgentPath `
        -SkillPath $SkillPath `
        -ContractPath $ContractPath `
        -ProcessKitProofPath $script:ProcessKitProofPath 2>&1 | Out-String

    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

function Assert-Success {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Result,

        [Parameter(Mandatory = $true)]
        [string] $Context
    )

    if ($Result.ExitCode -ne 0) {
        throw "$Context failed with exit $($Result.ExitCode):`n$($Result.Output)"
    }
}

$script:PowerShellPath = (Get-Process -Id $PID).Path
$script:ValidatorPath = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'scripts/check-vcs-agent-skill.ps1'))
$script:ProcessKitProofPath = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'scripts/test-vcs-agent-processkit.ps1'))
$script:AgentPath = [System.IO.Path]::GetFullPath($VcsAgentPath)
$sourceSkillDirectory = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'skills/using-vcs-agent'))
$sourceSkill = Join-Path $sourceSkillDirectory 'SKILL.md'
$sourceContract = Join-Path $sourceSkillDirectory 'references/contract.v1.json'
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('using-vcs-agent-skill-test-' + [Guid]::NewGuid().ToString('N'))

[System.IO.Directory]::CreateDirectory($scratch) | Out-Null

try {
    $baseline = Invoke-Validator $sourceSkill $sourceContract
    Assert-Success $baseline 'Committed Skill validation'

    $installed = Join-Path $scratch 'using-vcs-agent'
    Copy-Item -LiteralPath $sourceSkillDirectory -Destination $installed -Recurse
    $installedSkill = Join-Path $installed 'SKILL.md'
    $installedContract = Join-Path $installed 'references/contract.v1.json'
    $standalone = Invoke-Validator $installedSkill $installedContract
    Assert-Success $standalone 'Standalone copied Skill validation'

    $driftedContract = Join-Path $scratch 'drifted-contract.v1.json'
    $drifted = Get-Content -Raw -LiteralPath $installedContract -Encoding UTF8 | ConvertFrom-Json -Depth 100
    $drifted.examples[1].expectedExit = 22
    $drifted | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $driftedContract -Encoding utf8NoBOM
    $rejected = Invoke-Validator $installedSkill $driftedContract

    if ($rejected.ExitCode -eq 0) {
        throw 'Validator accepted a reference whose executable exit fact drifted.'
    }

    if ($rejected.Output -notmatch 'expected 22') {
        throw "Validator rejected the drift fixture for an unexpected reason:`n$($rejected.Output)"
    }

    Write-Host 'OK: committed and copied Skill bundles validate; executable-contract drift fails closed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
