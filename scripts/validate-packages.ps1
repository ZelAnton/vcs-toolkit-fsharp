#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validates .nupkg artifacts produced by `dotnet pack` against this repo's
    packaging invariants.

.DESCRIPTION
    `dotnet pack` is exercised only by the release workflow today, so a
    regression in the packaging mechanics (the post-pack sibling-dependency
    rewrite in Directory.Build.targets, the README/icon/XML-doc inclusion, the
    `vcs-mcp` DotnetTool layout) would otherwise surface only at release time —
    the most expensive place for a surprise. This script re-checks those
    invariants against a fresh `dotnet pack` output so CI can catch the
    regression on the PR that introduces it.

    For every `*.nupkg` under -PackagesDir (symbol packages are skipped):

      - Extracts the package and parses its .nuspec.
      - Every packable `VcsToolkit.*` library must declare exactly the
        `VcsToolkit.*` sibling <dependency> entries its src/*.fsproj
        `<Reference Include="VcsToolkit.*" />` set implies (see
        $expectedSiblings below, and the injection this mirrors in
        Directory.Build.targets) — no more, no fewer.
      - Every package must carry README.md and icon.png at its root, plus an
        XML documentation file under lib/ or tools/ (GenerateDocumentationFile).
      - `vcs-mcp` must be a self-contained DotnetTool: its .nuspec must declare
        <packageType name="DotnetTool" />, it must ship a
        tools/<tfm>/.../DotnetToolSettings.xml, and — being self-contained
        (siblings bundled, not restored) — it must NOT declare any
        `VcsToolkit.*` NuGet <dependency>.
      - Every package this script expects (the keys of $expectedSiblings, plus
        vcs-mcp) must actually have been produced — catches a project silently
        dropping out of the pack.

    Exits 1 (with every issue listed) on any violation, 0 when every packed
    artifact checks out.

.PARAMETER PackagesDir
    Directory containing the packed `*.nupkg` files. Defaults to ./artifacts
    (relative to the repo root), matching the --output used by
    .github/workflows/release.yml.

.EXAMPLE
    dotnet pack VcsToolkit.slnx --configuration Release --output ./artifacts
    pwsh ./scripts/validate-packages.ps1

.EXAMPLE
    pwsh ./scripts/validate-packages.ps1 -PackagesDir ./some/other/dir
#>
[CmdletBinding()]
param(
    [string]$PackagesDir = (Join-Path $PSScriptRoot '../artifacts')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Expected VcsToolkit.* sibling NuGet dependencies per packable library,
# mirroring each src/*/*.fsproj's `<Reference Include="VcsToolkit.*" />` set
# (the same set Directory.Build.targets' _InjectSiblingPackageDependencies
# target derives at pack time). Keep in sync when a project's sibling
# references change. `vcs-mcp` (the DotnetTool) is intentionally NOT a key
# here — it bundles its siblings instead of depending on them; see below.
$expectedSiblings = [ordered]@{
    'VcsToolkit.CliSupport' = @()
    'VcsToolkit.Diff'       = @()
    'VcsToolkit.TestKit'    = @()
    'VcsToolkit.Git'        = @('VcsToolkit.CliSupport', 'VcsToolkit.Diff')
    'VcsToolkit.GitHub'     = @('VcsToolkit.CliSupport', 'VcsToolkit.Diff')
    'VcsToolkit.GitLab'     = @('VcsToolkit.CliSupport', 'VcsToolkit.Diff')
    'VcsToolkit.Gitea'      = @('VcsToolkit.CliSupport', 'VcsToolkit.Diff')
    'VcsToolkit.Jj'         = @('VcsToolkit.CliSupport', 'VcsToolkit.Diff')
    'VcsToolkit.Core'       = @('VcsToolkit.CliSupport', 'VcsToolkit.Diff', 'VcsToolkit.Git', 'VcsToolkit.Jj')
    'VcsToolkit.Forge'      = @('VcsToolkit.CliSupport', 'VcsToolkit.Diff', 'VcsToolkit.GitHub', 'VcsToolkit.GitLab', 'VcsToolkit.Gitea')
    'VcsToolkit.Watch'      = @('VcsToolkit.Core', 'VcsToolkit.CliSupport', 'VcsToolkit.Diff', 'VcsToolkit.Git', 'VcsToolkit.Jj')
    'VcsToolkit.Mcp'        = @('VcsToolkit.Core', 'VcsToolkit.Forge', 'VcsToolkit.CliSupport', 'VcsToolkit.Diff', 'VcsToolkit.Git', 'VcsToolkit.Jj', 'VcsToolkit.GitHub', 'VcsToolkit.GitLab', 'VcsToolkit.Gitea')
}
$toolPackageId = 'vcs-mcp'

function Get-XPathAttr {
    param([System.Xml.XmlElement]$Node, [string]$AttrName)
    # Explicit GetAttribute avoids the ambiguity between PowerShell's ETS
    # attribute-as-property adaptation and real XmlElement/XmlNode CLR
    # properties that happen to share a name (e.g. an attribute named "name"
    # vs. XmlNode.Name, the element's own tag name).
    return $Node.GetAttribute($AttrName)
}

