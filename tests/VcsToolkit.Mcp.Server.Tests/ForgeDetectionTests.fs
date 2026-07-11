module VcsToolkit.Mcp.Server.Tests.ForgeDetectionTests

open System.IO
open System.Threading.Tasks
open NUnit.Framework
open VcsToolkit.Core
open VcsToolkit.Forge
open VcsToolkit.Git
open VcsToolkit.Jj
open VcsToolkit.TestKit

/// Whether a probe (a `<binary> --version` call) runs without raising — i.e. the binary is
/// on PATH. The jj fallback is a required integration scenario, so its tests fail when jj is unavailable.
let private binaryAvailable (probe: unit -> unit) : bool =
    try
        probe ()
        true
    with _ ->
        // the binary isn't on PATH (or failed to spawn) — the guarded test can't run.
        false

/// Hard-fail (rather than skip) when a required binary is unavailable. This is safe for jj:
/// jj is an unconditional hard dependency of the VcsToolkit.Jj project itself. VcsToolkit.Jj.Tests
/// (which exercises the real `jj` binary via JjSandbox.Init with no availability skip logic whatsoever)
/// passes 97/97 tests on all three CI runners (ubuntu-latest, windows-latest, macos-latest) as of
/// run 29155465472 (2026-07-11, main branch push). This confirms jj is already provisioned and
/// available on all CI runners; therefore, hard-failing here when it's missing is correct and will
/// never mask a true issue in CI.
let private requireBinary (name: string) (probe: unit -> unit) =
    if not (binaryAvailable probe) then
        Assert.Fail $"{name} must be available on PATH to run required forge-detection tests"

/// A throwaway **non-colocated** jj repo (`jj git init --no-colocate`) — the scenario
/// this task's fallback targets. `JjSandbox.Init` (`jj git init` with no flag) is
/// *colocated* by jj's current default, so it always has a root `.git` and never
/// exercises the fallback; building it by hand here (rather than extending `TestKit`,
/// out of this task's declared domain) via the `Raw` escape hatch is deliberate.
let private nonColocatedJjRepo (tag: string) : TempDir =
    let dir = new TempDir(tag)
    Raw.jj dir.Path [ "git"; "init"; "--no-colocate" ]
    dir

// ---------------------------------------------------------------------------
// The jj fallback (T-043): non-colocated jj repo, origin remote via `jj git remote list`.
// ---------------------------------------------------------------------------

[<TestFixture>]
type JjForgeDetectionTests() =

    [<Test>]
    member _.NonColocatedJjRepoResolvesForgeFromOriginRemote() : Task =
        task {
            requireBinary "jj" (fun () -> Raw.jj "." [ "--version" ])
            use dir = nonColocatedJjRepo "jj-noncolo-forge"

            // Confirm the scenario is genuinely non-colocated: no root `.git` for
            // `Git.RemoteUrl` to find. If a future jj version flips this default back,
            // this assertion (not a silent pass) is what tells us the test stopped
            // exercising the fallback.
            Assert.That(Directory.Exists(Path.Combine(dir.Path, ".git")), Is.False, "must be non-colocated")

            Raw.jj dir.Path [ "git"; "remote"; "add"; "origin"; "https://github.com/example/repo.git" ]

            let repo = Repo.FromJj(dir.Path, dir.Path, Jj.Create())

            match! Main.detectForgeKind repo with
            | Some ForgeKind.GitHub -> Assert.Pass()
            | other -> Assert.Fail $"expected Some GitHub, got {other}"
        }

    [<Test>]
    member _.NonColocatedJjRepoWithoutOriginReturnsNone() : Task =
        task {
            requireBinary "jj" (fun () -> Raw.jj "." [ "--version" ])
            use dir = nonColocatedJjRepo "jj-noncolo-noremote"

            let repo = Repo.FromJj(dir.Path, dir.Path, Jj.Create())

            match! Main.detectForgeKind repo with
            | Option.None -> Assert.Pass()
            | other -> Assert.Fail $"expected None (no remote configured), got {other}"
        }

// ---------------------------------------------------------------------------
// Regression: git-backed detection (unchanged codepath) and the `--forge` override.
// ---------------------------------------------------------------------------

[<TestFixture>]
type GitForgeDetectionRegressionTests() =

    [<Test>]
    member _.GitBackedRepoStillResolvesForgeFromOriginRemote() : Task =
        task {
            requireBinary "git" (fun () -> Raw.git "." [ "--version" ])
            use sandbox = GitSandbox.Init "git-forge-regress"
            sandbox.Git [ "remote"; "add"; "origin"; "https://github.com/example/repo.git" ]

            let repo = Repo.FromGit(sandbox.Path, sandbox.Path, Git.Create())

            match! Main.detectForgeKind repo with
            | Some ForgeKind.GitHub -> Assert.Pass()
            | other -> Assert.Fail $"expected Some GitHub, got {other}"
        }

[<TestFixture>]
type ForgeOverrideTests() =

    [<Test>]
    member _.ExplicitForgeBypassesRemoteDetection() : Task =
        task {
            requireBinary "jj" (fun () -> Raw.jj "." [ "--version" ])
            // No `origin` remote at all — detection alone would yield `None` — yet the
            // explicit `--forge` override must still win.
            use dir = nonColocatedJjRepo "jj-forge-override"

            let repo = Repo.FromJj(dir.Path, dir.Path, Jj.Create())

            match! Main.resolveForge repo (Some ForgeKind.GitLab) None with
            | Some forge -> Assert.That(forge.Kind, Is.EqualTo ForgeKind.GitLab)
            | Option.None -> Assert.Fail "expected the forced --forge to produce a Forge, got None"
        }
