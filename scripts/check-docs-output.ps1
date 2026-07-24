#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Checks local references and fragments in a generated fsdocs site.

.DESCRIPTION
    Resolves every relative href and src in the generated HTML against the Pages artifact. Local
    targets must stay inside the artifact and exist. Fragment identifiers are matched
    case-sensitively against an exact id or name attribute, which catches links that work on
    GitHub Markdown but break after fsdocs preserves heading capitalization.

.PARAMETER Output
    Generated fsdocs directory. Defaults to output under the repository root.

.PARAMETER SiteRoot
    Absolute root URL used to build the site. URLs below this root are mapped back into Output and
    validated like relative references.

.EXAMPLE
    pwsh ./scripts/check-docs-output.ps1
#>
[CmdletBinding()]
param(
    [string] $Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'output'),
    [string] $SiteRoot = 'https://zelanton.github.io/vcs-toolkit-fsharp/'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Output -PathType Container)) {
    throw "Generated documentation directory is missing: $Output"
}

$outputRoot = (Resolve-Path -LiteralPath $Output).Path
$SiteRoot = $SiteRoot.TrimEnd('/') + '/'
$failures = [System.Collections.Generic.List[string]]::new()
$pathComparer = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
$htmlCache = [System.Collections.Generic.Dictionary[string, string]]::new(
    $pathComparer
)
$leafCache = [System.Collections.Generic.Dictionary[string, bool]]::new(
    $pathComparer
)
$directoryCache = [System.Collections.Generic.Dictionary[string, bool]]::new(
    $pathComparer
)
$localResourceCount = 0
$fragmentCount = 0

function Get-HtmlText {
    param([string] $Path)

    if (-not $htmlCache.ContainsKey($Path)) {
        $htmlCache[$Path] = Get-Content -LiteralPath $Path -Raw
    }

    return $htmlCache[$Path]
}

function Test-ArtifactLeaf {
    param([string] $Path)

    if (-not $leafCache.ContainsKey($Path)) {
        $leafCache[$Path] = Test-Path -LiteralPath $Path -PathType Leaf
    }

    return $leafCache[$Path]
}

function Test-ArtifactDirectory {
    param([string] $Path)

    if (-not $directoryCache.ContainsKey($Path)) {
        $directoryCache[$Path] = Test-Path -LiteralPath $Path -PathType Container
    }

    return $directoryCache[$Path]
}

foreach ($source in Get-ChildItem -LiteralPath $outputRoot -Recurse -File -Filter '*.html') {
    $sourceText = Get-HtmlText -Path $source.FullName
    $resourceMatches = [regex]::Matches(
        $sourceText,
        '(?i)\b(?:href|src)\s*=\s*["''](?<target>[^"'']*)["'']'
    )

    foreach ($resourceMatch in $resourceMatches) {
        $target = [Net.WebUtility]::HtmlDecode($resourceMatch.Groups['target'].Value).Trim()
        $originalTarget = $target
        $isSiteAbsolute = $target.StartsWith($SiteRoot, [StringComparison]::OrdinalIgnoreCase)
        if ($isSiteAbsolute) {
            $target = $target.Substring($SiteRoot.Length)
        }
        elseif ([string]::IsNullOrWhiteSpace($target) -or $target -match '^(?:[a-z][a-z0-9+.-]*:|//|/)') {
            continue
        }

        $parts = $target -split '#', 2
        $pathPart = ($parts[0] -split '\?', 2)[0]
        $fragment = if ($parts.Count -eq 2) { [Uri]::UnescapeDataString($parts[1]) } else { '' }
        $baseDirectory = if ($isSiteAbsolute) { $outputRoot } else { $source.DirectoryName }
        $candidate = if ([string]::IsNullOrWhiteSpace($pathPart)) {
            $source.FullName
        }
        else {
            [IO.Path]::GetFullPath(
                (Join-Path $baseDirectory ([Uri]::UnescapeDataString($pathPart)))
            )
        }

        $relativeCandidate = [IO.Path]::GetRelativePath($outputRoot, $candidate)
        if ($relativeCandidate -eq '..' -or $relativeCandidate.StartsWith("..$([IO.Path]::DirectorySeparatorChar)")) {
            $relativeSource = [IO.Path]::GetRelativePath($outputRoot, $source.FullName)
            $failures.Add("$relativeSource references outside the Pages artifact via '$originalTarget'")
            continue
        }

        if (Test-ArtifactDirectory -Path $candidate) {
            $candidate = Join-Path $candidate 'index.html'
        }

        $localResourceCount++
        if (-not (Test-ArtifactLeaf -Path $candidate)) {
            $relativeSource = [IO.Path]::GetRelativePath($outputRoot, $source.FullName)
            $failures.Add("$relativeSource references missing generated target '$originalTarget'")
            continue
        }

        if ([string]::IsNullOrEmpty($fragment)) {
            continue
        }

        $fragmentCount++
        $targetText = Get-HtmlText -Path $candidate
        $markers = @(
            'id="' + $fragment + '"'
            "id='" + $fragment + "'"
            'name="' + $fragment + '"'
            "name='" + $fragment + "'"
        )
        if (-not ($markers | Where-Object { $targetText.Contains($_) })) {
            $relativeSource = [IO.Path]::GetRelativePath($outputRoot, $source.FullName)
            $relativeTarget = [IO.Path]::GetRelativePath($outputRoot, $candidate)
            $failures.Add("$relativeSource links to missing fragment '#$fragment' in $relativeTarget")
        }
    }
}

Write-Output ''
if ($failures.Count -gt 0) {
    Write-Output 'Generated documentation link check FAILED:'
    foreach ($failure in $failures | Sort-Object -Unique) {
        Write-Output "  - $failure"
    }
    exit 1
}

Write-Output "OK: $localResourceCount generated local references resolve, including $fragmentCount fragments."
