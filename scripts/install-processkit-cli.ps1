[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Destination,

    [string] $EvidencePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$version = '0.3.3'
$releaseBase = "https://github.com/ZelAnton/ProcessKit-CLI/releases/download/v$version"

$targets = @{
    'windows-x64' = @{
        Asset = "processkit-cli-v$version-x86_64-pc-windows-msvc.zip"
        Sha256 = '5d4d3fc8be39159a6cc5e8727dcbc0686f93b1cbd961e3902c41fb40eb2b0cb6'
        Binary = 'processkit-cli.exe'
    }
    'windows-arm64' = @{
        Asset = "processkit-cli-v$version-aarch64-pc-windows-msvc.zip"
        Sha256 = '4e27f44bd6822d64d5b2f98b94263d51d0f1888ae563b0d45476df144aa7f7c6'
        Binary = 'processkit-cli.exe'
    }
    'linux-x64' = @{
        Asset = "processkit-cli-v$version-x86_64-unknown-linux-gnu.tar.gz"
        Sha256 = '68e56188bba81e5fb8330e6f56280a997e3ab6163e5bdcdf9972e8d6bf814c55'
        Binary = 'processkit-cli'
    }
    'linux-arm64' = @{
        Asset = "processkit-cli-v$version-aarch64-unknown-linux-gnu.tar.gz"
        Sha256 = '2712a7d8c1a0a826dea93314562dca1af87f6fc04df030af01b12b679ef784e9'
        Binary = 'processkit-cli'
    }
    'macos-arm64' = @{
        Asset = "processkit-cli-v$version-aarch64-apple-darwin.tar.gz"
        Sha256 = '69992d06cbf50d78690ef3ec3a9f8aae423fdce9aea98265c27ed7f6d47db738'
        Binary = 'processkit-cli'
    }
}

$architecture = switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
    ([System.Runtime.InteropServices.Architecture]::X64) { 'x64'; break }
    ([System.Runtime.InteropServices.Architecture]::Arm64) { 'arm64'; break }
    default { [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant() }
}

$os = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows
    )) {
    'windows'
}
elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Linux
    )) {
    'linux'
}
elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::OSX
    )) {
    'macos'
}
else {
    'unknown'
}

$targetKey = "$os-$architecture"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
[System.IO.Directory]::CreateDirectory($destinationPath) | Out-Null

$evidence = [ordered]@{
    status = 'unavailable'
    version = $version
    target = $targetKey
    asset = $null
    sha256 = $null
    binary = $null
    reason = $null
}

try {
    if (-not $targets.ContainsKey($targetKey)) {
        $evidence.reason =
            "ProcessKit-CLI v$version publishes no asset for $targetKey; supported release targets are $($targets.Keys -join ', ')."
        throw $evidence.reason
    }

    $target = $targets[$targetKey]
    $assetName = [string] $target.Asset
    $expectedHash = [string] $target.Sha256
    $assetPath = Join-Path $destinationPath $assetName
    $downloadUri = "$releaseBase/$assetName"
    $evidence.asset = $assetName
    $evidence.sha256 = $expectedHash

    Invoke-WebRequest `
        -Uri $downloadUri `
        -OutFile $assetPath `
        -MaximumRetryCount 3 `
        -RetryIntervalSec 2

    $actualHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($actualHash -ne $expectedHash) {
        throw "ProcessKit-CLI asset hash mismatch for ${assetName}: expected $expectedHash, got $actualHash."
    }

    if ($assetName.EndsWith('.zip', [StringComparison]::Ordinal)) {
        Expand-Archive -LiteralPath $assetPath -DestinationPath $destinationPath -Force
    }
    else {
        & tar -xzf $assetPath -C $destinationPath

        if ($LASTEXITCODE -ne 0) {
            throw "tar failed to extract $assetName with exit code $LASTEXITCODE."
        }
    }

    $binary = Get-ChildItem -LiteralPath $destinationPath -Recurse -File |
        Where-Object Name -EQ ([string] $target.Binary) |
        Select-Object -First 1

    if ($null -eq $binary) {
        throw "The verified archive $assetName did not contain $($target.Binary)."
    }

    if ($os -ne 'windows') {
        & chmod +x $binary.FullName

        if ($LASTEXITCODE -ne 0) {
            throw "chmod failed for $($binary.FullName) with exit code $LASTEXITCODE."
        }
    }

    $versionOutput = (& $binary.FullName --version 2>&1 | Out-String).Trim()

    if ($LASTEXITCODE -ne 0 -or $versionOutput -ne "processkit-cli $version") {
        throw "The extracted binary reported '$versionOutput', expected 'processkit-cli $version'."
    }

    $evidence.status = 'installed'
    $evidence.asset = $assetName
    $evidence.sha256 = $actualHash
    $evidence.binary = $binary.FullName
    $evidence.reason = $null
}
catch {
    if ([string]::IsNullOrWhiteSpace([string] $evidence.reason)) {
        $evidence.reason = $_.Exception.Message
    }

    throw
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        $resolvedEvidence = [System.IO.Path]::GetFullPath($EvidencePath)
        $evidenceDirectory = [System.IO.Path]::GetDirectoryName($resolvedEvidence)

        if (-not [string]::IsNullOrWhiteSpace($evidenceDirectory)) {
            [System.IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null
        }

        $evidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resolvedEvidence -Encoding utf8NoBOM
    }
}

$evidence.binary
