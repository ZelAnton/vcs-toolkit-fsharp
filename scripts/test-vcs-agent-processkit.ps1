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

function Read-ValidatedEventStream {
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

    ,$events
}

function Read-EventStream {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $ScenarioDirectory
    )

    $events = Read-ValidatedEventStream $Path $ScenarioDirectory
    $terminal = @($events | Where-Object event -EQ 'runner_exit')
    Assert-Condition ($terminal.Count -eq 1) "Lifecycle stream '$Path' must contain exactly one runner_exit event."
    Assert-Condition ($events[-1].event -eq 'runner_exit') "runner_exit must be the terminal lifecycle record in '$Path'."

    ,$events
}

function Assert-CleanTerminalState {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Events
    )

    $cleanup = @($Events | Where-Object event -EQ 'cleanup_finished') | Select-Object -Last 1
    Assert-Condition ($null -ne $cleanup) 'The lifecycle stream did not report cleanup_finished.'
    Assert-Condition ($cleanup.remaining -eq 0) "ProcessKit-CLI left $($cleanup.remaining) process(es) after cleanup."
    Assert-Condition (-not $cleanup.kill_error) 'ProcessKit-CLI reported a kill_error during cleanup.'
    Assert-Condition (-not $cleanup.read_error) 'ProcessKit-CLI could not confirm the final contained-process set.'
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

    Assert-CleanTerminalState $Events
}

function Get-ProcessIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [int] $Id
    )

    try {
        $process = [System.Diagnostics.Process]::GetProcessById($Id)

        try {
            [pscustomobject]@{
                pid = $Id
                startTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks
            }
        }
        finally {
            $process.Dispose()
        }
    }
    catch [System.ArgumentException] {
        $null
    }
    catch [System.InvalidOperationException] {
        $null
    }
}

function Test-ProcessIdentityAlive {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Identity
    )

    $current = Get-ProcessIdentity -Id ([int] $Identity.pid)
    $null -ne $current -and $current.startTimeUtcTicks -eq [long] $Identity.startTimeUtcTicks
}

function Assert-ProcessIdentityGone {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Identity,

        [Parameter(Mandatory = $true)]
        [string] $Description,

        [int] $TimeoutSeconds = 15
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (-not (Test-ProcessIdentityAlive $Identity)) {
            return
        }

        Start-Sleep -Milliseconds 100
    }

    throw "$Description process identity pid=$($Identity.pid), startTimeUtcTicks=$($Identity.startTimeUtcTicks) survived teardown."
}

function Assert-ProcessIdentitiesGone {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $IdentityChecks,

        [int] $TimeoutSeconds = 15
    )

    foreach ($check in $IdentityChecks) {
        Assert-ProcessIdentityGone $check.Identity $check.Description $TimeoutSeconds
    }
}

function Invoke-VerifiedIdentityRecovery {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $IdentityChecks
    )

    $failures = [System.Collections.Generic.List[string]]::new()

    foreach ($check in $IdentityChecks) {
        if (-not (Test-ProcessIdentityAlive $check.Identity)) {
            continue
        }

        try {
            $process = [System.Diagnostics.Process]::GetProcessById([int] $check.Identity.pid)

            try {
                $actualStartTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks

                if ($actualStartTimeUtcTicks -ne [long] $check.Identity.startTimeUtcTicks) {
                    continue
                }

                $process.Kill($true)

                if (-not $process.WaitForExit(10000)) {
                    throw "$($check.Description) did not exit within the 10s emergency-recovery deadline."
                }
            }
            finally {
                $process.Dispose()
            }
        }
        catch {
            if (Test-ProcessIdentityAlive $check.Identity) {
                $failures.Add("$($check.Description) exact-identity recovery failed: $($_.Exception.Message)")
            }
        }
    }

    foreach ($check in $IdentityChecks) {
        try {
            Assert-ProcessIdentityGone $check.Identity $check.Description
        }
        catch {
            $failures.Add($_.Exception.Message)
        }
    }

    if ($failures.Count -gt 0) {
        throw "Exact-identity recovery was not confirmed: $($failures -join '; ')"
    }
}

