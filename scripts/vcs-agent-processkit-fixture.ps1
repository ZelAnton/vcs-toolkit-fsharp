[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('sleep', 'idle')]
    [string] $Mode
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Mode -eq 'idle') {
    [Console]::Out.WriteLine('fixture-ready')
    [Console]::Out.Flush()
}

Start-Sleep -Seconds 30
