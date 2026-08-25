[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('sleep', 'idle', 'descendant')]
    [string] $Mode,

    [string] $IdentityPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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
        $identity = [ordered]@{
            fixture = [ordered]@{
                pid = $PID
                startTimeUtcTicks = (Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks
            }
            descendant = [ordered]@{
                pid = $descendant.Id
                startTimeUtcTicks = $descendant.StartTime.ToUniversalTime().Ticks
            }
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
