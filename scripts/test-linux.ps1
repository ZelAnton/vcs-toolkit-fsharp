#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the test suite inside a Linux container.

.DESCRIPTION
    Cross-platform wrapper around `docker run` that builds and tests the
    solution against a Linux .NET SDK image. Intended for developers on
    Windows using Rancher Desktop (or Docker Desktop) to exercise the
    Linux/Unix code path without leaving their host.

    The host's bin/obj folders (populated by the Windows IDE build) are
    shadowed inside the container with anonymous volumes — Linux build
    artifacts live in those throwaway volumes and never touch the host
    working copy. A named volume caches NuGet packages between runs.

    This script is an optional convenience helper. Delete it (and
    docs/linux-testing.md) if your project does not need Linux testing.

.PARAMETER Image
    Container image. Defaults to mcr.microsoft.com/dotnet/sdk:10.0.

.PARAMETER Configuration
    MSBuild configuration. Debug or Release. Defaults to Release.

.PARAMETER Filter
    Optional `dotnet test --filter` expression
    (e.g. "FullyQualifiedName~greet").

.PARAMETER Rebuild
    Run `dotnet clean` before the tests.

.EXAMPLE
    pwsh ./scripts/test-linux.ps1

.EXAMPLE
    pwsh ./scripts/test-linux.ps1 -Filter "FullyQualifiedName~greet"
#>
[CmdletBinding()]
param(
    [string]$Image = 'mcr.microsoft.com/dotnet/sdk:10.0',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Filter,
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'

# Normalise to forward slashes so docker on Windows handles paths with mixed
# separators uniformly. The bind-mount source still needs to be quoted in case
# the user clones the repo into a path containing spaces.
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path -replace '\\', '/'
$NugetVolume = 'VcsToolkit-nuget'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host "docker CLI not found on PATH." -ForegroundColor Red
    Write-Host "Start Rancher Desktop (with the dockerd/moby engine) or install Docker Desktop, then re-open the shell." -ForegroundColor Yellow
    exit 1
}

& docker version --format '{{.Server.Version}}' *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Cannot reach the Docker daemon. Is Rancher Desktop running?" -ForegroundColor Red
    exit 1
}

$bashLines = @('set -e')
if ($Rebuild) {
    $bashLines += "dotnet clean -c $Configuration"
}
$bashLines += "dotnet build -c $Configuration"
$testCmd = "dotnet test --no-build -c $Configuration VcsToolkit.slnx"
if ($Filter) {
    $testCmd += " --filter `"$Filter`""
}
$bashLines += $testCmd
$bashScript = $bashLines -join "`n"

# Anonymous volumes shadow the host bin/obj folders inside the container so
# Windows IDE artifacts cannot leak into the Linux build, and the Linux build
# does not write back into the host tree. Each library DLL still lands at the
# standard `src/<Project>/bin/...` path its dependents expect via
# AssemblySearchPaths — just inside the anonymous volume. One pair per project.
$shadowedPaths = @(
    '/src/src/VcsToolkit.CliSupport/bin',
    '/src/src/VcsToolkit.CliSupport/obj',
    '/src/src/VcsToolkit.Diff/bin',
    '/src/src/VcsToolkit.Diff/obj',
    '/src/src/VcsToolkit.Git/bin',
    '/src/src/VcsToolkit.Git/obj',
    '/src/src/VcsToolkit.Jj/bin',
    '/src/src/VcsToolkit.Jj/obj',
    '/src/src/VcsToolkit.GitHub/bin',
    '/src/src/VcsToolkit.GitHub/obj',
    '/src/src/VcsToolkit.GitLab/bin',
    '/src/src/VcsToolkit.GitLab/obj',
    '/src/src/VcsToolkit.Gitea/bin',
    '/src/src/VcsToolkit.Gitea/obj',
    '/src/src/VcsToolkit.Core/bin',
    '/src/src/VcsToolkit.Core/obj',
    '/src/tests/VcsToolkit.CliSupport.Tests/bin',
    '/src/tests/VcsToolkit.CliSupport.Tests/obj',
    '/src/tests/VcsToolkit.Diff.Tests/bin',
    '/src/tests/VcsToolkit.Diff.Tests/obj',
    '/src/tests/VcsToolkit.Git.Tests/bin',
    '/src/tests/VcsToolkit.Git.Tests/obj',
    '/src/tests/VcsToolkit.Jj.Tests/bin',
    '/src/tests/VcsToolkit.Jj.Tests/obj',
    '/src/tests/VcsToolkit.GitHub.Tests/bin',
    '/src/tests/VcsToolkit.GitHub.Tests/obj',
    '/src/tests/VcsToolkit.GitLab.Tests/bin',
    '/src/tests/VcsToolkit.GitLab.Tests/obj',
    '/src/tests/VcsToolkit.Gitea.Tests/bin',
    '/src/tests/VcsToolkit.Gitea.Tests/obj',
    '/src/tests/VcsToolkit.Core.Tests/bin',
    '/src/tests/VcsToolkit.Core.Tests/obj'
)

$dockerArgs = @(
    'run', '--rm',
    '-v', "${RepoRoot}:/src",
    '-v', "${NugetVolume}:/root/.nuget/packages"
)
foreach ($p in $shadowedPaths) {
    $dockerArgs += @('-v', $p)
}
$dockerArgs += @(
    '-w', '/src',
    '-e', 'DOTNET_CLI_TELEMETRY_OPTOUT=1',
    '-e', 'DOTNET_NOLOGO=1',
    $Image,
    'bash', '-c', $bashScript
)

Write-Host "==> Running tests in $Image" -ForegroundColor DarkGray
Write-Host "    Repo:          $RepoRoot -> /src" -ForegroundColor DarkGray
Write-Host "    Configuration: $Configuration" -ForegroundColor DarkGray
if ($Filter)  { Write-Host "    Filter:        $Filter" -ForegroundColor DarkGray }
if ($Rebuild) { Write-Host "    Rebuild:       yes" -ForegroundColor DarkGray }

& docker @dockerArgs
exit $LASTEXITCODE
