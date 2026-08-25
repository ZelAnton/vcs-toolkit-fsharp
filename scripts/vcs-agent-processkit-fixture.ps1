[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('sleep', 'idle', 'descendant')]
    [string] $Mode,

    [string] $IdentityPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-ProcessIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process] $Process
    )

    $startTimeUtcTicks = $Process.StartTime.ToUniversalTime().Ticks
    $startIdentityToken = "utc-ticks:$startTimeUtcTicks"

    if ($IsLinux) {
        $stat = Get-Content -Raw -LiteralPath "/proc/$($Process.Id)/stat" -Encoding UTF8
        $commandEnd = $stat.LastIndexOf(')')

        if ($commandEnd -le 0) {
            throw "Linux process stat for PID $($Process.Id) had no command terminator."
        }

        $fields = @($stat.Substring($commandEnd + 1).Trim() -split '\s+')

        if ($fields.Count -le 19) {
            throw "Linux process stat for PID $($Process.Id) omitted its start-time field."
        }

        $startIdentityToken = "linux-proc:$($fields[19])"
    }

    [ordered]@{
        pid = $Process.Id
        startTimeUtcTicks = $startTimeUtcTicks
        startIdentityToken = $startIdentityToken
    }
}

if ($Mode -eq 'idle') {
    [Console]::Out.WriteLine('fixture-ready')
    [Console]::Out.Flush()
}

if ($Mode -eq 'descendant') {
    if ([string]::IsNullOrWhiteSpace($IdentityPath)) {
        throw '-IdentityPath is required for descendant mode.'
    }

    $powerShell = (Get-Process -Id $PID).Path
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $powerShell
    $startInfo.UseShellExecute = $false
    $startInfo.ArgumentList.Add('-NoProfile')
    $startInfo.ArgumentList.Add('-Command')
    $startInfo.ArgumentList.Add('Start-Sleep -Seconds 60')
    $descendant = [System.Diagnostics.Process]::Start($startInfo)

    try {
        $fixtureProcess = Get-Process -Id $PID

        $identity = [ordered]@{
            fixture = Get-ProcessIdentity $fixtureProcess
            descendant = Get-ProcessIdentity $descendant
        }
        $resolvedIdentityPath = [System.IO.Path]::GetFullPath($IdentityPath)
        $identityDirectory = [System.IO.Path]::GetDirectoryName($resolvedIdentityPath)
        [System.IO.Directory]::CreateDirectory($identityDirectory) | Out-Null
        $temporaryIdentityPath = "$resolvedIdentityPath.$PID.tmp"
        $identity | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporaryIdentityPath -Encoding utf8NoBOM
        Move-Item -LiteralPath $temporaryIdentityPath -Destination $resolvedIdentityPath
        [Console]::Out.WriteLine('descendant-ready')
        [Console]::Out.Flush()
    }
    finally {
        $descendant.Dispose()
    }
}

Start-Sleep -Seconds 60
