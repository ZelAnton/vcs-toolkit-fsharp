#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Fails closed when the standalone using-vcs-agent Skill drifts from the built tool.

.DESCRIPTION
    Validates Skill metadata and workflow invariants, compares the reference command and
    option surface with real executions of the built vcs-agent binary, and keeps the
    embedded ProcessKit-CLI preflight aligned with the cross-binary proof.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $VcsAgentPath,

    [string] $SkillPath = (Join-Path $PSScriptRoot '../skills/using-vcs-agent/SKILL.md'),

    [string] $ContractPath = (Join-Path $PSScriptRoot '../skills/using-vcs-agent/references/contract.v1.json'),

    [string] $ProcessKitProofPath = (Join-Path $PSScriptRoot 'test-vcs-agent-processkit.ps1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)]
        [bool] $Condition,

        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Read-JsonObject {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    Assert-Condition (Test-Path -LiteralPath $Path -PathType Leaf) "Required JSON file '$Path' does not exist."
    $value = Get-Content -Raw -LiteralPath $Path -Encoding UTF8 | ConvertFrom-Json -Depth 100
    Assert-Condition ($null -ne $value -and $value.GetType().Name -ne 'Object[]') "JSON file '$Path' must contain one object."
    $value
}

function Read-SkillMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    Assert-Condition (Test-Path -LiteralPath $Path -PathType Leaf) "Skill entrypoint '$Path' does not exist."
    $lines = @(Get-Content -LiteralPath $Path -Encoding UTF8)
    Assert-Condition ($lines.Count -ge 4 -and $lines[0] -ceq '---') 'SKILL.md must start with YAML frontmatter.'

    $closing = -1

    for ($index = 1; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -ceq '---') {
            $closing = $index
            break
        }
    }

    Assert-Condition ($closing -gt 1) 'SKILL.md frontmatter is not closed.'
    $metadata = [ordered]@{}

    for ($index = 1; $index -lt $closing; $index++) {
        $line = $lines[$index]

        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $separator = $line.IndexOf(':')
        Assert-Condition ($separator -gt 0) "SKILL.md frontmatter line '$line' is not scalar key: value metadata."
        $name = $line.Substring(0, $separator).Trim()
        $value = $line.Substring($separator + 1).Trim()
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($name) -and -not [string]::IsNullOrWhiteSpace($value)) "SKILL.md frontmatter line '$line' is incomplete."
        Assert-Condition (-not $metadata.Contains($name)) "SKILL.md frontmatter repeats '$name'."
        $metadata[$name] = $value
    }

    [pscustomobject]@{
        Metadata = $metadata
        Text = $lines -join "`n"
    }
}

