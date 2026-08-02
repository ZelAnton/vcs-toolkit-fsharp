#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Smoke-tests a `vcs-mcp` container image by driving it over the MCP stdio protocol.

.DESCRIPTION
    The publish gate for the container image built from the repository's Dockerfile:
    .github/workflows/release.yml runs this against the freshly built image and pushes to
    ghcr.io only if it passes.

    It is the container analogue of
    tests/VcsToolkit.Mcp.Server.Tests/McpServerStdioE2eTests.fs — the same protocol
    scenario (initialize handshake, tools/list, a read tool call against a real seeded
    repository), asserted over `docker run` instead of the built binary. Those tests prove
    the binary's SDK wiring; this proves the IMAGE serves that binary: its entrypoint, the
    runtime layer, the bundled CLIs, and a mounted repository.

    Everything the check needs comes from the image itself — the git repository is seeded
    with the image's own `git` into a throwaway Docker volume — so the host needs nothing
    but Docker, and host state cannot contaminate the result.

    Checks, in order:
      1. every bundled CLI (git, jj, gh, glab, tea) runs and reports its version;
      2. `initialize` returns a serverInfo block naming `vcs-mcp` (and, with
         -ExpectedVersion, the release version — proving the build stamped it in);
      3. `tools/list` advertises the repo_*/forge_* catalogue;
      4. `repo_snapshot`, a read tool, answers with a well-formed snapshot of the seeded
         repository (branch `main`, clean, unconflicted) — which only succeeds if the
         bundled git actually ran against the mounted working copy.

.PARAMETER Image
    The image reference to test, e.g. `vcs-mcp:dev` or `ghcr.io/owner/repo/vcs-mcp:1.2.3`.

.PARAMETER ExpectedVersion
    Optional. Assert that the version the server advertises over MCP starts with this
    string. The release workflow passes the release version, so an image whose assemblies
    were not stamped with it fails the gate before publication.

.PARAMETER TimeoutSeconds
    Per-response ceiling for the stdio conversation. Defaults to 120.

.EXAMPLE
    pwsh ./scripts/smoke-mcp-image.ps1 -Image vcs-mcp:dev

.EXAMPLE
    pwsh ./scripts/smoke-mcp-image.ps1 -Image ghcr.io/owner/repo/vcs-mcp:1.2.3 -ExpectedVersion 1.2.3
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Image,

    [string]$ExpectedVersion,

    [int]$TimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The MCP revision this check speaks. The server negotiates its own and answers with it;
# that answer is reported for diagnostics, not asserted — the handshake succeeding is the
# contract here, not a particular protocol revision.
$protocolVersion = '2024-11-05'

function Invoke-Docker {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$What
    )

    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed (docker exited $LASTEXITCODE)."
    }
}

function Send-Rpc {
    param(
        [Parameter(Mandatory = $true)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory = $true)][hashtable]$Message
    )

    # MCP's stdio transport is newline-delimited JSON: one message per line, no framing
    # header. The newline is written explicitly rather than through WriteLine, whose
    # platform NewLine would send CRLF from a Windows host.
    $json = $Message | ConvertTo-Json -Depth 10 -Compress
    $Writer.Write($json + "`n")
    $Writer.Flush()
}

function Receive-Rpc {
    param(
        [Parameter(Mandatory = $true)][System.IO.StreamReader]$Reader,
        [Parameter(Mandatory = $true)][int]$Id,
        [Parameter(Mandatory = $true)][int]$Timeout,
        [Parameter(Mandatory = $true)][string]$What
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)

    while ([DateTime]::UtcNow -lt $deadline) {
        $remainingMs = [int][Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
        $read = $Reader.ReadLineAsync()

        if (-not $read.Wait($remainingMs)) {
            throw "Timed out after ${Timeout}s waiting for the response to $What."
        }

        $line = $read.Result
        if ($null -eq $line) {
            throw "The server closed stdout while waiting for the response to $What."
        }
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $message = $line | ConvertFrom-Json
        } catch {
            throw "Non-JSON line on stdout while waiting for the response to ${What}: $line"
        }

        # Anything without this request's id is a server-initiated notification or an
        # unrelated response; keep reading so it cannot be mistaken for the answer.
        if ($message.PSObject.Properties['id'] -and [string]$message.id -eq [string]$Id) {
            if ($message.PSObject.Properties['error']) {
                throw "$What returned a JSON-RPC error: $($message.error | ConvertTo-Json -Depth 10 -Compress)"
            }
            if (-not $message.PSObject.Properties['result']) {
                throw "$What returned neither a result nor an error: $line"
            }
            return $message.result
        }
    }

    throw "Timed out after ${Timeout}s waiting for the response to $What."
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host 'docker CLI not found on PATH.' -ForegroundColor Red
    exit 1
}

