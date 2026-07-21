#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Run the Ionide F# static analyzers across every library under src/ and fail on any
    Warning- or Error-severity finding.

.DESCRIPTION
    The F# counterpart of `dotnet fantomas --check src tests`: static analysis that catches
    problem categories invisible to the compiler (even with warnings-as-errors) and to the
    formatter. It drives the pinned `fsharp-analyzers` tool (.config/dotnet-tools.json) with
    the Ionide.Analyzers rule set (Directory.Packages.props / src/Directory.Build.props) over
    each project under src/.

    The tool always exits 0 for non-Error findings, so this script owns the gate: it fails
    the run (exit 1) on any finding printed at Warning or Error severity, on any analyzer
    load / type-check failure the tool reports, or on a non-zero tool exit. Info- and
    Hint-severity findings are advisory — printed for visibility, but they do not fail the
    run (they include opinionated, sometimes semantics-changing nudges such as "add
    [<Struct>] to this DU" that are a design choice, not a defect).

    The analyzers type-check each project via FSharp.Compiler.Service, which needs the
    sibling assemblies (referenced via Reference + AssemblySearchPaths) already built, so
    the script builds the solution first. In CI this doubles as the analyzer job's build.

.PARAMETER Configuration
    Build configuration used to produce the assemblies the analyzers type-check against.
    Defaults to Release (matches CI).
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    Write-Host '==> Restoring local tools (fsharp-analyzers)'
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed (exit $LASTEXITCODE)" }

    Write-Host "==> Building the solution ($Configuration) so analyzer type-checking can resolve references"
    dotnet build VcsToolkit.slnx --configuration $Configuration -m:1 --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }

    # Resolve the restored Ionide.Analyzers package folder machine- and version-independently,
    # via the MSBuild path property GeneratePathProperty exposes (src/Directory.Build.props).
    $probe = Join-Path $repoRoot 'src' 'VcsToolkit.CliSupport' 'VcsToolkit.CliSupport.fsproj'
    $pkgRoot = (dotnet build $probe --configuration $Configuration --getProperty:PkgIonide_Analyzers | Select-Object -Last 1).Trim()
    if ([string]::IsNullOrWhiteSpace($pkgRoot)) { throw 'Could not resolve $(PkgIonide_Analyzers) — is Ionide.Analyzers referenced/restored?' }
    $analyzersPath = Join-Path $pkgRoot 'analyzers' 'dotnet' 'fs'
    if (-not (Test-Path -LiteralPath $analyzersPath)) { throw "Analyzers path not found: $analyzersPath" }

    $projects = Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -Filter '*.fsproj' | Sort-Object FullName
    if ($projects.Count -eq 0) { throw 'No src/**/*.fsproj projects found to analyze.' }
    $projectArgs = @()
    foreach ($p in $projects) { $projectArgs += @('--project', $p.FullName) }

    # Resolve the pinned analyzer tool assembly and run it on the NEWEST installed runtime.
    # WHY --roll-forward LatestMajor: `fsharp-analyzers` targets net8.0 and runs an IN-PROCESS
    # MSBuild design-time build (Ionide.ProjInfo.WorkspaceLoader) using the installed .NET SDK.
    # On a host where an older runtime (net8/net9) is present ALONGSIDE the newer SDK — e.g. the
    # GitHub ubuntu-latest image, which preinstalls net8/net9 runtimes next to the net10 SDK
    # that setup-dotnet adds — the tool otherwise runs on the older runtime (its
    # .config/dotnet-tools.json `rollForward: true` == Major stays on net8 while net8 is
    # present), then fails to load the net10 SDK's MSBuild assemblies with
    # `System.IO.FileNotFoundException: System.Runtime, Version=10.0.0.0` (tool exit 22). A dev
    # box with only the net10 runtime never hits this (the tool rolls forward to net10), which
    # is why this job passed locally on Windows but was red on Linux CI. Setting
    # DOTNET_ROLL_FORWARD in the environment does NOT fix it — the tool-manifest policy wins
    # over the env var — so we invoke the tool ASSEMBLY via `dotnet exec --roll-forward
    # LatestMajor`, whose host option forces the newest installed runtime (matching the SDK),
    # where the in-process MSBuild resolves. See .work KB K-064/K-065.
    $manifestPath = Join-Path $repoRoot '.config' 'dotnet-tools.json'
    $analyzerVersion = ((Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json).tools.'fsharp-analyzers').version
    if ([string]::IsNullOrWhiteSpace($analyzerVersion)) { throw "Could not read the fsharp-analyzers version from $manifestPath." }
    $globalPackages = $null
    foreach ($line in (dotnet nuget locals global-packages --list)) {
        if ($line -match 'global-packages:\s*(.+?)\s*$') { $globalPackages = $Matches[1]; break }
    }
    if ([string]::IsNullOrWhiteSpace($globalPackages)) { throw 'Could not resolve the NuGet global-packages folder.' }
    $cliDll = Get-ChildItem -Path (Join-Path $globalPackages 'fsharp-analyzers' $analyzerVersion 'tools') `
        -Recurse -Filter 'FSharp.Analyzers.Cli.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $cliDll) { throw "Could not locate FSharp.Analyzers.Cli.dll for fsharp-analyzers $analyzerVersion under $globalPackages (did 'dotnet tool restore' run?)." }

    Write-Host "==> Running fsharp-analyzers over $($projects.Count) src projects"
    $output = & dotnet exec --roll-forward LatestMajor $cliDll.FullName `
        --analyzers-path $analyzersPath `
        @projectArgs `
        --code-root $repoRoot 2>&1
    $toolExit = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }

    # The tool exits 0 even with Warning findings, so decide failure here:
    #   * a Warning/Error-severity finding line (path(line,col): Warning|Error CODE : msg)
    #   * a tool-level error line (e.g. an analyzer assembly was skipped -> nothing analyzed)
    #   * a non-zero tool exit (a project failed to type-check)
    $lines = @($output | ForEach-Object { $_.ToString() })
    $findings = @($lines | Where-Object { $_ -match '\):\s+(Warning|Error)\s' })
    $toolErrors = @($lines | Where-Object { $_ -match '\[FSharp\.Analyzers\.Cli\]\s+error:' })

    if ($findings.Count -gt 0 -or $toolErrors.Count -gt 0 -or $toolExit -ne 0) {
        Write-Host ''
        if ($findings.Count -gt 0) { Write-Host "FAILED: $($findings.Count) analyzer finding(s) at Warning/Error severity." }
        if ($toolErrors.Count -gt 0) { Write-Host "FAILED: the analyzer reported $($toolErrors.Count) tool-level error(s)." }
        if ($toolExit -ne 0) { Write-Host "FAILED: fsharp-analyzers exited with code $toolExit." }
        exit 1
    }

    Write-Host ''
    Write-Host 'OK: no Warning/Error analyzer findings across src/.'
    exit 0
}
finally {
    Pop-Location
}