function Invoke-VcsAgent {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]] $Arguments
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8

    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()

    try {
        $process.StartInfo = $startInfo
        Assert-Condition $process.Start() "Failed to start '$FilePath'."
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()

        if (-not $process.WaitForExit(20000)) {
            try {
                $process.Kill($true)
            }
            catch [System.InvalidOperationException] {
                # The process exited between the deadline check and kill request.
            }

            throw "vcs-agent exceeded the 20-second factual-drift deadline for: $($Arguments -join ' ')"
        }

        [pscustomobject]@{
            ExitCode = $process.ExitCode
            Stdout = $stdout.GetAwaiter().GetResult()
            Stderr = $stderr.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

try {
    $binary = [System.IO.Path]::GetFullPath($VcsAgentPath)
    $skillFile = [System.IO.Path]::GetFullPath($SkillPath)
    $contractFile = [System.IO.Path]::GetFullPath($ContractPath)
    $proofFile = [System.IO.Path]::GetFullPath($ProcessKitProofPath)
    Assert-Condition (Test-Path -LiteralPath $binary -PathType Leaf) "Built vcs-agent binary '$binary' does not exist."

    $contract = Read-JsonObject $contractFile
    $skill = Read-SkillMetadata $skillFile
    Assert-Condition ($contract.skillContractVersion -ceq 'using-vcs-agent/v1') 'Skill contract version must be using-vcs-agent/v1.'
    Assert-Condition ($skill.Metadata.Count -eq 2) 'SKILL.md frontmatter must contain only name and description.'
    Assert-Condition ($skill.Metadata.name -ceq $contract.metadata.name) 'SKILL.md name differs from the reference contract.'
    Assert-Condition ($skill.Metadata.description -ceq $contract.metadata.description) 'SKILL.md description differs from the reference contract.'

    foreach ($requiredText in @(
            'Run `vcs-agent probe`, then `vcs-agent inspect',
            'exact selected paths',
            'Immediately before each commit or publication',
            'at least 60 seconds',
            'tracked root or',
            'PID plus start-identity',
            'terminal `runner_exit`',
            'host''s sandbox',
            'fallbackReason'
        )) {
        Assert-Condition $skill.Text.Contains($requiredText, [StringComparison]::Ordinal) "SKILL.md omits required workflow fact '$requiredText'."
    }

    $expectedClassifications = @('unsupported', 'missing-executable', 'diagnostic-output-required')
    $actualClassifications = @($contract.rawCliFallbackClassifications.PSObject.Properties.Name)
    Assert-Condition (($actualClassifications -join "`n") -ceq ($expectedClassifications -join "`n")) 'Raw CLI fallback classifications differ from the three allowed grounds.'

    $expectedAgentFallbacks = @(
        'operation-not-implemented',
        'missing-executable',
        'unsupported-backend',
        'unsupported-forge',
        'raw-diagnostic-required'
    )
    Assert-Condition ((@($contract.agentFallbackReasons) -join "`n") -ceq ($expectedAgentFallbacks -join "`n")) 'Agent fallback facts drifted.'

    $processKit = $contract.processKitCli
    Assert-Condition ($processKit.minimumExpectedDurationSeconds -eq 60) 'ProcessKit-CLI duration threshold must remain 60 seconds.'
    Assert-Condition $processKit.wrapOnDescendantCleanupRisk 'Descendant-risk operations must select ProcessKit-CLI.'
    Assert-Condition ($processKit.readiness.processGroupMembership -ceq 'tracked-root-only') 'process_group readiness must remain leader-aware.'
    Assert-Condition ($processKit.readiness.exactIdentity -ceq 'pid-plus-start-identity') 'Cleanup must retain exact PID/start identity.'
    Assert-Condition ($processKit.terminalEvent -ceq 'runner_exit') 'ProcessKit-CLI terminal event drifted.'
    Assert-Condition ($processKit.cleanup.remaining -eq 0 -and -not $processKit.cleanup.killError -and -not $processKit.cleanup.readError -and $processKit.cleanup.allExactIdentitiesGone) 'ProcessKit-CLI cleanup is not fail-closed.'

    Assert-Condition (Test-Path -LiteralPath $proofFile -PathType Leaf) "ProcessKit-CLI proof '$proofFile' does not exist."
    $proofSource = Get-Content -Raw -LiteralPath $proofFile -Encoding UTF8
    $surfaceBlock = [regex]::Match($proofSource, '(?s)\$requiredSurface\s*=\s*@\((?<body>.*?)\r?\n\)')
    Assert-Condition $surfaceBlock.Success 'Could not locate the ProcessKit-CLI required-surface declaration.'
    $proofSurface = @([regex]::Matches($surfaceBlock.Groups['body'].Value, "'([^']+)'") | ForEach-Object { $_.Groups[1].Value })
    Assert-Condition (($proofSurface -join "`n") -ceq (@($processKit.requiredSurface) -join "`n")) 'Skill ProcessKit-CLI required surface drifted from the executable proof.'

    $expectedPreflightPrefix = @('probe', '--json', '--require-schema-version', '1', '--require-exit-code-band', '100-119')
    Assert-Condition ((@($processKit.preflight.argvPrefix) -join "`n") -ceq ($expectedPreflightPrefix -join "`n")) 'Skill ProcessKit-CLI preflight prefix drifted from the executable proof.'
    Assert-Condition ((@($processKit.preflight.repeatForEachRequiredSurface) -join "`n") -ceq "--require-surface`n<surface>") 'Skill preflight no longer requires every published surface.'
    Assert-Condition ($processKit.preflight.command -ceq 'processkit-cli' -and $processKit.preflight.success.exit -eq 0 -and $processKit.preflight.success.binary -ceq 'processkit-cli' -and $processKit.preflight.success.probeVersion -eq 1 -and $processKit.preflight.success.compatible -and $processKit.preflight.success.mismatchCount -eq 0) 'Skill ProcessKit-CLI preflight success gate drifted.'
    Assert-Condition ((@($processKit.supervisedRunTemplate) -join ' ') -ceq 'processkit-cli run --jsonl <events-path> --capture-dir <capture-dir> --capture-max-bytes 64k --no-echo -- vcs-agent <operation> <arguments>') 'Skill supervised-run template drifted.'
    Assert-Condition ((@($processKit.validateEventsTemplate) -join ' ') -ceq 'processkit-cli events --file <events-path> --validate') 'Skill lifecycle-validation template drifted.'
    Assert-Condition $proofSource.Contains("snapshot.mechanism -eq 'process_group'", [StringComparison]::Ordinal) 'ProcessKit-CLI proof lost mechanism-aware process_group readiness.'
    Assert-Condition $proofSource.Contains('Assert-ProcessIdentitiesGone', [StringComparison]::Ordinal) 'ProcessKit-CLI proof lost exact-identity cleanup.'

    $missingRepository = Join-Path ([System.IO.Path]::GetTempPath()) ('vcs-agent-skill-missing-' + [Guid]::NewGuid().ToString('N'))
    $examplesByOperation = @{}

    foreach ($example in @($contract.examples)) {
        $arguments = @($example.argv | ForEach-Object { if ($_ -ceq '__missing_repo__') { $missingRepository } else { [string] $_ } })
        $result = Invoke-VcsAgent $binary $arguments
        Assert-Condition ($result.ExitCode -eq [int] $example.expectedExit) "Example '$($example.id)' exited $($result.ExitCode), expected $($example.expectedExit)."

        try {
            $envelope = $result.Stdout | ConvertFrom-Json -Depth 100
        }
        catch {
            throw "Example '$($example.id)' did not emit one JSON envelope: $($_.Exception.Message)"
        }

        Assert-Condition ($envelope.contractVersion -ceq $contract.agentContractVersion) "Example '$($example.id)' returned another agent contract."
        Assert-Condition ($envelope.operation -ceq $example.operation) "Example '$($example.id)' selected operation '$($envelope.operation)'."

        if ($null -eq $example.expectedError) {
            Assert-Condition ($null -eq $envelope.error) "Example '$($example.id)' unexpectedly returned an error."
        }
        else {
            Assert-Condition ($null -ne $envelope.error -and $envelope.error.code -ceq $example.expectedError) "Example '$($example.id)' returned the wrong structured error."
        }

        $examplesByOperation[$example.operation] = $arguments
    }

    $probeResult = Invoke-VcsAgent $binary @('probe')
    Assert-Condition ($probeResult.ExitCode -eq 0) 'vcs-agent probe failed.'
    $probe = $probeResult.Stdout | ConvertFrom-Json -Depth 100
    $probeCommands = @($probe.data.operations | ForEach-Object { $_.name })
    Assert-Condition (($probeCommands -join "`n") -ceq (@($contract.commands) -join "`n")) 'Skill command set drifted from vcs-agent probe.'

    foreach ($operationProperty in $contract.requiredOptions.PSObject.Properties) {
        $operation = $operationProperty.Name
        Assert-Condition $examplesByOperation.ContainsKey($operation) "No executable example covers '$operation'."
        $arguments = @($examplesByOperation[$operation])

        foreach ($option in @($operationProperty.Value)) {
            Assert-Condition ($option -in $arguments) "Example '$operation' does not exercise required option '$option'."
        }
    }

    Write-Host "OK: using-vcs-agent Skill matches contract $($contract.skillContractVersion), built agent v$($contract.agentContractVersion), and $(@($contract.examples).Count) executable examples." -ForegroundColor Green
}
catch {
    Write-Error "using-vcs-agent Skill validation failed: $($_.Exception.Message)"
    exit 1
}
