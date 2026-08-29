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
        [string] $ContractPath,

        [string] $AgentPath = $script:AgentPath
    )

    $output = & $script:PowerShellPath -NoProfile -File $script:ValidatorPath `
        -VcsAgentPath $AgentPath `
        -SkillPath $SkillPath `
        -ContractPath $ContractPath `
        -ProcessKitProofPath $script:ProcessKitProofPath 2>&1 |
        ForEach-Object { $_.ToString() } |
        Out-String -Width 32767

    $exitCode = $LASTEXITCODE

    [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Normalize-DiagnosticOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Text
    )

    $plainText = [regex]::Replace($Text, "$([char] 27)\[[0-?]*[ -/]*[@-~]", '')
    $withoutGutters = [regex]::Replace($plainText, '(?m)^\s*(?:Line\s+\||(?:\d+\s+)?\|)\s?', '')
    return [regex]::Replace($withoutGutters, '\s+', ' ').Trim()
}

function Assert-Rejected {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ContractPath,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedText,

        [Parameter(Mandatory = $true)]
        [string] $Context
    )

    $result = Invoke-Validator $script:InstalledSkill $ContractPath

    if ($result.ExitCode -eq 0) {
        throw "$Context was accepted."
    }

    $normalizedOutput = Normalize-DiagnosticOutput $result.Output

    if (-not $normalizedOutput.Contains($ExpectedText, [System.StringComparison]::Ordinal)) {
        throw "$Context was rejected for an unexpected reason:`n$($result.Output)"
    }
}

function New-DriftFixture {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [scriptblock] $Mutate
    )

    $fixture = Get-Content -Raw -LiteralPath $script:InstalledContract -Encoding UTF8 | ConvertFrom-Json -Depth 100
    & $Mutate $fixture
    $path = Join-Path $script:Scratch "$Name.json"
    $fixture | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    return $path
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
    $gutteredDiagnostic = "using-vcs-agent Skill validation failed: Option surface for 'inspect'`n    | differs from built vcs-agent contractFacts."
    $normalizedDiagnostic = Normalize-DiagnosticOutput $gutteredDiagnostic

    if (-not $normalizedDiagnostic.Contains("Option surface for 'inspect' differs", [System.StringComparison]::Ordinal)) {
        throw 'PowerShell diagnostic gutter normalization regressed.'
    }

    $baseline = Invoke-Validator $sourceSkill $sourceContract
    Assert-Success $baseline 'Committed Skill validation'

    $installed = Join-Path $scratch 'using-vcs-agent'
    Copy-Item -LiteralPath $sourceSkillDirectory -Destination $installed -Recurse
    $installedSkill = Join-Path $installed 'SKILL.md'
    $installedContract = Join-Path $installed 'references/contract.v1.json'
    $script:InstalledSkill = $installedSkill
    $script:InstalledContract = $installedContract
    $script:Scratch = $scratch
    $standalone = Invoke-Validator $installedSkill $installedContract
    Assert-Success $standalone 'Standalone copied Skill validation'

    $documentedAgentPath = $script:AgentPath

    if ([System.OperatingSystem]::IsWindows() -and [System.IO.Path]::GetExtension($documentedAgentPath) -ieq '.exe') {
        $documentedAgentPath = $documentedAgentPath.Substring(0, $documentedAgentPath.Length - 4)
    }

    $documented = Invoke-Validator $installedSkill $installedContract -AgentPath $documentedAgentPath
    Assert-Success $documented 'Documented platform-neutral apphost path'

    $exampleExit = New-DriftFixture 'example-exit-drift' {
        param($value)
        $value.examples[1].expectedExit = 22
    }
    Assert-Rejected $exampleExit 'expected 22' 'Executable example exit drift'

    $missingOption = New-DriftFixture 'missing-option-drift' {
        param($value)
        $value.requiredOptions.inspect = @($value.requiredOptions.inspect | Where-Object { $_ -cne '--output-budget' })
        $value.examples[1].argv = @($value.examples[1].argv | Where-Object { $_ -cne '--output-budget' -and $_ -cne '65536' })
    }
    Assert-Rejected $missingOption "Option surface for 'inspect' differs" 'Removed option drift'

    $errorExit = New-DriftFixture 'error-exit-drift' {
        param($value)
        $value.errorExits.unsupported = 99
    }
    Assert-Rejected $errorExit "Exit for error 'unsupported' differs" 'Error exit drift'

    $terminalExit = New-DriftFixture 'terminal-exit-drift' {
        param($value)
        $value.terminalExits.nonTerminal = 11
    }
    Assert-Rejected $terminalExit "Terminal exit 'nonTerminal' differs" 'Terminal exit drift'

    $fallback = New-DriftFixture 'fallback-drift' {
        param($value)
        $value.agentFallbackReasons = @($value.agentFallbackReasons | Select-Object -SkipLast 1)
    }
    Assert-Rejected $fallback 'Agent fallback facts differ' 'Fallback taxonomy drift'

    $routing = New-DriftFixture 'routing-drift' {
        param($value)
        $value.routingPolicy.authorizationDenied.inspectionInterface = 'none'
    }
    Assert-Rejected $routing 'Authorization-denial routing must inspect through vcs-agent' 'Authorization-denial routing drift'

    $readError = New-DriftFixture 'cleanup-read-error-drift' {
        param($value)
        $value.processKitCli.cleanup.readError = $true
    }
    Assert-Rejected $readError 'cleanup is not fail-closed' 'Cleanup read-error drift'

    $killError = New-DriftFixture 'cleanup-kill-error-drift' {
        param($value)
        $value.processKitCli.cleanup.killError = $true
    }
    Assert-Rejected $killError 'cleanup is not fail-closed' 'Cleanup kill-error drift'

    $controlTemplate = New-DriftFixture 'control-template-drift' {
        param($value)
        $value.processKitCli.cancelTemplate = @('processkit-cli', 'cancel')
    }
    Assert-Rejected $controlTemplate 'cancellation template drifted' 'Control template drift'

    Write-Host 'OK: platform apphost resolution and standalone Skill facts validate; option, exit, fallback, routing, cleanup, and control drift fail closed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}

exit 0
