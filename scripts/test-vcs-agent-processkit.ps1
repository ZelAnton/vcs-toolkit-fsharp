[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ProcessKitCliPath,

    [Parameter(Mandatory = $true)]
    [string] $VcsAgentPath,

    [string] $FixtureScriptPath = (Join-Path $PSScriptRoot 'vcs-agent-processkit-fixture.ps1'),

    [string] $EvidencePath
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

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

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [Parameter(Mandatory = $true)]
        [string] $StdoutPath,

        [Parameter(Mandatory = $true)]
        [string] $StderrPath
    )

    & $FilePath @Arguments 1> $StdoutPath 2> $StderrPath
    $LASTEXITCODE
}

function Read-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    Assert-Condition (Test-Path -LiteralPath $Path -PathType Leaf) "Expected JSON file '$Path' was not created."
    Get-Content -Raw -LiteralPath $Path -Encoding UTF8 | ConvertFrom-Json -Depth 64
}

function Read-EventStream {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $ScenarioDirectory
    )

    Assert-Condition (Test-Path -LiteralPath $Path -PathType Leaf) "Expected lifecycle stream '$Path' was not created."

    $validationExit = Invoke-Native `
        -FilePath $script:ProcessKitCli `
        -Arguments @('events', '--file', $Path, '--validate') `
        -StdoutPath (Join-Path $ScenarioDirectory 'events-validation.stdout.log') `
        -StderrPath (Join-Path $ScenarioDirectory 'events-validation.stderr.log')

    Assert-Condition ($validationExit -eq 0) "ProcessKit-CLI rejected lifecycle stream '$Path' with exit $validationExit."

    $events = @(
        Get-Content -LiteralPath $Path -Encoding UTF8 |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { $_ | ConvertFrom-Json -Depth 64 }
    )

    Assert-Condition ($events.Count -gt 0) "Lifecycle stream '$Path' was empty."

    foreach ($event in $events) {
        Assert-Condition ($event.schema_version -eq 1) "Lifecycle stream '$Path' contained a non-v1 event."
    }

    $terminal = @($events | Where-Object event -EQ 'runner_exit')
    Assert-Condition ($terminal.Count -eq 1) "Lifecycle stream '$Path' must contain exactly one runner_exit event."
    Assert-Condition ($events[-1].event -eq 'runner_exit') "runner_exit must be the terminal lifecycle record in '$Path'."

    ,$events
}

function Assert-CleanTerminal {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Events,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedCode,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedSource,

        [AllowNull()]
        [object] $ExpectedChildCode
    )

    $terminal = @($Events | Where-Object event -EQ 'runner_exit')[0]
    Assert-Condition ($terminal.code -eq $ExpectedCode) "runner_exit code was $($terminal.code), expected $ExpectedCode."
    Assert-Condition ($terminal.source -eq $ExpectedSource) "runner_exit source was '$($terminal.source)', expected '$ExpectedSource'."

    if ($null -eq $ExpectedChildCode) {
        Assert-Condition ($null -eq $terminal.child_code) 'runner_exit unexpectedly reported a child_code.'
    }
    else {
        Assert-Condition ($terminal.child_code -eq [int] $ExpectedChildCode) "runner_exit child_code was $($terminal.child_code), expected $ExpectedChildCode."
    }

    $cleanup = @($Events | Where-Object event -EQ 'cleanup_finished') | Select-Object -Last 1
    Assert-Condition ($null -ne $cleanup) 'The lifecycle stream did not report cleanup_finished.'
    Assert-Condition ($cleanup.remaining -eq 0) "ProcessKit-CLI left $($cleanup.remaining) process(es) after cleanup."
    Assert-Condition (-not $cleanup.kill_error) 'ProcessKit-CLI reported a kill_error during cleanup.'
    Assert-Condition (-not $cleanup.read_error) 'ProcessKit-CLI could not confirm the final contained-process set.'
}

