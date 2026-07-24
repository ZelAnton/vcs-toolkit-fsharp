#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Checks source documentation links and the MCP tool-reference catalogue.

.DESCRIPTION
    Every relative link in the published docs/ Markdown must stay inside docs/ so the generated
    GitHub Pages artifact contains its target. Root documentation may link anywhere in the repo.

    The MCP tool tables in docs/mcp-server.md must also contain exactly one row for every
    repo_* / forge_* ToolSpec declared in src/VcsToolkit.Mcp/Catalog.fs. This catches catalogue
    additions before the user guide silently falls behind the executable server.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of this script's directory.

.EXAMPLE
    pwsh ./scripts/check-docs.ps1
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
$docsDir = Join-Path $RepoRoot 'docs'
$catalogPath = Join-Path $RepoRoot 'src' 'VcsToolkit.Mcp' 'Catalog.fs'
$mcpGuidePath = Join-Path $docsDir 'mcp-server.md'
$failures = [System.Collections.Generic.List[string]]::new()

foreach ($requiredPath in @($docsDir, $catalogPath, $mcpGuidePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        $failures.Add("Required documentation input is missing: $requiredPath")
    }
}

if ($failures.Count -eq 0) {
    $catalogText = Get-Content -LiteralPath $catalogPath -Raw
    $guideText = Get-Content -LiteralPath $mcpGuidePath -Raw

    $catalogMatches = [regex]::Matches(
        $catalogText,
        '(?ms)^\s*(?:\[\s*)?(?:read|write)\s+(?:\r?\n\s+)?"((?:repo|forge)_[a-z0-9_]+)"'
    )
    $catalogRecordMatches = [regex]::Matches(
        $catalogText,
        '(?m)^\s*\{\s*Name\s*=\s*"((?:repo|forge)_[a-z0-9_]+)"'
    )
    $guideMatches = [regex]::Matches(
        $guideText,
        '(?m)^\|\s+`((?:repo|forge)_[a-z0-9_]+)`\s+\|'
    )

    $catalogNames = @(
        @($catalogMatches) + @($catalogRecordMatches) |
            ForEach-Object { $_.Groups[1].Value } |
            Sort-Object -Unique
    )
    $guideNames = @($guideMatches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)

    foreach ($duplicate in $guideMatches | Group-Object { $_.Groups[1].Value } | Where-Object Count -gt 1) {
        $failures.Add("docs/mcp-server.md has duplicate tool rows for '$($duplicate.Name)'")
    }

    foreach ($name in $catalogNames) {
        if ($name -notin $guideNames) {
            $failures.Add("docs/mcp-server.md has no tool-reference row for catalogue tool '$name'")
        }
    }

    foreach ($name in $guideNames) {
        if ($name -notin $catalogNames) {
            $failures.Add("docs/mcp-server.md has stale tool-reference row '$name' absent from Catalog.fs")
        }
    }
}

$rootMarkdown = @(
    'README.md'
    'CONTRIBUTING.md'
    'SECURITY.md'
    'release-token-bypass.md'
) | ForEach-Object { Join-Path $RepoRoot $_ }
$publishedMarkdown = @(Get-ChildItem -LiteralPath $docsDir -Filter '*.md' -File) |
    Where-Object Name -ne 'index.md' |
    ForEach-Object FullName
$markdownFiles = @($rootMarkdown) + @($publishedMarkdown)

foreach ($markdownPath in $markdownFiles) {
    if (-not (Test-Path -LiteralPath $markdownPath)) {
        $failures.Add("Documentation file is missing: $markdownPath")
        continue
    }

    $text = Get-Content -LiteralPath $markdownPath -Raw
    $linkMatches = [regex]::Matches($text, '!?(?:\[[^\]]*\])\(([^)]+)\)')
    $isPublishedPage = $markdownPath.StartsWith(
        $docsDir + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase
    )

    foreach ($match in $linkMatches) {
        $target = $match.Groups[1].Value.Trim()
        if ($target -match '^(?:https?://|mailto:|#)') {
            continue
        }

        $targetPath = ($target -split '[?#]', 2)[0]
        if ([string]::IsNullOrWhiteSpace($targetPath)) {
            continue
        }

        $resolvedTarget = [IO.Path]::GetFullPath(
            (Join-Path (Split-Path -Parent $markdownPath) ([Uri]::UnescapeDataString($targetPath)))
        )
        if (-not (Test-Path -LiteralPath $resolvedTarget)) {
            $relativeSource = [IO.Path]::GetRelativePath($RepoRoot, $markdownPath)
            $failures.Add("$relativeSource links to missing relative target '$target'")
            continue
        }

        if ($isPublishedPage) {
            $relativeToDocs = [IO.Path]::GetRelativePath($docsDir, $resolvedTarget)
            if ($relativeToDocs -eq '..' -or $relativeToDocs.StartsWith("..$([IO.Path]::DirectorySeparatorChar)")) {
                $relativeSource = [IO.Path]::GetRelativePath($RepoRoot, $markdownPath)
                $failures.Add("$relativeSource links outside the Pages artifact via '$target'; use a docs-local or absolute URL")
            }
        }
    }
}

Write-Output ''
if ($failures.Count -gt 0) {
    Write-Output 'Documentation consistency check FAILED:'
    foreach ($failure in $failures) {
        Write-Output "  - $failure"
    }
    exit 1
}

Write-Output "OK: documentation links resolve and all $($catalogNames.Count) MCP tools have exactly one reference row."
