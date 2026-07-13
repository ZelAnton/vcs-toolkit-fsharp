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
/// on PATH. Used to skip jj-dependent tests when jj isn't provisioned (see `requireBinary`).
let private binaryAvailable (probe: unit -> unit) : bool =
    try
        probe ()
        true
    with _ ->
        // the binary isn't on PATH (or failed to spawn) — the guarded test can't run.
        false

/// Skip (rather than fail) when a required binary is unavailable. `.github/workflows/ci.yml`
/// installs no jj on any of the three OS runners, and `VcsToolkit.Jj.Tests` passing green proves
/// nothing about real jj availability — it drives a scripted fake runner, never the real binary
/// (see `tests/VcsToolkit.Jj.Tests/JjTests.fs`). The only tests in this repo that actually invoke
/// real jj (`tests/VcsToolkit.TestKit.Tests/TestKitTests.fs`, via `JjSandbox.Init`) skip when it's
/// absent — "a hermetic CI has git but not jj" is documented there. This jj fallback is a
/// best-effort, jj-only convenience: the git-backed detection path and the `--forge` override path
/// are covered unconditionally by the other tests in this file, which don't need jj. Skipping here
/// when jj is missing matches that established convention rather than turning CI red on every
/// runner that lacks jj.
let private requireBinary (name: string) (probe: unit -> unit) =
    if not (binaryAvailable probe) then
        Assert.Ignore $"{name} not available on PATH"

/// A throwaway **non-colocated** jj repo (`JjSandbox.InitNonColocated`, i.e. `jj git init
/// --no-colocate`) — the scenario this task's fallback targets. `JjSandbox.Init` is
/// colocated (`--colocate`), so it always has a root `.git` and never exercises the
/// fallback.
let private nonColocatedJjRepo (tag: string) : JjSandbox = JjSandbox.InitNonColocated tag

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