function Assert-AgentEnvelope {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Operation,

        [Parameter(Mandatory = $true)]
        [string] $Status,

        [AllowNull()]
        [object] $ErrorCode
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    Assert-Condition ($bytes.Length -gt 0) "vcs-agent result '$Path' was empty."
    Assert-Condition ($bytes[-1] -eq 10) "vcs-agent result '$Path' was not LF-terminated."

    $envelope = Read-JsonFile $Path
    Assert-Condition ($envelope.contractVersion -eq '1') 'vcs-agent returned a non-v1 contract envelope.'
    Assert-Condition ($envelope.operation -eq $Operation) "vcs-agent operation was '$($envelope.operation)', expected '$Operation'."
    Assert-Condition ($envelope.status -eq $Status) "vcs-agent status was '$($envelope.status)', expected '$Status'."
    Assert-Condition $envelope.terminal 'vcs-agent returned a non-terminal envelope.'

    if ($null -eq $ErrorCode) {
        Assert-Condition ($null -eq $envelope.error) 'A successful vcs-agent envelope unexpectedly contained an error.'
    }
    else {
        Assert-Condition ($null -ne $envelope.error) 'An error vcs-agent envelope did not contain structured error data.'
        Assert-Condition ($envelope.error.code -eq $ErrorCode) "vcs-agent error code was '$($envelope.error.code)', expected '$ErrorCode'."
    }

    $envelope
}

function New-ScenarioDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $directory = Join-Path $script:Scratch $Name
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $directory 'capture')) | Out-Null
    $directory
}

function Invoke-RunScenario {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string[]] $RunArguments
    )

    $directory = New-ScenarioDirectory $Name
    $eventsPath = Join-Path $directory 'events.jsonl'
    $arguments = @('run', '--jsonl', $eventsPath) + $RunArguments
    $exitCode = Invoke-Native `
        -FilePath $script:ProcessKitCli `
        -Arguments $arguments `
        -StdoutPath (Join-Path $directory 'runner.stdout.log') `
        -StderrPath (Join-Path $directory 'runner.stderr.log')

    [pscustomobject]@{
        Name = $Name
        Directory = $directory
        EventsPath = $eventsPath
        ExitCode = $exitCode
    }
}

$script:ProcessKitCli = [System.IO.Path]::GetFullPath($ProcessKitCliPath)
$vcsAgent = [System.IO.Path]::GetFullPath($VcsAgentPath)
$fixture = [System.IO.Path]::GetFullPath($FixtureScriptPath)

Assert-Condition (Test-Path -LiteralPath $script:ProcessKitCli -PathType Leaf) "ProcessKit-CLI binary '$script:ProcessKitCli' does not exist."
Assert-Condition (Test-Path -LiteralPath $vcsAgent -PathType Leaf) "Packaged vcs-agent executable '$vcsAgent' does not exist."
Assert-Condition (Test-Path -LiteralPath $fixture -PathType Leaf) "Fixture script '$fixture' does not exist."