function Read-RunRootIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [string] $EventsPath,

        [string] $Description = 'detached run',

        [int] $TimeoutSeconds = 15
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $EventsPath -PathType Leaf) {
            foreach ($line in Get-Content -LiteralPath $EventsPath -Encoding UTF8) {
                if ([string]::IsNullOrWhiteSpace($line)) {
                    continue
                }

                try {
                    $event = $line | ConvertFrom-Json -Depth 64
                }
                catch {
                    continue
                }

                if ($event.event -eq 'run_started' -and $null -ne $event.root_pid) {
                    $identity = Get-ProcessIdentity -Id ([int] $event.root_pid)

                    if ($null -ne $identity) {
                        return $identity
                    }
                }
            }
        }

        Start-Sleep -Milliseconds 100
    }

    throw "Could not observe a live root PID/start-time identity for $Description within ${TimeoutSeconds}s."
}

function Read-FixtureIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [int] $TimeoutSeconds = 15
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            try {
                $identity = Read-JsonFile $Path

                if (
                    $identity.fixture.pid -gt 0 -and
                    $identity.fixture.startTimeUtcTicks -gt 0 -and
                    $identity.descendant.pid -gt 0 -and
                    $identity.descendant.startTimeUtcTicks -gt 0
                ) {
                    return $identity
                }
            }
            catch {
                # The fixture publishes by atomic rename, but tolerate a transient reader race.
            }
        }

        Start-Sleep -Milliseconds 100
    }

    throw "The descendant fixture did not publish a complete PID/start-time identity at '$Path' within ${TimeoutSeconds}s."
}

