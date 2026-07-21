module VcsToolkit.Mcp.Server.Tests.ForgeDetectionTests

open System.IO
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
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

/// Skip unavailable binaries locally, except jj when CI explicitly requires it.
let private requireBinary (name: string) (probe: unit -> unit) =
    if not (binaryAvailable probe) then
        let message = $"{name} not available on PATH"

        if name = "jj" && System.Environment.GetEnvironmentVariable "REQUIRE_JJ" = "1" then
            Assert.Fail $"REQUIRE_JJ=1 but {message}"
        else
            Assert.Ignore message

/// A throwaway **non-colocated** jj repo (`JjSandbox.InitNonColocated`, i.e. `jj git init
/// --no-colocate`) — the scenario this task's fallback targets. `JjSandbox.Init` is
/// colocated (`--colocate`), so it always has a root `.git` and never exercises the
/// fallback.
// ---------------------------------------------------------------------------
// The jj fallback (T-043): non-colocated jj repo, origin remote via `jj git remote list`.
// ---------------------------------------------------------------------------

[<TestFixture>]
type JjForgeDetectionTests() =

    [<Test>]
    member _.JjRemoteListForcesColorNever() : Task =
        task {
            let captured = ref (None: Command option)

            let runner =
                ScriptedRunner()
                    .When(
                        (fun (command: Command) ->
                            captured.Value <- Some command
                            true),
                        Reply.Ok "origin https://github.com/example/repo.git\n"
                    )

            let repo = Repo.FromJj("/repo", "/repo", Jj.WithRunner runner)

            match! Main.detectForgeKind repo with
            | Some ForgeKind.GitHub -> ()
            | other -> Assert.Fail $"expected Some GitHub, got {other}"

            match captured.Value with
            | Some command ->
                Assert.That(
                    (command.Arguments |> Seq.toList = [ "git"
                                                         "remote"
                                                         "list"
                                                         "--ignore-working-copy"
                                                         "--color"
                                                         "never" ]),
                    Is.True
                )
            | Option.None -> Assert.Fail "detectForgeKind must run jj git remote list"
        }

    [<Test>]
    member _.NonColocatedJjRepoResolvesForgeFromOriginRemote() : Task =
        task {
            requireBinary "jj" (fun () -> Raw.jj "." [ "--version" ])
            use dir = JjSandbox.InitNonColocated "jj-noncolo-forge"

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
            use dir = JjSandbox.InitNonColocated "jj-noncolo-noremote"

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
            use dir = JjSandbox.InitNonColocated "jj-forge-override"

            let repo = Repo.FromJj(dir.Path, dir.Path, Jj.Create())

            match! Main.resolveForge repo (Some ForgeKind.GitLab) None None with
            | Some forge -> Assert.That(forge.Kind, Is.EqualTo ForgeKind.GitLab)
            | Option.None -> Assert.Fail "expected the forced --forge to produce a Forge, got None"
        }
