#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds (or watches) the fsdocs API-reference/documentation site.

.DESCRIPTION
    Wraps `dotnet fsdocs`, which reads `docs/*.md` as content pages and
    `src/*`'s XML doc comments (via `--projects`, defaulted to every packable
    project) as the API reference.

    docs/index.md — the site's home page — is regenerated from README.md on
    every run, so the two never drift: the repository's actual README (with
    fsdocs front matter prepended) becomes the home page verbatim. Root-relative
    links inside it are rewritten so they resolve on the generated site instead
    of 404ing: `docs/foo.md` -> `foo.html` (fsdocs renders it as a sibling
    content page); every other repo-relative link (LICENSE, CHANGELOG.md, ...)
    -> a `blob/main` URL on GitHub, since those files are not part of the site.
    docs/index.md is generated output, not checked in (see .gitignore) — edit
    README.md, never docs/index.md directly.

.PARAMETER Output
    Output folder for the generated site. Defaults to 'output'. Ignored with
    -Watch (fsdocs always uses 'tmp/watch' for that mode).

.PARAMETER Root
    Site root URL substituted into every generated page — fsdocs templates use
    root-absolute asset/nav links, not relative ones, so this must match where
    the site is actually served. Defaults to this repo's GitHub Pages URL (see
    the publish workflow, .github/workflows/docs.yml). Ignored with -Watch
    (fsdocs defaults to the local preview server's own http://localhost:<port>/).

.PARAMETER Configuration
    MSBuild configuration used to build the projects fsdocs cracks for API
    docs. Defaults to Release.

.PARAMETER Watch
    Run `dotnet fsdocs watch` instead of `build`: serves the site locally
    (default http://localhost:8901) and rebuilds on change. Useful to preview
    edits to docs/*.md or XML doc comments.

.EXAMPLE
    pwsh ./scripts/build-docs.ps1

.EXAMPLE
    pwsh ./scripts/build-docs.ps1 -Watch
#>
[CmdletBinding()]
param(
    [string]$Output = 'output',
    [string]$Root = 'https://zelanton.github.io/vcs-toolkit-fsharp/',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Watch
)

$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ReadmePath = Join-Path $RepoRoot 'README.md'
$IndexPath = Join-Path $RepoRoot 'docs' 'index.md'
$RepoUrl = 'https://github.com/ZelAnton/vcs-toolkit-fsharp'

if (-not (Test-Path -LiteralPath $ReadmePath)) {
    throw "README.md not found at $ReadmePath"
}

$readme = Get-Content -LiteralPath $ReadmePath -Raw

# Rewrite every markdown link target that is not already absolute (http(s)/mailto)
# or an in-page anchor. `docs/x.md` becomes fsdocs' own rendered sibling page
# `x.html`; everything else (LICENSE, CHANGELOG.md, Directory.Build.targets, ...)
# is not part of the generated site, so it becomes a GitHub blob link instead.
# README.md has no local image links, so `![alt](...)` syntax is not a concern here.
$linkPattern = '\]\(([^)#][^)]*)\)'
$readme = [regex]::Replace(
    $readme,
    $linkPattern,
    [System.Text.RegularExpressions.MatchEvaluator] {
        param($match)
        $target = $match.Groups[1].Value
        if ($target -match '^(https?:|mailto:)') {
            return "]($target)"
        }
        if ($target -match '^docs/(.+)\.md$') {
            return "]($($Matches[1]).html)"
        }
        return "]($RepoUrl/blob/main/$target)"
    })

# category/categoryindex/index pin the home page first in fsdocs' sidebar;
# title backs the <title>/OpenGraph tags since the README's own leading line
# is an H1 ("# VcsToolkit"), which fsdocs otherwise uses only for on-page text.
$frontMatter = @(
    '---'
    'title: VcsToolkit'
    'category: Documentation'
    'categoryindex: 1'
    'index: 1'
    '---'
    ''
) -join "`n"

Set-Content -LiteralPath $IndexPath -Value ($frontMatter + $readme) -NoNewline

$verb = if ($Watch) { 'watch' } else { 'build' }
$dotnetArgs = @('fsdocs', $verb, '--properties', "Configuration=$Configuration")
if ($Watch) {
    $dotnetArgs += @('--parameters', 'fsdocs-favicon-src', 'img/logo.png')
    Write-Host "==> Watching fsdocs site (local preview server)" -ForegroundColor DarkGray
}
else {
    $dotnetArgs += @(
        '--clean'
        '--output'
        $Output
        '--parameters'
        'root'
        $Root
        'fsdocs-favicon-src'
        'img/logo.png'
    )
    Write-Host "==> Building fsdocs site -> $Output (root=$Root)" -ForegroundColor DarkGray
}

Push-Location $RepoRoot
try {
    & dotnet @dotnetArgs
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