$powerShell = (Get-Process -Id $PID).Path
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$script:Scratch = Join-Path $tempRoot ('vcs-agent-processkit-' + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($script:Scratch) | Out-Null

$requiredSurface = @(
    'run',
    'run:--jsonl',
    'run:--run-id',
    'run:--timeout',
    'run:--idle-timeout',
    'run:--grace',
    'run:--capture-dir',
    'run:--capture-max-bytes',
    'run:--capture-overflow',
    'run:--no-echo',
    'run:--detach',
    'run:resource-summary',
    'cancel',
    'cancel:--run-id',
    'wait',
    'wait:--run-id',
    'wait:--timeout',
    'wait:--report-outcome',
    'events',
    'events:--file',
    'events:--validate'
)

$scenarioResults = [System.Collections.Generic.List[object]]::new()
$preflightReport = $null
$preflightRejection = $null
$successEnvelope = $null
$proofStatus = 'failed'
$failure = $null
$detachedRunId = $null
$detachedFinished = $false

try {
    $preflightDirectory = New-ScenarioDirectory 'preflight'
    $probeArguments = @(
        'probe',
        '--json',
        '--require-schema-version',
        '1',
        '--require-exit-code-band',
        '100-119'
    )

    foreach ($surface in $requiredSurface) {
        $probeArguments += @('--require-surface', $surface)
    }

    $probeExit = Invoke-Native `
        -FilePath $script:ProcessKitCli `
        -Arguments $probeArguments `
        -StdoutPath (Join-Path $preflightDirectory 'probe.json') `
        -StderrPath (Join-Path $preflightDirectory 'probe.stderr.log')

    $preflightReport = Read-JsonFile (Join-Path $preflightDirectory 'probe.json')
    Assert-Condition ($probeExit -eq 0) "ProcessKit-CLI preflight failed closed with exit ${probeExit}: $($preflightReport.mismatches -join '; ')."
    Assert-Condition ($preflightReport.probe_version -eq 1) 'ProcessKit-CLI returned an unsupported probe report version.'
    Assert-Condition ($preflightReport.binary -eq 'processkit-cli') 'The preflight candidate did not identify as processkit-cli.'
    Assert-Condition $preflightReport.compatible 'ProcessKit-CLI reported an incompatible supervision surface.'
    Assert-Condition ($preflightReport.mismatches.Count -eq 0) 'A compatible ProcessKit-CLI preflight contained mismatches.'

    $rejectionExit = Invoke-Native `
        -FilePath $script:ProcessKitCli `
        -Arguments @(
            'probe',
            '--json',
            '--require-schema-version',
            '1',
            '--require-exit-code-band',
            '100-119',
            '--require-surface',
            'vcs-agent-proof:intentionally-absent'
        ) `
        -StdoutPath (Join-Path $preflightDirectory 'probe-rejection.json') `
        -StderrPath (Join-Path $preflightDirectory 'probe-rejection.stderr.log')
    $preflightRejection = Read-JsonFile (Join-Path $preflightDirectory 'probe-rejection.json')
    Assert-Condition ($rejectionExit -eq 110) "An incompatible ProcessKit-CLI preflight exited $rejectionExit, expected 110."
    Assert-Condition (-not $preflightRejection.compatible) 'An intentionally incompatible ProcessKit-CLI preflight reported compatible=true.'
    Assert-Condition ($preflightRejection.mismatches.Count -eq 1) 'The intentionally incompatible preflight did not report exactly one missing surface.'

    $success = Invoke-RunScenario `
        -Name 'success' `
        -RunArguments @(
            '--capture-dir', (Join-Path $script:Scratch 'success/capture'),
            '--capture-max-bytes', '64k',
            '--no-echo',
            '--',
            $vcsAgent,
            'probe'
        )
    Assert-Condition ($success.ExitCode -eq 0) "Supervised vcs-agent probe exited $($success.ExitCode), expected 0."
    $successEvents = Read-EventStream $success.EventsPath $success.Directory
    Assert-CleanTerminal $successEvents 0 'child_exit' 0
    $successEnvelope = Assert-AgentEnvelope (Join-Path $success.Directory 'capture/stdout.log') 'probe' 'success' $null
    $scenarioResults.Add([pscustomobject]@{ name = 'success'; status = 'passed'; exitCode = 0 })

    $nonSuccess = Invoke-RunScenario `
        -Name 'non-success' `
        -RunArguments @(
            '--capture-dir', (Join-Path $script:Scratch 'non-success/capture'),
            '--capture-max-bytes', '64k',
            '--no-echo',
            '--',
            $vcsAgent,
            'raw'
        )
    Assert-Condition ($nonSuccess.ExitCode -eq 22) "ProcessKit-CLI did not preserve vcs-agent exit 22; got $($nonSuccess.ExitCode)."
    $nonSuccessEvents = Read-EventStream $nonSuccess.EventsPath $nonSuccess.Directory
    Assert-CleanTerminal $nonSuccessEvents 22 'child_exit' 22
    $null = Assert-AgentEnvelope (Join-Path $nonSuccess.Directory 'capture/stdout.log') 'command' 'error' 'invalid-input'
    $scenarioResults.Add([pscustomobject]@{ name = 'non-success'; status = 'passed'; exitCode = 22 })

    $timeout = Invoke-RunScenario `
        -Name 'timeout' `
        -RunArguments @(
            '--timeout', '500ms',
            '--grace', '50ms',
            '--no-echo',
            '--',
            $powerShell,
            '-NoProfile',
            '-File',
            $fixture,
            '-Mode',
            'sleep'
        )
    Assert-Condition ($timeout.ExitCode -eq 106) "Overall timeout exited $($timeout.ExitCode), expected 106."
    $timeoutEvents = Read-EventStream $timeout.EventsPath $timeout.Directory
    Assert-CleanTerminal $timeoutEvents 106 'timeout' $null
    $timeoutEvent = @($timeoutEvents | Where-Object event -EQ 'timeout')
    Assert-Condition ($timeoutEvent.Count -eq 1 -and $timeoutEvent[0].reason -eq 'overall') 'Overall timeout lifecycle evidence was absent or misclassified.'
    $scenarioResults.Add([pscustomobject]@{ name = 'timeout'; status = 'passed'; exitCode = 106 })

    $idle = Invoke-RunScenario `
        -Name 'idle' `
        -RunArguments @(
            '--idle-timeout', '750ms',
            '--grace', '50ms',
            '--capture-dir', (Join-Path $script:Scratch 'idle/capture'),
            '--capture-max-bytes', '64k',
            '--no-echo',
            '--',
            $powerShell,
            '-NoProfile',
            '-File',
            $fixture,
            '-Mode',
            'idle'
        )
    Assert-Condition ($idle.ExitCode -eq 106) "Idle timeout exited $($idle.ExitCode), expected 106."
    $idleEvents = Read-EventStream $idle.EventsPath $idle.Directory
    Assert-CleanTerminal $idleEvents 106 'timeout' $null
    $idleTimeoutEvent = @($idleEvents | Where-Object event -EQ 'timeout')
    Assert-Condition ($idleTimeoutEvent.Count -eq 1 -and $idleTimeoutEvent[0].reason -eq 'idle') 'Idle timeout lifecycle evidence was absent or misclassified.'
    Assert-Condition ((Get-Content -Raw -LiteralPath (Join-Path $idle.Directory 'capture/stdout.log') -Encoding UTF8).Contains('fixture-ready')) 'The idle fixture did not produce its readiness output before going silent.'
    $scenarioResults.Add([pscustomobject]@{ name = 'idle'; status = 'passed'; exitCode = 106 })

    $bounded = Invoke-RunScenario `
        -Name 'bounded-capture' `
        -RunArguments @(
            '--capture-dir', (Join-Path $script:Scratch 'bounded-capture/capture'),
            '--capture-max-bytes', '512',
            '--capture-overflow', 'truncate',
            '--no-echo',
            '--',
            $vcsAgent,
            'probe'
        )
    Assert-Condition ($bounded.ExitCode -eq 0) "Bounded capture scenario exited $($bounded.ExitCode), expected 0."
    $boundedEvents = Read-EventStream $bounded.EventsPath $bounded.Directory
    Assert-CleanTerminal $boundedEvents 0 'child_exit' 0
    $capturedEvent = @($boundedEvents | Where-Object event -EQ 'output_captured')
    Assert-Condition ($capturedEvent.Count -eq 1) 'Bounded capture did not emit output_captured.'
    Assert-Condition $capturedEvent[0].stdout.truncated 'Bounded capture did not report stdout truncation at 512 bytes.'
    Assert-Condition ($capturedEvent[0].stdout.bytes -gt 512) "Bounded capture observed only $($capturedEvent[0].stdout.bytes) stdout bytes, so the truncation verdict was not exercised."
    Assert-Condition ((Get-Item -LiteralPath (Join-Path $bounded.Directory 'capture/stdout.log')).Length -eq 512) 'Bounded capture file length did not match its configured ceiling.'
    $scenarioResults.Add([pscustomobject]@{ name = 'bounded-capture'; status = 'passed'; exitCode = 0 })

    $cancellationDirectory = New-ScenarioDirectory 'cancellation'
    $detachedRunId = 'vcs-agent-t208-' + [Guid]::NewGuid().ToString('N')
    $cancellationEvents = Join-Path $cancellationDirectory 'events.jsonl'
    $detachExit = Invoke-Native `
        -FilePath $script:ProcessKitCli `
        -Arguments @(
            'run',
            '--detach',
            '--run-id',
            $detachedRunId,
            '--jsonl',
            $cancellationEvents,
            '--capture-dir',
            (Join-Path $cancellationDirectory 'capture'),
            '--capture-max-bytes',
            '64k',
            '--',
            $powerShell,
            '-NoProfile',
            '-File',
            $fixture,
            '-Mode',
            'sleep'
        ) `
        -StdoutPath (Join-Path $cancellationDirectory 'detach.stdout.log') `
        -StderrPath (Join-Path $cancellationDirectory 'detach.stderr.log')
    Assert-Condition ($detachExit -eq 0) "Detached cancellation fixture failed to start with exit $detachExit."

    $cancelExit = Invoke-Native `
        -FilePath $script:ProcessKitCli `
        -Arguments @('cancel', '--run-id', $detachedRunId) `
        -StdoutPath (Join-Path $cancellationDirectory 'cancel.stdout.log') `
        -StderrPath (Join-Path $cancellationDirectory 'cancel.stderr.log')
    Assert-Condition ($cancelExit -eq 0) "Control-plane cancellation failed with exit $cancelExit."

    $waitExit = Invoke-Native `
        -FilePath $script:ProcessKitCli `
        -Arguments @('wait', '--run-id', $detachedRunId, '--timeout', '15s', '--report-outcome') `
        -StdoutPath (Join-Path $cancellationDirectory 'wait.json') `
        -StderrPath (Join-Path $cancellationDirectory 'wait.stderr.log')
    Assert-Condition ($waitExit -eq 0) "Waiting for the cancelled run failed with exit $waitExit."
    $waitReport = Read-JsonFile (Join-Path $cancellationDirectory 'wait.json')
    Assert-Condition ($waitReport.status -in @('reported', 'unknown')) "Cancellation wait status was '$($waitReport.status)', expected the published reported/unknown taxonomy."

    if ($waitReport.status -eq 'reported') {
        Assert-Condition ($waitReport.code -eq 108 -and $waitReport.source -eq 'control_cancel') 'Cancellation wait report did not preserve the control_cancel outcome.'
    }
    else {
        Assert-Condition ($null -eq $waitReport.code -and $null -eq $waitReport.source) 'An unknown wait outcome must not fabricate an exit classification.'
    }
    $cancellationStream = Read-EventStream $cancellationEvents $cancellationDirectory
    Assert-CleanTerminal $cancellationStream 108 'control_cancel' $null
    $cancelledEvent = @($cancellationStream | Where-Object event -EQ 'cancelled')
    Assert-Condition ($cancelledEvent.Count -eq 1 -and $cancelledEvent[0].source -eq 'control_cancel') 'Cancellation lifecycle evidence was absent or misclassified.'
    $detachedFinished = $true
    $scenarioResults.Add([pscustomobject]@{ name = 'cancellation'; status = 'passed'; exitCode = 108 })

    $nestedDirectory = New-ScenarioDirectory 'nested-containment'
    $innerDirectory = Join-Path $nestedDirectory 'inner'
    [System.IO.Directory]::CreateDirectory($innerDirectory) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $innerDirectory 'capture')) | Out-Null
    $outerEvents = Join-Path $nestedDirectory 'events.jsonl'
    $innerEvents = Join-Path $innerDirectory 'events.jsonl'
    $nestedExit = Invoke-Native `
        -FilePath $script:ProcessKitCli `
        -Arguments @(
            'run',
            '--jsonl',
            $outerEvents,
            '--capture-dir',
            (Join-Path $nestedDirectory 'capture'),
            '--capture-max-bytes',
            '64k',
            '--no-echo',
            '--',
            $script:ProcessKitCli,
            'run',
            '--jsonl',
            $innerEvents,
            '--capture-dir',
            (Join-Path $innerDirectory 'capture'),
            '--capture-max-bytes',
            '64k',
            '--no-echo',
            '--',
            $vcsAgent,
            'probe'
        ) `
        -StdoutPath (Join-Path $nestedDirectory 'runner.stdout.log') `
        -StderrPath (Join-Path $nestedDirectory 'runner.stderr.log')
    Assert-Condition ($nestedExit -eq 0) "Nested ProcessKit-CLI composition exited $nestedExit, expected 0."
    $outerStream = Read-EventStream $outerEvents $nestedDirectory
    $innerStream = Read-EventStream $innerEvents $innerDirectory
    Assert-CleanTerminal $outerStream 0 'child_exit' 0
    Assert-CleanTerminal $innerStream 0 'child_exit' 0
    $outerStarted = @($outerStream | Where-Object event -EQ 'run_started')[0]
    $innerStarted = @($innerStream | Where-Object event -EQ 'run_started')[0]
    Assert-Condition ($outerStarted.root_pid -ne $innerStarted.root_pid) 'Nested lifecycle streams reported the same root process.'
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($outerStarted.mechanism)) 'Outer containment mechanism was not reported.'
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($innerStarted.mechanism)) 'Inner containment mechanism was not reported.'
    $null = Assert-AgentEnvelope (Join-Path $innerDirectory 'capture/stdout.log') 'probe' 'success' $null
    $scenarioResults.Add([pscustomobject]@{
        name = 'nested-containment'
        status = 'passed'
        exitCode = 0
        outerMechanism = $outerStarted.mechanism
        innerMechanism = $innerStarted.mechanism
    })

    $proofStatus = 'passed'
}
catch {
    $failure = $_.Exception.Message
    throw
}
finally {
    if ($null -ne $detachedRunId -and -not $detachedFinished) {
        & $script:ProcessKitCli kill --run-id $detachedRunId 1> $null 2> $null
        & $script:ProcessKitCli wait --run-id $detachedRunId --timeout 15s 1> $null 2> $null
    }

    $evidence = [ordered]@{
        contract = 'vcs-agent-processkit-proof-v1'
        status = $proofStatus
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        preflight = if ($null -eq $preflightReport) { $null } else {
            [ordered]@{
                probeVersion = $preflightReport.probe_version
                binary = $preflightReport.binary
                version = $preflightReport.version
                schemaVersion = $preflightReport.schema_version
                exitCodeBand = $preflightReport.exit_code_band
                requiredSurface = $requiredSurface
                compatible = $preflightReport.compatible
                mismatches = @($preflightReport.mismatches)
            }
        }
        preflightRejection = if ($null -eq $preflightRejection) { $null } else {
            [ordered]@{
                exitCode = 110
                compatible = $preflightRejection.compatible
                mismatches = @($preflightRejection.mismatches)
            }
        }
        agentResult = $successEnvelope
        scenarios = @($scenarioResults)
        failure = $failure
    }

    if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        $resolvedEvidence = [System.IO.Path]::GetFullPath($EvidencePath)
        $evidenceDirectory = [System.IO.Path]::GetDirectoryName($resolvedEvidence)

        if (-not [string]::IsNullOrWhiteSpace($evidenceDirectory)) {
            [System.IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null
        }

        $evidence | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $resolvedEvidence -Encoding utf8NoBOM
    }

    $resolvedScratch = [System.IO.Path]::GetFullPath($script:Scratch)
    Assert-Condition ($resolvedScratch.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) 'Refusing to clean a scratch directory outside the system temp root.'
    Assert-Condition ($resolvedScratch -ne $tempRoot) 'Refusing to clean the system temp root itself.'

    if ([System.IO.Directory]::Exists($resolvedScratch)) {
        [System.IO.Directory]::Delete($resolvedScratch, $true)
    }
}

Write-Host "ProcessKit-CLI $($preflightReport.version) supervised proof passed: $($scenarioResults.Count) scenarios."