if (-not (Test-Path $PackagesDir)) {
    Write-Host "Packages directory not found: $PackagesDir" -ForegroundColor Red
    Write-Host "Run 'dotnet pack VcsToolkit.slnx --configuration Release --output `"$PackagesDir`"' first." -ForegroundColor Yellow
    exit 1
}

$nupkgs = Get-ChildItem -Path $PackagesDir -Filter '*.nupkg' -File
if ($nupkgs.Count -eq 0) {
    Write-Host "No .nupkg files found in $PackagesDir" -ForegroundColor Red
    exit 1
}

Write-Host "==> Validating $($nupkgs.Count) package(s) in $PackagesDir" -ForegroundColor Cyan

$failures = [System.Collections.Generic.List[string]]::new()
$seenIds = @{}

foreach ($nupkg in $nupkgs) {
    $extractDir = Join-Path ([System.IO.Path]::GetTempPath()) ("validate-packages-" + [Guid]::NewGuid())
    New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($nupkg.FullName, $extractDir)

        $nuspecFile = Get-ChildItem -Path $extractDir -Filter '*.nuspec' -File | Select-Object -First 1
        if (-not $nuspecFile) {
            $failures.Add("$($nupkg.Name): no .nuspec found inside the package")
            continue
        }

        [xml]$nuspec = Get-Content -Raw $nuspecFile.FullName
        $idNode = $nuspec.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="id"]')
        if (-not $idNode) {
            $failures.Add("$($nupkg.Name): .nuspec has no <id> under <metadata>")
            continue
        }
        $id = $idNode.InnerText.Trim()
        $seenIds[$id] = $true
        Write-Host "  -- $id ($($nupkg.Name))" -ForegroundColor DarkGray

        # --- Sibling VcsToolkit.* dependency check --------------------------
        $depNodes = @($nuspec.SelectNodes('//*[local-name()="dependency"]'))
        $actualDeps = @($depNodes `
            | ForEach-Object { Get-XPathAttr $_ 'id' } `
            | Where-Object { $_ -like 'VcsToolkit.*' } `
            | Sort-Object -Unique)

        if ($id -eq $toolPackageId) {
            if ($actualDeps.Count -gt 0) {
                $failures.Add("${id}: DotnetTool package must bundle its VcsToolkit.* siblings, not declare NuGet dependencies on them, but found: $($actualDeps -join ', ')")
            }
        } elseif ($expectedSiblings.Contains($id)) {
            $expected = @($expectedSiblings[$id] | Sort-Object -Unique)
            $missing = @($expected | Where-Object { $actualDeps -notcontains $_ })
            $unexpected = @($actualDeps | Where-Object { $expected -notcontains $_ })
            if ($missing.Count -gt 0) {
                $failures.Add("${id}: missing expected sibling dependency/-ies: $($missing -join ', ') (found: $($actualDeps -join ', '))")
            }
            if ($unexpected.Count -gt 0) {
                $failures.Add("${id}: unexpected sibling dependency/-ies not in `$expectedSiblings: $($unexpected -join ', ')")
            }
        } else {
            $failures.Add("${id}: no entry in `$expectedSiblings (nor is it '$toolPackageId') — update scripts/validate-packages.ps1 when adding a new packable project")
        }

        # --- README / icon / XML-doc check -----------------------------------
        $allFiles = @(Get-ChildItem -Path $extractDir -Recurse -File `
            | ForEach-Object { $_.FullName.Substring($extractDir.Length + 1) -replace '\\', '/' })

        if (-not ($allFiles | Where-Object { $_ -ieq 'README.md' })) {
            $failures.Add("${id}: package is missing README.md at its root")
        }
        if (-not ($allFiles | Where-Object { $_ -ieq 'icon.png' })) {
            $failures.Add("${id}: package is missing icon.png at its root")
        }
        # Excludes DotnetToolSettings.xml (a tool-manifest metadata file dotnet
        # pack always emits under tools/<tfm>/... for DotnetTool packages, not
        # the compiler-generated XML API doc this check is looking for) so a
        # DotnetTool package can't satisfy this purely by virtue of being a
        # tool — it still needs its own assembly's XML documentation file.
        $hasXmlDoc = $allFiles | Where-Object {
            $_ -match '\.xml$' -and
            ($_ -like 'lib/*' -or $_ -like 'tools/*') -and
            [System.IO.Path]::GetFileName($_) -ne 'DotnetToolSettings.xml'
        }
        if (-not $hasXmlDoc) {
            $failures.Add("${id}: package is missing an XML documentation file under lib/ or tools/ (GenerateDocumentationFile)")
        }

        # --- vcs-mcp DotnetTool validity ---------------------------------------
        if ($id -eq $toolPackageId) {
            $packageTypeNodes = @($nuspec.SelectNodes('//*[local-name()="packageType"]'))
            $hasDotnetTool = @($packageTypeNodes | Where-Object { (Get-XPathAttr $_ 'name') -eq 'DotnetTool' })
            if ($hasDotnetTool.Count -eq 0) {
                $failures.Add("${id}: not a valid DotnetTool package (.nuspec is missing <packageTypes><packageType name=`"DotnetTool`" /></packageTypes>)")
            }
            $hasToolSettings = $allFiles | Where-Object { $_ -match '^tools/[^/]+/.*/DotnetToolSettings\.xml$' }
            if (-not $hasToolSettings) {
                $failures.Add("${id}: missing tools/<tfm>/.../DotnetToolSettings.xml — not a valid .NET tool layout")
            }
        }
    } finally {
        Remove-Item -Path $extractDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Every project this script expects a package for must actually have produced
# one — catches a pack step silently skipping a project.
foreach ($expectedId in $expectedSiblings.Keys) {
    if (-not $seenIds.ContainsKey($expectedId)) {
        $failures.Add("${expectedId}: expected package was not found among the packed artifacts")
    }
}
if (-not $seenIds.ContainsKey($toolPackageId)) {
    $failures.Add("${toolPackageId}: expected DotnetTool package was not found among the packed artifacts")
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "Package validation FAILED ($($failures.Count) issue(s)):" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}

Write-Host "Package validation passed for $($nupkgs.Count) package(s)." -ForegroundColor Green
exit 0