$runId = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$volume = "vcs-mcp-smoke-$runId"
$container = "vcs-mcp-smoke-$runId"
$process = $null
$stderrRead = $null
$exitCode = 1

try {
    Write-Host "==> Smoke-testing $Image" -ForegroundColor Cyan

    # 1. The bundled CLIs. A missing or non-runnable binary is a broken image even when the
    #    server itself starts, because the repo_*/forge_* tools spawn exactly these.
    Write-Host '--> bundled CLIs' -ForegroundColor DarkGray
    Invoke-Docker -What 'The bundled CLI check' -Arguments @(
        'run', '--rm', '--entrypoint', 'sh', $Image, '-c',
        'set -eu; git --version; jj --version; gh --version; glab --version; tea --version'
    )

    # 2. A throwaway volume holding a seeded git repository, created with the image's own
    #    git so the host needs no git installed and the container owns what it reads.
    Write-Host '--> seeding a repository in a throwaway volume' -ForegroundColor DarkGray
    Invoke-Docker -What 'Creating the smoke volume' -Arguments @('volume', 'create', $volume)

    $seed = @(
        'set -eu'
        'git init --quiet --initial-branch=main /repo'
        'cd /repo'
        'git config user.email "smoke@example.invalid"'
        'git config user.name "vcs-mcp smoke"'
        'printf "hello\n" > README.md'
        'git add README.md'
        'git commit --quiet --message "seed the working copy so HEAD is born"'
    ) -join '; '

    Invoke-Docker -What 'Seeding the smoke repository' -Arguments @(
        'run', '--rm', '--volume', "${volume}:/repo", '--entrypoint', 'sh', $Image, '-c', $seed
    )

    # 3. The stdio conversation, against the image's real entrypoint (no override).
    Write-Host '--> MCP stdio handshake' -ForegroundColor DarkGray
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'docker'
    # --name, so the teardown below can force-remove the container even if the docker CLI
    # client is killed before `--rm` gets to run.
    foreach ($argument in @('run', '--rm', '--interactive', '--name', $container, '--volume', "${volume}:/repo", $Image, '--repo', '/repo')) {
        $startInfo.ArgumentList.Add($argument)
    }
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    $startInfo.StandardInputEncoding = $utf8NoBom
    $startInfo.StandardOutputEncoding = $utf8NoBom
    $startInfo.StandardErrorEncoding = $utf8NoBom

    $process = [System.Diagnostics.Process]::Start($startInfo)

    # Drain stderr in the background: the server logs there, and a full pipe buffer would
    # otherwise deadlock the child mid-conversation.
    $stderrRead = $process.StandardError.ReadToEndAsync()

    Send-Rpc -Writer $process.StandardInput -Message @{
        jsonrpc = '2.0'
        id      = 1
        method  = 'initialize'
        params  = @{
            protocolVersion = $protocolVersion
            capabilities    = @{}
            clientInfo      = @{ name = 'vcs-mcp-image-smoke'; version = '1.0.0' }
        }
    }

    $initialize = Receive-Rpc -Reader $process.StandardOutput -Id 1 -Timeout $TimeoutSeconds -What 'initialize'

    if (-not $initialize.PSObject.Properties['serverInfo']) {
        throw 'The initialize result carried no serverInfo block.'
    }
    if ($initialize.serverInfo.name -ne 'vcs-mcp') {
        throw "The server advertised the name '$($initialize.serverInfo.name)', expected 'vcs-mcp'."
    }

    $reportedVersion = [string]$initialize.serverInfo.version
    if ($ExpectedVersion) {
        # StartsWith, not equality: SourceLink appends `+<commit sha>` to the informational
        # version on a repository-aware build, and that suffix is not part of the release tag.
        if (-not $reportedVersion.StartsWith($ExpectedVersion)) {
            throw "The image advertises version '$reportedVersion', which does not start with the expected release version '$ExpectedVersion'."
        }
    }
    Write-Host "    serverInfo: $($initialize.serverInfo.name) $reportedVersion (protocol $($initialize.protocolVersion))"

    Send-Rpc -Writer $process.StandardInput -Message @{
        jsonrpc = '2.0'
        method  = 'notifications/initialized'
        params  = @{}
    }

    # 3a. tools/list — the catalogue reached the wire.
    Send-Rpc -Writer $process.StandardInput -Message @{
        jsonrpc = '2.0'
        id      = 2
        method  = 'tools/list'
        params  = @{}
    }

    $tools = Receive-Rpc -Reader $process.StandardOutput -Id 2 -Timeout $TimeoutSeconds -What 'tools/list'
    if (-not $tools.PSObject.Properties['tools']) {
        throw 'The tools/list result carried no tools array.'
    }

    $toolNames = @($tools.tools | ForEach-Object { [string]$_.name })
    foreach ($required in @('repo_snapshot', 'repo_info', 'forge_pr_list')) {
        if ($toolNames -notcontains $required) {
            throw "tools/list did not advertise '$required' (got $($toolNames.Count) tools)."
        }
    }
    Write-Host "    tools/list: $($toolNames.Count) tools"

    # 3b. A read tool against the mounted repository — the end-to-end proof that the
    #     bundled git ran inside the container and its output came back over stdio.
    Send-Rpc -Writer $process.StandardInput -Message @{
        jsonrpc = '2.0'
        id      = 3
        method  = 'tools/call'
        params  = @{
            name      = 'repo_snapshot'
            arguments = @{}
        }
    }

    $call = Receive-Rpc -Reader $process.StandardOutput -Id 3 -Timeout $TimeoutSeconds -What 'tools/call repo_snapshot'

    if ($call.PSObject.Properties['isError'] -and $call.isError) {
        throw "repo_snapshot returned a tool-execution error: $($call | ConvertTo-Json -Depth 10 -Compress)"
    }
    if (-not $call.PSObject.Properties['content']) {
        throw 'The repo_snapshot result carried no content block.'
    }

    $text = @($call.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { [string]$_.text })
    if ($text.Count -eq 0) {
        throw 'The repo_snapshot result carried no text content block.'
    }

    $snapshot = $text[0] | ConvertFrom-Json
    if ($snapshot.branch -ne 'main') {
        throw "repo_snapshot reported branch '$($snapshot.branch)', expected 'main'."
    }
    if ($snapshot.dirty) {
        throw 'repo_snapshot reported a dirty working copy in a freshly seeded repository.'
    }
    if ($snapshot.conflicted) {
        throw 'repo_snapshot reported conflicts in a freshly seeded repository.'
    }
    Write-Host "    repo_snapshot: branch=$($snapshot.branch) dirty=$($snapshot.dirty) operation=$($snapshot.operation)"

    # Closing stdin is the MCP client's shutdown signal; the server should exit on its own.
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(10000)) {
        throw 'The server did not exit within 10s of stdin being closed.'
    }
    if ($process.ExitCode -ne 0) {
        $stderrText = if ($stderrRead.Wait(5000)) { $stderrRead.Result } else { '<stderr unavailable>' }
        throw "The container exited with code $($process.ExitCode) after a clean shutdown. stderr: $stderrText"
    }

    Write-Host "SMOKE OK: $Image serves MCP over stdio with git/jj/gh/glab/tea available." -ForegroundColor Green
    $exitCode = 0
} catch {
    Write-Host "SMOKE FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($null -ne $process -and $null -ne $stderrRead) {
        try {
            if ($stderrRead.Wait(5000) -and -not [string]::IsNullOrWhiteSpace($stderrRead.Result)) {
                Write-Host '--- container stderr ---' -ForegroundColor DarkGray
                Write-Host $stderrRead.Result
            }
        } catch {
            # The stderr drain never completed (the child is still holding the pipe open);
            # the failure message above is the diagnostic, so this is not worth escalating.
        }
    }
    $exitCode = 1
} finally {
    if ($null -ne $process) {
        try {
            if (-not $process.HasExited) {
                $process.Kill($true)
            }
        } catch {
            # The child exited between the check and the kill; nothing left to clean up.
        }
        $process.Dispose()
    }

    # Best-effort teardown; a leaked container or volume must not fail the gate itself.
    # The volume removal only succeeds once the container holding it is gone, so order matters.
    & docker rm --force $container *> $null
    & docker volume rm --force $volume *> $null
}

# Explicit, so a non-zero $LASTEXITCODE left by the best-effort teardown above cannot leak
# out as this script's own exit code.
exit $exitCode