function Invoke-DetachedTeardown {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RunId,

        [Parameter(Mandatory = $true)]
        [ValidateSet('cancel', 'kill')]
        [string] $Action,

        [Parameter(Mandatory = $true)]
        [string] $EventsPath,

        [Parameter(Mandatory = $true)]
        [string] $ScenarioDirectory,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedCode,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedSource
    )

    $controlExit = Invoke-Native `
        -FilePath $script:ProcessKitCli `
        -Arguments @($Action, '--run-id', $RunId) `
        -StdoutPath (Join-Path $ScenarioDirectory "$Action.stdout.log") `
        -StderrPath (Join-Path $ScenarioDirectory "$Action.stderr.log")

    $waitExit = Invoke-Native `
        -FilePath $script:ProcessKitCli `
        -Arguments @('wait', '--run-id', $RunId, '--timeout', '15s', '--report-outcome') `
        -StdoutPath (Join-Path $ScenarioDirectory "$Action-wait.json") `
        -StderrPath (Join-Path $ScenarioDirectory "$Action-wait.stderr.log")

    $failures = [System.Collections.Generic.List[string]]::new()

    if ($controlExit -ne 0) {
        $failures.Add("$Action --run-id failed with exit $controlExit")
    }

    if ($waitExit -ne 0) {
        $failures.Add("wait --run-id failed with exit $waitExit")
    }

    $events = $null

    try {
        $events = Read-EventStream $EventsPath $ScenarioDirectory
        Assert-CleanTerminal $events $ExpectedCode $ExpectedSource $null
    }
    catch {
        $failures.Add("terminal lifecycle confirmation failed: $($_.Exception.Message)")
    }

    if ($waitExit -eq 0) {
        try {
            $waitReport = Read-JsonFile (Join-Path $ScenarioDirectory "$Action-wait.json")
            Assert-Condition ($waitReport.status -in @('reported', 'unknown')) "Teardown wait status was '$($waitReport.status)'."

            if ($waitReport.status -eq 'reported') {
                Assert-Condition ($waitReport.code -eq $ExpectedCode) "Teardown wait code was $($waitReport.code), expected $ExpectedCode."
                Assert-Condition ($waitReport.source -eq $ExpectedSource) "Teardown wait source was '$($waitReport.source)', expected '$ExpectedSource'."
            }
            else {
                Assert-Condition ($null -eq $waitReport.code -and $null -eq $waitReport.source) 'An unknown teardown wait outcome fabricated a result.'
            }
        }
        catch {
            $failures.Add("wait outcome validation failed: $($_.Exception.Message)")
        }
    }

    if ($failures.Count -gt 0) {
        throw "Detached teardown for '$RunId' was not confirmed: $($failures -join '; ')."
    }

    [pscustomobject]@{
        runId = $RunId
        action = $Action
        code = $ExpectedCode
        source = $ExpectedSource
        remaining = 0
    }
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
    'kill',
    'kill:--run-id',
    'inspect',
    'inspect:--run-id',
    'inspect:--json',
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
$cleanupFailure = $null
$primaryException = $null
$retainedScratch = $null
$detachedRunId = $null
$detachedFinished = $false
$detachedEvents = $null
$detachedDirectory = $null
$detachedIdentityChecks = @()
$identityAssertionFailed = $false

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
    $detachedDirectory = $cancellationDirectory
    $detachedEvents = $cancellationEvents
    $detachedIdentityChecks = @()
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

    $null = Invoke-DetachedTeardown `
        -RunId $detachedRunId `
        -Action cancel `
        -EventsPath $cancellationEvents `
        -ScenarioDirectory $cancellationDirectory `
        -ExpectedCode 108 `
        -ExpectedSource 'control_cancel'
    $cancellationStream = Read-EventStream $cancellationEvents $cancellationDirectory
    $cancelledEvent = @($cancellationStream | Where-Object event -EQ 'cancelled')
    Assert-Condition ($cancelledEvent.Count -eq 1 -and $cancelledEvent[0].source -eq 'control_cancel') 'Cancellation lifecycle evidence was absent or misclassified.'
    $detachedFinished = $true
    $scenarioResults.Add([pscustomobject]@{ name = 'cancellation'; status = 'passed'; exitCode = 108 })

    $cleanupDirectory = New-ScenarioDirectory 'detached-cleanup-failure-path'
    $negativeAttemptDirectory = Join-Path $cleanupDirectory 'negative-attempt'
    [System.IO.Directory]::CreateDirectory($negativeAttemptDirectory) | Out-Null
    $detachedRunId = 'vcs-agent-cleanup-t208-' + [Guid]::NewGuid().ToString('N')
    $detachedEvents = Join-Path $cleanupDirectory 'events.jsonl'
    $detachedDirectory = $cleanupDirectory
    $detachedFinished = $false
    $detachedIdentityChecks = @()
    $cleanupDetachExit = Invoke-Native `
        -FilePath $script:ProcessKitCli `
        -Arguments @(
            'run',
            '--detach',
            '--run-id',
            $detachedRunId,
            '--jsonl',
            $detachedEvents,
            '--',
            $powerShell,
            '-NoProfile',
            '-File',
            $fixture,
            '-Mode',
            'sleep'
        ) `
        -StdoutPath (Join-Path $cleanupDirectory 'detach.stdout.log') `
        -StderrPath (Join-Path $cleanupDirectory 'detach.stderr.log')
    Assert-Condition ($cleanupDetachExit -eq 0) "Detached cleanup fixture failed to start with exit $cleanupDetachExit."
    $cleanupRootIdentity = Read-RunRootIdentity $detachedEvents 'cleanup failure-path fixture'
    $expectedCleanupFailure = $null

    try {
        $null = Invoke-DetachedTeardown `
            -RunId "$detachedRunId-intentionally-missing" `
            -Action kill `
            -EventsPath $detachedEvents `
            -ScenarioDirectory $negativeAttemptDirectory `
            -ExpectedCode 109 `
            -ExpectedSource 'control_kill'
    }
    catch {
        $expectedCleanupFailure = $_.Exception.Message
    }

    Assert-Condition (-not [string]::IsNullOrWhiteSpace($expectedCleanupFailure)) 'The negative cleanup attempt did not fail closed.'
    Assert-Condition ([System.IO.Directory]::Exists($cleanupDirectory)) 'Negative cleanup failure removed its scratch evidence prematurely.'
    Assert-Condition (Test-Path -LiteralPath $detachedEvents -PathType Leaf) 'Negative cleanup failure removed its lifecycle evidence prematurely.'
    Assert-Condition (Test-ProcessIdentityAlive $cleanupRootIdentity) 'Negative cleanup failure did not leave a live subject for the verified recovery path.'
    $detachedIdentityChecks = @(
        [pscustomobject]@{
            Identity = $cleanupRootIdentity
            Description = 'Detached cleanup fixture root'
        }
    )
    $cleanupProof = Invoke-DetachedTeardown `
        -RunId $detachedRunId `
        -Action kill `
        -EventsPath $detachedEvents `
        -ScenarioDirectory $cleanupDirectory `
        -ExpectedCode 109 `
        -ExpectedSource 'control_kill'

    $identityRecoveryStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $identityRecoveryStartInfo.FileName = $powerShell
    $identityRecoveryStartInfo.UseShellExecute = $false
    $identityRecoveryStartInfo.ArgumentList.Add('-NoProfile')
    $identityRecoveryStartInfo.ArgumentList.Add('-File')
    $identityRecoveryStartInfo.ArgumentList.Add($fixture)
    $identityRecoveryStartInfo.ArgumentList.Add('-Mode')
    $identityRecoveryStartInfo.ArgumentList.Add('sleep')
    $identityRecoverySubject = [System.Diagnostics.Process]::Start($identityRecoveryStartInfo)
    Assert-Condition ($null -ne $identityRecoverySubject) 'The identity-recovery regression subject did not start.'

    try {
        $identityRecoverySubjectIdentity = Get-ProcessIdentity -Id $identityRecoverySubject.Id
        Assert-Condition ($null -ne $identityRecoverySubjectIdentity) 'The identity-recovery regression subject did not expose a live exact identity.'
        $detachedIdentityChecks = @(
            [pscustomobject]@{
                Identity = $cleanupRootIdentity
                Description = 'Detached cleanup fixture root'
            },
            [pscustomobject]@{
                Identity = $identityRecoverySubjectIdentity
                Description = 'Synthetic post-lifecycle survivor'
            }
        )
        $expectedIdentityFailure = $null

        try {
            Assert-ProcessIdentitiesGone $detachedIdentityChecks -TimeoutSeconds 1
        }
        catch {
            $expectedIdentityFailure = $_.Exception.Message
        }

        Assert-Condition (-not [string]::IsNullOrWhiteSpace($expectedIdentityFailure)) 'The exact-identity completion gate accepted a post-lifecycle survivor.'
        Assert-Condition (-not $detachedFinished) 'Detached cleanup was marked confirmed before exact identities were gone.'
        Assert-Condition ([System.IO.Directory]::Exists($cleanupDirectory)) 'The post-lifecycle identity failure removed scratch evidence before recovery.'
        Assert-Condition (Test-Path -LiteralPath $detachedEvents -PathType Leaf) 'The post-lifecycle identity failure removed lifecycle evidence before recovery.'
        Invoke-VerifiedIdentityRecovery $detachedIdentityChecks
        Assert-ProcessIdentitiesGone $detachedIdentityChecks
    }
    finally {
        if (-not $identityRecoverySubject.HasExited) {
            $identityRecoverySubject.Kill($true)
            Assert-Condition ($identityRecoverySubject.WaitForExit(10000)) 'The identity-recovery regression subject survived its local safety cleanup.'
        }

        $identityRecoverySubject.Dispose()
    }

    $detachedFinished = $true
    $scenarioResults.Add([pscustomobject]@{
        name = 'detached-cleanup-failure-path'
        status = 'passed'
        exitCode = $cleanupProof.code
        failureObserved = $true
        evidencePreservedUntilRecovery = $true
        identityFailureObservedAfterTerminalLifecycle = $true
        cleanupConfirmedBeforeIdentityRecovery = $false
        identityRecoveryConfirmed = $true
        remaining = $cleanupProof.remaining
    })

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

    $nestedTeardownDirectory = New-ScenarioDirectory 'nested-containment-teardown'
    $nestedTeardownInnerDirectory = Join-Path $nestedTeardownDirectory 'inner'
    [System.IO.Directory]::CreateDirectory($nestedTeardownInnerDirectory) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $nestedTeardownInnerDirectory 'capture')) | Out-Null
    $nestedIdentityPath = Join-Path $nestedTeardownInnerDirectory 'process-identities.json'
    $nestedOuterEvents = Join-Path $nestedTeardownDirectory 'events.jsonl'
    $nestedInnerEvents = Join-Path $nestedTeardownInnerDirectory 'events.jsonl'
    $nestedInnerRunId = 'vcs-agent-nested-inner-t208-' + [Guid]::NewGuid().ToString('N')
    $detachedRunId = 'vcs-agent-nested-outer-t208-' + [Guid]::NewGuid().ToString('N')
    $detachedEvents = $nestedOuterEvents
    $detachedDirectory = $nestedTeardownDirectory
    $detachedFinished = $false
    $detachedIdentityChecks = @()
    $nestedDetachExit = Invoke-Native `
        -FilePath $script:ProcessKitCli `
        -Arguments @(
            'run',
            '--detach',
            '--run-id',
            $detachedRunId,
            '--jsonl',
            $nestedOuterEvents,
            '--grace',
            '2s',
            '--capture-dir',
            (Join-Path $nestedTeardownDirectory 'capture'),
            '--capture-max-bytes',
            '64k',
            '--no-echo',
            '--',
            $script:ProcessKitCli,
            'run',
            '--run-id',
            $nestedInnerRunId,
            '--jsonl',
            $nestedInnerEvents,
            '--grace',
            '1s',
            '--capture-dir',
            (Join-Path $nestedTeardownInnerDirectory 'capture'),
            '--capture-max-bytes',
            '64k',
            '--no-echo',
            '--',
            $powerShell,
            '-NoProfile',
            '-File',
            $fixture,
            '-Mode',
            'descendant',
            '-IdentityPath',
            $nestedIdentityPath
        ) `
        -StdoutPath (Join-Path $nestedTeardownDirectory 'detach.stdout.log') `
        -StderrPath (Join-Path $nestedTeardownDirectory 'detach.stderr.log')
    Assert-Condition ($nestedDetachExit -eq 0) "Detached nested containment fixture failed to start with exit $nestedDetachExit."

    $innerRunnerIdentity = Read-RunRootIdentity $nestedOuterEvents 'outer container root (the inner runner)'
    $nestedIdentity = Read-FixtureIdentity $nestedIdentityPath
    $detachedIdentityChecks = @(
        [pscustomobject]@{
            Identity = $innerRunnerIdentity
            Description = 'Nested inner runner'
        },
        [pscustomobject]@{
            Identity = $nestedIdentity.fixture
            Description = 'Nested fixture root'
        },
        [pscustomobject]@{
            Identity = $nestedIdentity.descendant
            Description = 'Nested long-lived descendant'
        }
    )
    Assert-Condition (Test-ProcessIdentityAlive $innerRunnerIdentity) 'The inner runner was not alive before outer cancellation.'
    Assert-Condition (Test-ProcessIdentityAlive $nestedIdentity.fixture) 'The inner fixture was not alive before outer cancellation.'
    Assert-Condition (Test-ProcessIdentityAlive $nestedIdentity.descendant) 'The long-lived descendant was not alive before outer cancellation.'

    $outerInspectExit = Invoke-Native `
        -FilePath $script:ProcessKitCli `
        -Arguments @('inspect', '--run-id', $detachedRunId, '--json') `
        -StdoutPath (Join-Path $nestedTeardownDirectory 'outer-inspect.json') `
        -StderrPath (Join-Path $nestedTeardownDirectory 'outer-inspect.stderr.log')
    Assert-Condition ($outerInspectExit -eq 0) "Inspecting the live outer container failed with exit $outerInspectExit."
    $outerSnapshot = Read-JsonFile (Join-Path $nestedTeardownDirectory 'outer-inspect.json')
    Assert-Condition ($outerSnapshot.run_id -eq $detachedRunId) 'Outer inspect returned the wrong run identity.'
    Assert-Condition ($outerSnapshot.root_pid -eq $innerRunnerIdentity.pid) 'Outer inspect did not identify the live inner runner as its root.'
    $outerRootMember = @($outerSnapshot.members | Where-Object pid -EQ $innerRunnerIdentity.pid)
    Assert-Condition ($outerRootMember.Count -eq 1) 'Outer containment did not report the inner runner as a member.'
    Assert-Condition (-not [string]::IsNullOrWhiteSpace([string] $outerRootMember[0].start_time)) 'Outer containment did not publish a start-time token for the inner runner.'

    $innerInspectExit = Invoke-Native `
        -FilePath $script:ProcessKitCli `
        -Arguments @('inspect', '--run-id', $nestedInnerRunId, '--json') `
        -StdoutPath (Join-Path $nestedTeardownDirectory 'inner-inspect.json') `
        -StderrPath (Join-Path $nestedTeardownDirectory 'inner-inspect.stderr.log')
    Assert-Condition ($innerInspectExit -eq 0) "Inspecting the live inner container failed with exit $innerInspectExit."
    $innerSnapshot = Read-JsonFile (Join-Path $nestedTeardownDirectory 'inner-inspect.json')
    Assert-Condition ($innerSnapshot.run_id -eq $nestedInnerRunId) 'Inner inspect returned the wrong run identity.'
    Assert-Condition ($innerSnapshot.root_pid -eq $nestedIdentity.fixture.pid) 'Inner inspect did not identify the fixture root.'
    $fixtureMember = @($innerSnapshot.members | Where-Object pid -EQ $nestedIdentity.fixture.pid)
    $descendantMember = @($innerSnapshot.members | Where-Object pid -EQ $nestedIdentity.descendant.pid)
    Assert-Condition ($fixtureMember.Count -eq 1) 'Inner containment did not report the fixture root as a member.'
    Assert-Condition ($descendantMember.Count -eq 1) 'Inner containment did not report the long-lived descendant as a member.'
    Assert-Condition (-not [string]::IsNullOrWhiteSpace([string] $fixtureMember[0].start_time)) 'Inner containment did not publish a start-time token for the fixture root.'
    Assert-Condition (-not [string]::IsNullOrWhiteSpace([string] $descendantMember[0].start_time)) 'Inner containment did not publish a start-time token for the descendant.'

    $nestedCleanupProof = Invoke-DetachedTeardown `
        -RunId $detachedRunId `
        -Action cancel `
        -EventsPath $nestedOuterEvents `
        -ScenarioDirectory $nestedTeardownDirectory `
        -ExpectedCode 108 `
        -ExpectedSource 'control_cancel'

    try {
        Assert-ProcessIdentitiesGone $detachedIdentityChecks
    }
    catch {
        $identityAssertionFailed = $true
        throw
    }

    $nestedOuterStream = Read-EventStream $nestedOuterEvents $nestedTeardownDirectory
    $nestedInnerStream = Read-ValidatedEventStream $nestedInnerEvents $nestedTeardownInnerDirectory
    $nestedInnerStarted = @($nestedInnerStream | Where-Object event -EQ 'run_started')
    Assert-Condition ($nestedInnerStarted.Count -eq 1) 'The inner nested lifecycle did not report exactly one run_started event.'
    Assert-Condition ($nestedInnerStarted[0].run_id -eq $nestedInnerRunId) 'The inner nested lifecycle reported the wrong run identity.'
    $nestedInnerTerminals = @($nestedInnerStream | Where-Object event -EQ 'runner_exit')
    Assert-Condition ($nestedInnerTerminals.Count -le 1) 'The inner nested lifecycle reported more than one runner_exit event.'

    if ($nestedInnerTerminals.Count -eq 1) {
        Assert-Condition ($nestedInnerStream[-1].event -eq 'runner_exit') 'The inner nested runner_exit was not terminal.'
        Assert-CleanTerminal $nestedInnerStream 107 'cancelled' $null
        $nestedInnerCancelled = @($nestedInnerStream | Where-Object event -EQ 'cancelled')
        Assert-Condition ($nestedInnerCancelled.Count -eq 1) 'The inner nested lifecycle did not report exactly one local cancellation.'
        Assert-Condition ($nestedInnerCancelled[0].source -in @('sigterm', 'sighup', 'ctrl_break', 'ctrl_close', 'ctrl_logoff', 'ctrl_shutdown')) "The inner nested lifecycle cancellation source was '$($nestedInnerCancelled[0].source)'."
        $nestedInnerCleanup = @($nestedInnerStream | Where-Object event -EQ 'cleanup_finished') | Select-Object -Last 1
    }
    else {
        $nestedInnerCancelled = @()
        $nestedInnerCleanup = $null
    }

    $detachedFinished = $true
    $nestedOuterStarted = @($nestedOuterStream | Where-Object event -EQ 'run_started')[0]
    $scenarioResults.Add([pscustomobject]@{
        name = 'nested-containment-teardown'
        status = 'passed'
        exitCode = $nestedCleanupProof.code
        outerMechanism = $nestedOuterStarted.mechanism
        innerMechanism = $innerSnapshot.mechanism
        innerRunnerIdentityGone = $true
        fixtureIdentityGone = $true
        descendantIdentityGone = $true
        remaining = $nestedCleanupProof.remaining
        outerLifecycle = [ordered]@{
            validated = $true
            code = $nestedCleanupProof.code
            source = $nestedCleanupProof.source
            remaining = $nestedCleanupProof.remaining
        }
        innerLifecycle = [ordered]@{
            validated = $true
            terminalGuaranteed = $false
            terminalObserved = $nestedInnerTerminals.Count -eq 1
            code = if ($nestedInnerTerminals.Count -eq 1) { $nestedInnerTerminals[0].code } else { $null }
            source = if ($nestedInnerTerminals.Count -eq 1) { $nestedInnerTerminals[0].source } else { $null }
            cancelSource = if ($nestedInnerCancelled.Count -eq 1) { $nestedInnerCancelled[0].source } else { $null }
            remaining = if ($null -ne $nestedInnerCleanup) { $nestedInnerCleanup.remaining } else { $null }
            cleanupProof = if ($nestedInnerTerminals.Count -eq 1) { 'inner-terminal' } else { 'outer-clean-terminal-and-exact-identities-gone' }
        }
    })

    $proofStatus = 'passed'
}
catch {
    $failure = $_.Exception.Message
    $primaryException = $_.Exception
}
finally {
    if ($null -ne $detachedRunId -and -not $detachedFinished) {
        try {
            $terminalAlreadyClean = $false

            try {
                $existingEvents = Read-EventStream $detachedEvents $detachedDirectory
                Assert-CleanTerminalState $existingEvents
                $terminalAlreadyClean = $true
            }
            catch {
                # A live or partial stream is expected on the recovery path; kill/wait must finish it below.
            }

            if (-not $terminalAlreadyClean) {
                $null = Invoke-DetachedTeardown `
                    -RunId $detachedRunId `
                    -Action kill `
                    -EventsPath $detachedEvents `
                    -ScenarioDirectory $detachedDirectory `
                    -ExpectedCode 109 `
                    -ExpectedSource 'control_kill'
            }

            Invoke-VerifiedIdentityRecovery $detachedIdentityChecks
            $detachedFinished = $true
        }
        catch {
            $cleanupFailure = $_.Exception.Message
            $proofStatus = 'failed'
            $retainedScratch = $script:Scratch
        }
    }

    if ($null -ne $primaryException -and $null -eq $retainedScratch) {
        $retainedScratch = $script:Scratch
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
        cleanup = [ordered]@{
            confirmed = $detachedFinished -or $null -eq $detachedRunId
            failure = $cleanupFailure
            identityAssertionFailed = $identityAssertionFailed
            retainedScratch = $retainedScratch
        }
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

    if ($null -eq $primaryException -and $null -eq $cleanupFailure -and [System.IO.Directory]::Exists($resolvedScratch)) {
        [System.IO.Directory]::Delete($resolvedScratch, $true)
    }
}

if ($null -ne $primaryException -and $null -ne $cleanupFailure) {
    throw [System.AggregateException]::new(
        "The supervised proof failed and detached cleanup was not confirmed; evidence remains at '$retainedScratch'.",
        [System.Exception[]] @($primaryException, [System.InvalidOperationException]::new($cleanupFailure))
    )
}

if ($null -ne $primaryException) {
    throw $primaryException
}

if ($null -ne $cleanupFailure) {
    throw "Detached cleanup was not confirmed; evidence remains at '$retainedScratch': $cleanupFailure"
}

Write-Host "ProcessKit-CLI $($preflightReport.version) supervised proof passed: $($scenarioResults.Count) scenarios."
