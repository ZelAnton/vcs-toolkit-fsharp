module VcsToolkit.Core.Tests

open System
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.Diff
open VcsToolkit.Git
open VcsToolkit.Jj
open VcsToolkit.Core
open VcsToolkit.TestKit

// Create a unique temp directory, run `f` against it, then remove it.
// On macOS, Path.GetTempPath() returns /var/... which is a symlink to /private/var.
// Directory.GetCurrentDirectory() after chdir() returns the resolved path /private/var/...
// To ensure expected paths built via Path.Combine match the OS-level resolved cwd,
// canonicalize the base directory by round-tripping through SetCurrentDirectory/GetCurrentDirectory.
let private withTempDir (f: string -> unit) =
    let unresolved =
        Path.Combine(Path.GetTempPath(), "vcs-core-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory unresolved |> ignore

    // Canonicalize: chdir to it, get the resolved path, chdir back.
    let previous = Directory.GetCurrentDirectory()

    let dir =
        try
            Directory.SetCurrentDirectory unresolved
            Directory.GetCurrentDirectory()
        finally
            Directory.SetCurrentDirectory previous

    try
        f dir
    finally
        try
            Directory.Delete(dir, true)
        with
        | :? IOException ->
            // A test may still hold a file, or have removed its sandbox itself; cleanup must not hide its result.
            ()
        | :? UnauthorizedAccessException ->
            // Windows can briefly deny removal while a test-created handle is being released; preserve the test result.
            ()

// Change the process cwd only for the scope that needs to prove a handle captured its path.
let private withCurrentDirectory (dir: string) (f: unit -> unit) =
    let previous = Directory.GetCurrentDirectory()

    try
        Directory.SetCurrentDirectory dir
        f ()
    finally
        Directory.SetCurrentDirectory previous

// A jj-backed Repo over a runner scripted to reply to `tokens` with `reply`. Also
// scripts `jj root` to echo back the fixed "/repo" cwd every jjRepo test uses, since
// `Jj.Status`/`Jj.DiffSummary` resolve the workspace root before querying — a no-op
// rule for tests that never reach that query.
let private jjRepo (tokens: string list) (reply: Reply) =
    Repo.FromJj("/repo", "/repo", Jj.WithRunner(ScriptedRunner().On([ "root" ], Reply.Ok "/repo\n").On(tokens, reply)))

// A git-backed Repo over a runner scripted to reply to `tokens` with `reply`.
let private gitRepo (tokens: string list) (reply: Reply) =
    Repo.FromGit("/repo", "/repo", Git.WithRunner(ScriptedRunner().On(tokens, reply)))

// A tab, built explicitly so no escape has to survive a round-trip.
let private tab = string (char 9)

// One valid `op log` row (OP_TEMPLATE shape) whose short id is `id` — for scripting the
// divergence probe in the shared jj rollback protocol (`jj.RollbackTo`, which `Repo.TryMerge`
// drives). `parseOperations` needs >= 3 tab fields to keep the id.
let private opRow (id: string) =
    $"{id}{tab}u@h{tab}2026-01-01T00:00:00+00:00{tab}probe\n"

// ---------------------------------------------------------------------------
// detect — pure filesystem probing for the backing repository
// ---------------------------------------------------------------------------

[<TestFixture>]
type DetectTests() =

    [<Test>]
    member _.DetectsAGitDirectory() =
        withTempDir (fun dir ->
            Directory.CreateDirectory(Path.Combine(dir, ".git")) |> ignore

            match Detect.detect dir with
            | Some located ->
                Assert.That(located.Kind, Is.EqualTo BackendKind.Git)
                Assert.That(located.Root, Is.EqualTo dir)
            | None -> Assert.Fail "expected to detect a git repository")

    [<Test>]
    member _.DetectsAJjDirectory() =
        withTempDir (fun dir ->
            // A real jj repo owns a `.jj/repo` store — a bare `.jj` dir is not a valid marker.
            Directory.CreateDirectory(Path.Combine(dir, ".jj", "repo")) |> ignore

            match Detect.detect dir with
            | Some located ->
                Assert.That(located.Kind, Is.EqualTo BackendKind.Jj)
                Assert.That(located.Root, Is.EqualTo dir)
            | None -> Assert.Fail "expected to detect a jj repository")

    [<Test>]
    member _.JjWinsOverGitWhenColocated() =
        withTempDir (fun dir ->
            Directory.CreateDirectory(Path.Combine(dir, ".git")) |> ignore
            Directory.CreateDirectory(Path.Combine(dir, ".jj", "repo")) |> ignore

            match Detect.detect dir with
            | Some located -> Assert.That(located.Kind, Is.EqualTo BackendKind.Jj, "jj drives a colocated repo")
            | None -> Assert.Fail "expected to detect the colocated repo")

    [<Test>]
    member _.StoreLessJjDoesNotShadowGit() =
        withTempDir (fun dir ->
            // A stray/empty `.jj` (an aborted `jj init`, or a bare `mkdir .jj`) with NO `.jj/repo`
            // store must NOT shadow a healthy colocated `.git` — detect falls through to git (M19).
            Directory.CreateDirectory(Path.Combine(dir, ".git")) |> ignore
            Directory.CreateDirectory(Path.Combine(dir, ".jj")) |> ignore // no `repo` store

            match Detect.detect dir with
            | Some located ->
                Assert.That(located.Kind, Is.EqualTo BackendKind.Git, "a store-less .jj must not shadow .git")
            | None -> Assert.Fail "expected to detect the git repo")

    [<Test>]
    member _.AcceptsAGitlinkFile() =
        withTempDir (fun dir ->
            // A linked worktree/submodule uses a `.git` *file* whose content is `gitdir: …`.
            File.WriteAllText(Path.Combine(dir, ".git"), "gitdir: /somewhere/.git/worktrees/wt\n")

            match Detect.detect dir with
            | Some located ->
                Assert.That(located.Kind, Is.EqualTo BackendKind.Git)
                Assert.That(located.Root, Is.EqualTo dir)
            | None -> Assert.Fail "a gitlink file must be accepted as a git marker")

    [<Test>]
    member _.RejectsAGarbageDotGitFile() =
        withTempDir (fun dir ->
            // A stray file merely *named* `.git` (no `gitdir:`) must not be a marker.
            File.WriteAllText(Path.Combine(dir, ".git"), "not a real gitlink\n")
            Assert.That(Detect.detect dir, Is.EqualTo None))

    [<Test>]
    member _.WalksUpToAnAncestorRepo() =
        withTempDir (fun dir ->
            Directory.CreateDirectory(Path.Combine(dir, ".git")) |> ignore
            let sub = Path.Combine(dir, "a", "b")
            Directory.CreateDirectory sub |> ignore

            match Detect.detect sub with
            | Some located -> Assert.That(located.Root, Is.EqualTo dir, "detect walks up to the ancestor holding .git")
            | None -> Assert.Fail "expected to find the ancestor repository")

    [<Test>]
    member _.ReturnsNoneWhenNoRepo() =
        withTempDir (fun dir -> Assert.That(Detect.detect dir, Is.EqualTo None))

    [<Test>]
    member _.GarbageDotGitDoesNotShadowAnAncestorRepo() =
        withTempDir (fun parent ->
            // A real ancestor repo, with a *garbage* `.git` file in a child dir. The child's
            // invalid marker must not shadow the real repo above — detect walks past it.
            Directory.CreateDirectory(Path.Combine(parent, ".git")) |> ignore
            let child = Path.Combine(parent, "child")
            Directory.CreateDirectory child |> ignore
            File.WriteAllText(Path.Combine(child, ".git"), "garbage, not a gitlink\n")

            match Detect.detect child with
            | Some located ->
                Assert.That(located.Root, Is.EqualTo parent, "a garbage .git must not shadow the ancestor")
                Assert.That(located.Kind, Is.EqualTo BackendKind.Git)
            | None -> Assert.Fail "expected to find the ancestor repository past the garbage marker")

    [<Test>]
    member _.RejectsAnEmptyDotGitFile() =
        withTempDir (fun dir ->
            File.WriteAllText(Path.Combine(dir, ".git"), "")
            Assert.That(Detect.detect dir, Is.EqualTo None, "an empty .git file is not a marker"))

// ---------------------------------------------------------------------------
// Repo construction — all stored paths must be independent of process cwd
// ---------------------------------------------------------------------------

[<TestFixture>]
type RepoConstructionTests() =

    [<Test>]
    member _.RelativePathsAreCapturedByEveryConstructor() : Task =
        task {
            withTempDir (fun sandbox ->
                let initial = Path.Combine(sandbox, "initial")
                let later = Path.Combine(sandbox, "later")
                let openDir = Path.Combine(initial, "opened")
                let expectedAt = Path.Combine(initial, "at")
                let expectedRoot = Path.Combine(initial, "root")
                let expectedCwd = Path.Combine(initial, "cwd")
                let worktree = Path.Combine(expectedAt, "wt")
                let atHolder: Repo option ref = ref None
                Directory.CreateDirectory(Path.Combine(openDir, ".git")) |> ignore
                Directory.CreateDirectory initial |> ignore
                Directory.CreateDirectory later |> ignore

                withCurrentDirectory initial (fun () ->
                    match Repo.Open "opened" with
                    | Error e -> Assert.Fail $"Repo.Open(relative) failed: {e.Message}"
                    | Ok opened -> Assert.That(opened.Cwd, Is.EqualTo openDir, "Open captures the relative path now")

                    let git =
                        Repo.FromGit("root", "cwd", Git.WithRunner(ScriptedRunner().Fallback(Reply.Ok "")))

                    let jj =
                        Repo.FromJj(
                            "root",
                            "cwd",
                            Jj.WithRunner(
                                ScriptedRunner()
                                    .On(
                                        [ "workspace"
                                          "add"
                                          "--name"
                                          "feature"
                                          "-r"
                                          "main"
                                          "wt"
                                          "--color"
                                          "never" ],
                                        Reply.Ok ""
                                    )
                                    .On([ "bookmark"; "create"; "feature"; "-r"; "feature@" ], Reply.Ok "")
                                    .On([ "workspace"; "list" ], Reply.Ok $"feature\tdeadbeef\t\n")
                                    .On([ "workspace"; "root"; "--name"; "feature" ], Reply.Ok(worktree + "\n"))
                                    .On([ "workspace"; "forget"; "feature" ], Reply.Ok "")
                            )
                        )

                    let at = jj.At "at"
                    Assert.That(git.Root, Is.EqualTo expectedRoot, "FromGit captures a relative root")
                    Assert.That(git.Cwd, Is.EqualTo expectedCwd, "FromGit captures a relative cwd")
                    Assert.That(jj.Root, Is.EqualTo expectedRoot, "FromJj captures a relative root")
                    Assert.That(jj.Cwd, Is.EqualTo expectedCwd, "FromJj captures a relative cwd")
                    atHolder.Value <- Some at)

                withCurrentDirectory later (fun () ->
                    match atHolder.Value with
                    | None -> Assert.Fail "the relative Repo.At handle was not created"
                    | Some at ->
                        // The re-anchor happened before the cwd changed. Both operations must
                        // resolve `wt` from `initial/at`, not from the later process cwd.
                        Assert.That(at.Cwd, Is.EqualTo expectedAt, "At keeps the captured cwd")

                        match at.CreateWorktree("wt", "feature", "main").GetAwaiter().GetResult() with
                        | Error e -> Assert.Fail $"CreateWorktree failed after cwd change: {e.Message}"
                        | Ok _ -> ()

                        match at.RemoveWorktree("wt", true).GetAwaiter().GetResult() with
                        | Error e -> Assert.Fail $"RemoveWorktree used the process cwd: {e.Message}"
                        | Ok() -> ()))
        }

    [<Test>]
    member _.AbsolutePathsRemainUnchangedForEveryConstructor() =
        withTempDir (fun root ->
            let cwd = Path.Combine(root, "cwd")
            Directory.CreateDirectory(Path.Combine(cwd, ".git")) |> ignore
            let git = Repo.FromGit(root, cwd, Git.WithRunner(ScriptedRunner()))
            let jj = Repo.FromJj(root, cwd, Jj.WithRunner(ScriptedRunner()))

            match Repo.Open cwd with
            | Error e -> Assert.Fail $"Repo.Open(absolute) failed: {e.Message}"
            | Ok opened ->
                Assert.That(opened.Cwd, Is.EqualTo cwd)
                Assert.That(opened.Root, Is.EqualTo cwd)

            Assert.That(git.Root, Is.EqualTo root)
            Assert.That(git.Cwd, Is.EqualTo cwd)
            Assert.That(jj.Root, Is.EqualTo root)
            Assert.That(jj.Cwd, Is.EqualTo cwd)
            Assert.That(git.At(cwd).Cwd, Is.EqualTo cwd))

    [<Test>]
    member _.InvalidPathsProduceDiagnosticInputErrors() =
        let invalid = string (char 0)
        let client = Git.WithRunner(ScriptedRunner())
        let jj = Jj.WithRunner(ScriptedRunner())

        match Repo.Open invalid with
        | Error(RepoError.InvalidInput message) -> Assert.That(message, Does.Contain "dir")
        | Error e -> Assert.Fail $"expected InvalidInput, got: {e.Message}"
        | Ok _ -> Assert.Fail "an invalid path must be refused"

        let requireArgumentException (action: Action) : ArgumentException =
            let caughtException = Assert.Throws<ArgumentException>(action)

            match caughtException with
            | null -> raise (InvalidOperationException "Assert.Throws returned null unexpectedly")
            | nonNullException -> nonNullException

        let fromGit: ArgumentException =
            requireArgumentException (Action(fun () -> Repo.FromGit(invalid, ".", client) |> ignore))

        let fromJj: ArgumentException =
            requireArgumentException (Action(fun () -> Repo.FromJj(".", invalid, jj) |> ignore))

        let repo = Repo.FromGit(".", ".", client)

        let at: ArgumentException =
            requireArgumentException (Action(fun () -> repo.At invalid |> ignore))

        Assert.That(fromGit.ParamName, Is.EqualTo "root")
        Assert.That(fromJj.ParamName, Is.EqualTo "cwd")
        Assert.That(at.ParamName, Is.EqualTo "dir")

    // --- OpenWith: inject the per-backend client via a lazy factory (T-071) ------

    [<Test>]
    member _.OpenWithDetectsGitAndUsesOnlyTheInjectedGitClient() =
        withTempDir (fun sandbox ->
            Directory.CreateDirectory(Path.Combine(sandbox, ".git")) |> ignore
            let injected = Git.WithRunner(ScriptedRunner().Fallback(Reply.Ok ""))
            let mutable gitBuilt = 0
            let mutable jjBuilt = 0

            let makeGit () =
                gitBuilt <- gitBuilt + 1
                injected

            let makeJj () =
                jjBuilt <- jjBuilt + 1
                Jj.WithRunner(ScriptedRunner())

            match Repo.OpenWith(sandbox, makeGit, makeJj) with
            | Error e -> Assert.Fail $"OpenWith(git) failed: {e.Message}"
            | Ok repo ->
                Assert.That(repo.Kind, Is.EqualTo BackendKind.Git)
                Assert.That(repo.Root, Is.EqualTo sandbox, "detection anchors the root exactly like Open")
                Assert.That(gitBuilt, Is.EqualTo 1, "the detected backend's factory is invoked exactly once")
                Assert.That(jjBuilt, Is.EqualTo 0, "the unused backend's factory is never invoked")

                match repo.Git with
                | Some g ->
                    Assert.That(obj.ReferenceEquals(g, injected), Is.True, "the handle drives the injected client")
                | None -> Assert.Fail "a git-backed handle must expose its injected Git client")

    [<Test>]
    member _.OpenWithDetectsJjAndUsesOnlyTheInjectedJjClient() =
        withTempDir (fun sandbox ->
            // A valid jj marker requires a `.jj/repo` store (see Detect.isJjMarker).
            Directory.CreateDirectory(Path.Combine(sandbox, ".jj", "repo")) |> ignore
            let injected = Jj.WithRunner(ScriptedRunner())
            let mutable gitBuilt = 0
            let mutable jjBuilt = 0

            let makeGit () =
                gitBuilt <- gitBuilt + 1
                Git.WithRunner(ScriptedRunner())

            let makeJj () =
                jjBuilt <- jjBuilt + 1
                injected

            match Repo.OpenWith(sandbox, makeGit, makeJj) with
            | Error e -> Assert.Fail $"OpenWith(jj) failed: {e.Message}"
            | Ok repo ->
                Assert.That(repo.Kind, Is.EqualTo BackendKind.Jj)
                Assert.That(repo.Root, Is.EqualTo sandbox, "detection anchors the root exactly like Open")
                Assert.That(jjBuilt, Is.EqualTo 1, "the detected backend's factory is invoked exactly once")
                Assert.That(gitBuilt, Is.EqualTo 0, "the unused backend's factory is never invoked")

                match repo.Jj with
                | Some j ->
                    Assert.That(obj.ReferenceEquals(j, injected), Is.True, "the handle drives the injected client")
                | None -> Assert.Fail "a jj-backed handle must expose its injected Jj client")

    [<Test>]
    member _.OpenWithReturnsNotARepositoryWithoutBuildingAnyClient() =
        withTempDir (fun sandbox ->
            let mutable built = 0

            let makeGit () =
                built <- built + 1
                Git.WithRunner(ScriptedRunner())

            let makeJj () =
                built <- built + 1
                Jj.WithRunner(ScriptedRunner())

            match Repo.OpenWith(sandbox, makeGit, makeJj) with
            | Error(RepoError.NotARepository dir) ->
                Assert.That(dir, Is.EqualTo sandbox, "the absolutised start dir is reported, like Open")
                Assert.That(built, Is.EqualTo 0, "no client is built when there is no repository")
            | Error e -> Assert.Fail $"expected NotARepository, got: {e.Message}"
            | Ok _ -> Assert.Fail "no repository exists at the temp dir")

    [<Test>]
    member _.OpenWithReturnsInvalidInputForABadPathWithoutBuildingAnyClient() =
        let invalid = string (char 0)
        let mutable built = 0

        let makeGit () =
            built <- built + 1
            Git.WithRunner(ScriptedRunner())

        let makeJj () =
            built <- built + 1
            Jj.WithRunner(ScriptedRunner())

        match Repo.OpenWith(invalid, makeGit, makeJj) with
        | Error(RepoError.InvalidInput message) ->
            Assert.That(message, Does.Contain "dir", "the diagnostic names the offending parameter, like Open")
            Assert.That(built, Is.EqualTo 0, "a rejected path builds no client")
        | Error e -> Assert.Fail $"expected InvalidInput, got: {e.Message}"
        | Ok _ -> Assert.Fail "an invalid path must be refused"
// ---------------------------------------------------------------------------
// DTOs and the facade error type
// ---------------------------------------------------------------------------

[<TestFixture>]
type TypeTests() =

    [<Test>]
    member _.BackendKindShortNames() =
        Assert.That(BackendKind.Git.AsString, Is.EqualTo "git")
        Assert.That(BackendKind.Jj.AsString, Is.EqualTo "jj")

    [<Test>]
    member _.MergeProbeIsCleanTester() =
        // The compiler-generated case testers distinguish the two outcomes.
        Assert.That(MergeProbe.Clean.IsClean, Is.True)
        Assert.That((MergeProbe.Conflicts [ "a.rs" ]).IsClean, Is.False)

    [<Test>]
    member _.RepoErrorNotFoundClassifier() =
        // A missing-binary Vcs error is `IsNotFound`; the facade's own variants are not.
        let notFound = RepoError.Vcs(ProcessError.NotFound("git", None))
        Assert.That(notFound.IsNotFound, Is.True)
        Assert.That((RepoError.NotARepository "/x").IsNotFound, Is.False)
        Assert.That((RepoError.NotARepository "/x").IsMergeConflict, Is.False)

    [<Test>]
    member _.RepoErrorClassifiersFalseForFacadeVariants() =
        // The facade's own input/io/detection variants are never a vcs/transient/not-found error.
        let invalidInput = RepoError.InvalidInput "bad path"
        let io = RepoError.Io "disk full"
        Assert.That(invalidInput.IsTransient, Is.False)
        Assert.That(invalidInput.IsNotFound, Is.False)
        Assert.That(io.IsTransient, Is.False)
        Assert.That(io.IsTransientFetchError, Is.False)
        Assert.That(io.IsNothingToCommit, Is.False)
        Assert.That(io.IsLockContention, Is.False)
        Assert.That((RepoError.WorktreeNotFound "/wt").IsTransient, Is.False)

    [<Test>]
    member _.RepoErrorMessageIncludesContext() =
        Assert.That((RepoError.NotARepository "/x").Message, Does.Contain "/x")
        Assert.That((RepoError.WorktreeNotFound "/wt").Message, Does.Contain "/wt")
        Assert.That((RepoError.InvalidInput "bad path").Message, Is.EqualTo "bad path")
        Assert.That((RepoError.Io "disk full").Message, Is.EqualTo "disk full")

// ---------------------------------------------------------------------------
// Backend dispatch via a scripted runner (jj + git)
// ---------------------------------------------------------------------------

[<TestFixture>]
type DispatchTests() =

    [<Test>]
    member _.KindAndAccessorsReflectTheBackend() =
        let jj = jjRepo [ "log" ] (Reply.Ok "")
        Assert.That(jj.Kind, Is.EqualTo BackendKind.Jj)
        Assert.That(jj.Jj.IsSome, Is.True)
        Assert.That(jj.Jj.IsSome && jj.Git.IsNone, Is.True, "jj-backed: Git accessor is None")
        Assert.That(jj.Cwd, Is.EqualTo(Path.GetFullPath "/repo"))
        Assert.That(jj.At("/elsewhere").Cwd, Is.EqualTo(Path.GetFullPath "/elsewhere"), "At re-anchors the cwd")

        let git = gitRepo [ "status" ] (Reply.Ok "")
        Assert.That(git.Kind, Is.EqualTo BackendKind.Git)
        Assert.That(git.Git.IsSome && git.Jj.IsNone, Is.True, "git-backed: Jj accessor is None")

    [<Test>]
    member _.JjChangedFilesMapsSummaryToFileChanges() : Task =
        task {
            // `jj diff -r @ --summary` letters map to ChangeKind on the facade FileChange.
            let repo =
                jjRepo [ "diff"; "-r"; "@"; "--summary" ] (Reply.Ok "M a.rs\nA b.rs\nD gone.rs\n")

            match! repo.ChangedFiles() with
            | Ok [ a; b; c ] ->
                Assert.That(a.Path, Is.EqualTo "a.rs")
                Assert.That(a.Kind, Is.EqualTo ChangeKind.Modified)
                Assert.That(b.Kind, Is.EqualTo ChangeKind.Added)
                Assert.That(c.Kind, Is.EqualTo ChangeKind.Deleted)
            | Ok other -> Assert.Fail $"expected three changes, got {other.Length}"
            | Error e -> Assert.Fail $"changed files failed: {e.Message}"
        }

    [<Test>]
    member _.JjCurrentBranchIsNearestReachableBookmark() : Task =
        task {
            // reachable_bookmarks with two equally-near names returns the smallest.
            let repo =
                jjRepo [ "log"; "heads(::@ & bookmarks())" ] (Reply.Ok "main feature\tabc123\n")

            match! repo.CurrentBranch() with
            | Ok(Some name) -> Assert.That(name, Is.EqualTo "feature", "the lexicographically-smallest name")
            | Ok None -> Assert.Fail "expected a branch"
            | Error e -> Assert.Fail $"current branch failed: {e.Message}"
        }

    [<Test>]
    member _.JjHasUncommittedChangesReadsEmptyFlag() : Task =
        task {
            // `jj log` current-change template: changeId, commitId, empty, description.
            let dirty = jjRepo [ "log" ] (Reply.Ok "kztuxlro\t38e00654\tfalse\twip\n")

            match! dirty.HasUncommittedChanges() with
            | Ok v -> Assert.That(v, Is.True, "a non-empty change is dirty")
            | Error e -> Assert.Fail $"has uncommitted failed: {e.Message}"

            // An empty, NON-conflicted change: the fallback probes `is_conflicted` (its own
            // `log -T if(conflict,…)` call), which reports `0` → not dirty.
            let clean =
                Repo.FromJj(
                    "/repo",
                    "/repo",
                    Jj.WithRunner(
                        ScriptedRunner()
                            .On([ "log"; "-T"; "if(conflict, \"1\", \"0\")" ], Reply.Ok "0\n")
                            .On([ "log" ], Reply.Ok "kztuxlro\t38e00654\ttrue\t\n")
                    )
                )

            match! clean.HasUncommittedChanges() with
            | Ok v -> Assert.That(v, Is.False, "an empty, non-conflicted change is clean")
            | Error e -> Assert.Fail $"has uncommitted failed: {e.Message}"

            // An empty but CONFLICTED change is still uncommitted state (needs resolution) — the
            // fallback's `is_conflicted` reports `1`, so `HasUncommittedChanges` is true (M18).
            let conflicted =
                Repo.FromJj(
                    "/repo",
                    "/repo",
                    Jj.WithRunner(
                        ScriptedRunner()
                            .On([ "log"; "-T"; "if(conflict, \"1\", \"0\")" ], Reply.Ok "1\n")
                            .On([ "log" ], Reply.Ok "kztuxlro\t38e00654\ttrue\t\n")
                    )
                )

            match! conflicted.HasUncommittedChanges() with
            | Ok v -> Assert.That(v, Is.True, "an empty but conflicted change is dirty")
            | Error e -> Assert.Fail $"has uncommitted failed: {e.Message}"
        }

    [<Test>]
    member _.GitCurrentBranchReadsSymbolicRef() : Task =
        task {
            let repo =
                gitRepo [ "symbolic-ref"; "--quiet"; "--short"; "HEAD" ] (Reply.Ok "main\n")

            match! repo.CurrentBranch() with
            | Ok(Some name) -> Assert.That(name, Is.EqualTo "main")
            | Ok None -> Assert.Fail "expected a branch"
            | Error e -> Assert.Fail $"current branch failed: {e.Message}"
        }

    [<Test>]
    member _.CommitPathsRefusesAnEmptyPathSet() : Task =
        task {
            // The empty-set guard fires before any spawn, on both backends — a permissive
            // runner would otherwise let a leak through.
            let repo =
                Repo.FromJj("/repo", "/repo", Jj.WithRunner(ScriptedRunner().Fallback(Reply.Ok "")))

            match! repo.CommitPaths([], "msg") with
            | Error(RepoError.InvalidInput message) -> Assert.That(message, Does.Contain "at least one path")
            | Error e -> Assert.Fail $"expected InvalidInput, got: {e.Message}"
            | Ok() -> Assert.Fail "an empty path set must be refused before spawning"
        }

    [<Test>]
    member _.GitLogPathsReturnsUnifiedCommitScopedToPaths() : Task =
        task {
            // A git-backed path-scoped log maps git's typed commit onto the unified `Commit` DTO,
            // author/date filled. The scripted tokens include the literal path, so a match proves
            // the pathspec was passed.
            let us = string (char 0x1f)
            let nul = string (char 0)

            let row = $"abc123{us}abc{us}Ada{us}2026-05-31T10:00:00+00:00{us}Add feature{nul}"

            let repo = gitRepo [ "log"; "HEAD"; "--"; "src/a.fs" ] (Reply.Ok row)

            match! repo.LogPaths("HEAD", 50, [ "src/a.fs" ]) with
            | Ok commits ->
                Assert.That(commits.Length, Is.EqualTo 1)
                Assert.That(commits.[0].Id, Is.EqualTo "abc123")
                Assert.That(commits.[0].Description, Is.EqualTo "Add feature")
                Assert.That(commits.[0].Author, Is.EqualTo(Some "Ada"), "author is filled on git")
                Assert.That(commits.[0].Date, Is.EqualTo(Some "2026-05-31T10:00:00+00:00"), "date is filled on git")
            | Error e -> Assert.Fail $"LogPaths failed: {e.Message}"
        }

    [<Test>]
    member _.JjLogReturnsUnifiedCommitWithNoAuthorOrDate() : Task =
        task {
            // A jj-backed log maps jj's typed change onto the unified `Commit` DTO — author/date are
            // `None` (jj's typed log carries neither).
            let repo =
                jjRepo [ "log"; "-r"; "@" ] (Reply.Ok $"kztuxlro{tab}38e00654{tab}false{tab}feat: stuff\n")

            match! repo.Log("@", 10) with
            | Ok commits ->
                Assert.That(commits.Length, Is.EqualTo 1)
                Assert.That(commits.[0].Id, Is.EqualTo "38e00654", "the id is jj's commit id")
                Assert.That(commits.[0].Description, Is.EqualTo "feat: stuff")
                Assert.That(commits.[0].Author, Is.EqualTo(None: string option), "author is None on jj")
                Assert.That(commits.[0].Date, Is.EqualTo(None: string option), "date is None on jj")
            | Error e -> Assert.Fail $"Log failed: {e.Message}"
        }

    [<Test>]
    member _.JjLogPathsScopesToFilesets() : Task =
        task {
            // A jj-backed path-scoped log converts the plain path to an exact-path `root-file:"…"`
            // fileset (workspace-root-relative); a scripted match on that token proves the conversion.
            let repo =
                jjRepo
                    [ "log"; "-r"; "@"; "root-file:\"src/a.fs\"" ]
                    (Reply.Ok $"kztuxlro{tab}38e00654{tab}false{tab}scoped\n")

            match! repo.LogPaths("@", 10, [ "src/a.fs" ]) with
            | Ok commits ->
                Assert.That(commits.Length, Is.EqualTo 1)
                Assert.That(commits.[0].Id, Is.EqualTo "38e00654")
                Assert.That(commits.[0].Description, Is.EqualTo "scoped")
            | Error e -> Assert.Fail $"LogPaths failed: {e.Message}"
        }

    [<Test>]
    member _.LogPathsRefusesAnEmptyPathSet() : Task =
        task {
            // The empty-set guard fires before any spawn — a path-less scope would degrade to an
            // unrestricted log on both backends, the opposite of "scoped to these paths".
            let repo =
                Repo.FromJj("/repo", "/repo", Jj.WithRunner(ScriptedRunner().Fallback(Reply.Ok "")))

            match! repo.LogPaths("@", 5, []) with
            | Error(RepoError.InvalidInput message) -> Assert.That(message, Does.Contain "at least one path")
            | Error e -> Assert.Fail $"expected InvalidInput, got: {e.Message}"
            | Ok _ -> Assert.Fail "an empty path set must be refused before spawning"
        }

    [<Test>]
    member _.JjTryMergeCleanRollsBack() : Task =
        task {
            // op head → new_merge → is_conflicted(false) → divergence probe → op restore. A
            // clean, rolled-back probe (the probe still shows the captured op, so it restores).
            let runner =
                ScriptedRunner()
                    .On([ "op"; "log"; "--limit"; "1" ], Reply.Ok "opabc\n") // op-head capture
                    .On([ "op"; "log"; "--limit"; "32" ], Reply.Ok(opRow "opabc")) // divergence probe: captured op present
                    .On([ "new" ], Reply.Ok "")
                    .On([ "log"; "-T" ], Reply.Ok "0\n") // is_conflicted template → not conflicted
                    .On([ "op"; "restore"; "opabc" ], Reply.Ok "")

            let repo = Repo.FromJj("/repo", "/repo", Jj.WithRunner runner)

            match! repo.TryMerge "feature" with
            | Ok MergeProbe.Clean -> ()
            | Ok(MergeProbe.Conflicts fs) -> Assert.Fail $"expected Clean, got Conflicts {fs}"
            | Error e -> Assert.Fail $"try merge failed: {e.Message}"
        }

    [<Test>]
    member _.GitShowFileReturnsBlobContentUntrimmed() : Task =
        task {
            // `show <rev>:<path>`; the trailing newline must survive untrimmed.
            let repo = gitRepo [ "show"; "abc123:file.txt" ] (Reply.Ok "hello world\n")

            match! repo.ShowFile("abc123", "file.txt") with
            | Ok content -> Assert.That(content, Is.EqualTo "hello world\n")
            | Error e -> Assert.Fail $"show file failed: {e.Message}"
        }

    [<Test>]
    member _.GitShowFileSurfacesAMissingRevisionAsError() : Task =
        task {
            let repo =
                gitRepo [ "show"; "deadbeef:missing.txt" ] (Reply.Fail(128, "fatal: invalid object name 'deadbeef'"))

            match! repo.ShowFile("deadbeef", "missing.txt") with
            | Error _ -> ()
            | Ok content -> Assert.Fail $"expected an error, got content {content}"
        }

    [<Test>]
    member _.JjShowFileReturnsBlobContentUntrimmed() : Task =
        task {
            // `file show -r <revset> root-file:"<path>"`; the trailing newline must survive untrimmed.
            let repo =
                jjRepo [ "file"; "show"; "-r"; "@"; "root-file:\"file.txt\"" ] (Reply.Ok "hello world\n")

            match! repo.ShowFile("@", "file.txt") with
            | Ok content -> Assert.That(content, Is.EqualTo "hello world\n")
            | Error e -> Assert.Fail $"show file failed: {e.Message}"
        }

    [<Test>]
    member _.JjShowFileSurfacesAMissingPathAsError() : Task =
        task {
            let repo =
                jjRepo [ "file"; "show"; "-r"; "@"; "root-file:\"missing.txt\"" ] (Reply.Fail(1, "Error: No such path"))

            match! repo.ShowFile("@", "missing.txt") with
            | Error _ -> ()
            | Ok content -> Assert.Fail $"expected an error, got content {content}"
        }

    [<Test>]
    member _.GitNewChildIsEquivalentToCheckout() : Task =
        task {
            // On git, NewChild is exactly `checkout <reference> --` — the next commit
            // naturally appends on top, so there is no separate "new child" primitive.
            let repo = gitRepo [ "checkout"; "feat"; "--" ] (Reply.Ok "")

            match! repo.NewChild "feat" with
            | Ok() -> ()
            | Error e -> Assert.Fail $"new child failed: {e.Message}"
        }

    [<Test>]
    member _.JjNewChildRunsNewNotEdit() : Task =
        task {
            // On jj, NewChild maps to `jj new <reference>` — a fresh undescribed child
            // change stacked on `reference`, NOT `jj edit` (which would rewrite it).
            let repo = jjRepo [ "new"; "feat" ] (Reply.Ok "")

            match! repo.NewChild "feat" with
            | Ok() -> ()
            | Error e -> Assert.Fail $"new child failed: {e.Message}"
        }

// ---------------------------------------------------------------------------
// The intricate assemblies: snapshot, tryMerge outcomes/rollback, worktrees
// ---------------------------------------------------------------------------

[<TestFixture>]
type AssemblyTests() =

    [<Test>]
    member _.JjSnapshotAssemblesDirtyState() : Task =
        task {
            // template (@ head/empty/conflict) → reachable bookmark → change count (dirty).
            // The count spawn resolves the workspace root first (`Jj.Status`), hence `root`.
            let runner =
                ScriptedRunner()
                    .On([ "log"; "-r"; "@"; "--limit"; "1" ], Reply.Ok "abc123def\t0\t0\n") // empty="0" ⇒ dirty
                    .On([ "log"; "heads(::@ & bookmarks())" ], Reply.Ok "main\txyz\n")
                    .On([ "root" ], Reply.Ok "/repo\n")
                    .On([ "diff"; "-r"; "@"; "--summary" ], Reply.Ok "M a.rs\nA b.rs\n")

            let repo = Repo.FromJj("/repo", "/repo", Jj.WithRunner runner)

            match! repo.Snapshot() with
            | Ok s ->
                Assert.That(s.Head, Is.EqualTo(Some "abc123def"))
                Assert.That(s.Branch, Is.EqualTo(Some "main"))
                Assert.That(s.Dirty, Is.True)
                Assert.That(s.ChangeCount, Is.EqualTo 2UL)
                Assert.That(s.Conflicted, Is.False)
                Assert.That(s.Tracking.IsNone, Is.True, "jj has no git-style upstream tracking")
                Assert.That(s.Operation, Is.EqualTo OperationState.Clear)
            | Error e -> Assert.Fail $"snapshot failed: {e.Message}"
        }

    [<Test>]
    member _.JjSnapshotCleanSkipsTheCountSpawn() : Task =
        task {
            // An empty change is clean → the change-count spawn is skipped. No `diff
            // --summary` rule is scripted, so if snapshot wrongly fetched the count the
            // ScriptedRunner would raise.
            let runner =
                ScriptedRunner()
                    .On([ "log"; "-r"; "@"; "--limit"; "1" ], Reply.Ok "abc123\t1\t0\n") // empty="1" ⇒ clean
                    .On([ "log"; "heads(::@ & bookmarks())" ], Reply.Ok "\n") // no bookmark

            let repo = Repo.FromJj("/repo", "/repo", Jj.WithRunner runner)

            match! repo.Snapshot() with
            | Ok s ->
                Assert.That(s.Dirty, Is.False)
                Assert.That(s.ChangeCount, Is.EqualTo 0UL)
                Assert.That(s.Branch.IsNone, Is.True)
            | Error e -> Assert.Fail $"snapshot failed: {e.Message}"
        }

    [<Test>]
    member _.JjTryMergeReportsConflicts() : Task =
        task {
            // is_conflicted(true) → jj file list (conflicted-paths template) → MergeProbe.Conflicts,
            // still rolled back.
            let runner =
                ScriptedRunner()
                    .On([ "op"; "log"; "--limit"; "1" ], Reply.Ok "opabc\n") // op-head capture
                    .On([ "op"; "log"; "--limit"; "32" ], Reply.Ok(opRow "opabc")) // divergence probe: captured op present
                    .On([ "new" ], Reply.Ok "")
                    .On([ "log"; "-T" ], Reply.Ok "1\n") // conflicted
                    .On([ "file"; "list" ], Reply.Ok "\"a.rs\"\n")
                    .On([ "op"; "restore"; "opabc" ], Reply.Ok "")

            let repo = Repo.FromJj("/repo", "/repo", Jj.WithRunner runner)

            match! repo.TryMerge "feature" with
            | Ok(MergeProbe.Conflicts [ "a.rs" ]) -> ()
            | Ok other -> Assert.Fail $"expected Conflicts [a.rs], got {other}"
            | Error e -> Assert.Fail $"try merge failed: {e.Message}"
        }

    [<Test>]
    member _.JjTryMergeFailedRollbackSurfacesError() : Task =
        task {
            // A clean probe whose `op restore` FAILS must surface as Error — a `Clean` with
            // the probe change still present would lie about the tree.
            let runner =
                ScriptedRunner()
                    .On([ "op"; "log"; "--limit"; "1" ], Reply.Ok "opabc\n") // op-head capture
                    .On([ "op"; "log"; "--limit"; "32" ], Reply.Ok(opRow "opabc")) // divergence probe: captured op present
                    .On([ "new" ], Reply.Ok "")
                    .On([ "log"; "-T" ], Reply.Ok "0\n")
                    .On([ "op"; "restore"; "opabc" ], Reply.Fail(1, "restore failed"))

            let repo = Repo.FromJj("/repo", "/repo", Jj.WithRunner runner)

            let! r = repo.TryMerge "feature"
            Assert.That(Result.isError r, Is.True, "a failed rollback must surface as an error, not Clean")
        }

    [<Test>]
    member _.JjTryMergeComposesMergeAndRollbackFailures() : Task =
        task {
            let runner =
                ScriptedRunner()
                    .On([ "op"; "log"; "--limit"; "1" ], Reply.Ok "opabc\n") // op-head capture
                    .On([ "op"; "log"; "--limit"; "32" ], Reply.Ok(opRow "opabc")) // rollback divergence probe
                    .On([ "new" ], Reply.Fail(1, "merge failed"))
                    .On([ "log"; "-T" ], Reply.Ok "0\n")
                    .On([ "op"; "restore"; "opabc" ], Reply.Fail(1, "rollback failed"))

            let repo = Repo.FromJj("/repo", "/repo", Jj.WithRunner runner)

            match! repo.TryMerge "feature" with
            | Error e ->
                Assert.That(e.Message, Does.Contain "merge failed")
                Assert.That(e.Message, Does.Contain "rollback failed")
            | Ok result -> Assert.Fail $"expected composed merge/rollback error, got {result}"
        }

    [<Test>]
    member _.JjTryMergeRefusesRollbackOnDivergence() : Task =
        task {
            // A concurrent operation advanced the op-log past the captured op between capture
            // and rollback, so the probe no longer sees it. The rollback must be refused (no
            // `op restore` scripted — one would raise), surfacing as an error rather than
            // clobbering the concurrent work with a blind restore.
            let runner =
                ScriptedRunner()
                    .On([ "op"; "log"; "--limit"; "1" ], Reply.Ok "opabc\n") // op-head capture
                    .On([ "op"; "log"; "--limit"; "32" ], Reply.Ok(opRow "opNEW")) // probe: captured op gone → diverged
                    .On([ "new" ], Reply.Ok "")
                    .On([ "log"; "-T" ], Reply.Ok "0\n")

            let repo = Repo.FromJj("/repo", "/repo", Jj.WithRunner runner)

            let! r = repo.TryMerge "feature"
            Assert.That(Result.isError r, Is.True, "a diverged rollback must surface as an error, not Clean")
        }

    [<Test>]
    member _.GitTryMergeCleanupRunsOnFreshBudgetDespiteCancelledToken() =
        // Mirrors `RollbackToRunsCleanupOnFreshBudgetDespiteCancelledToken` (VcsToolkit.Jj) for
        // the git backend (T-032). The ambient client's cancellation token is already fired
        // BEFORE `TryMerge` is even called, so the probe merge itself (`git merge --no-commit
        // --no-ff`) — which is NOT detached, and inherits the token like any other mutation —
        // surfaces as an error. The three tryMerge cleanup branches must still run their
        // merge-in-progress probe + `merge --abort` on their OWN fresh, live budget
        // (`Git.IsMergeInProgressDetached`/`MergeAbortDetached`): if the cleanup instead
        // inherited the already-cancelled ambient token, `rev-parse --git-dir`/`merge --abort`
        // would error as Cancelled before ever matching a scripted rule (the ScriptedRunner
        // cancellation contract) and `merge --abort` would never actually run — leaving the
        // probe merge staged. Reaching (and running) `merge --abort` proves the detached budget.
        withTempDir (fun dir ->
            let gitDir = Path.Combine(dir, ".git")
            Directory.CreateDirectory gitDir |> ignore
            // MERGE_HEAD present ⇒ the cleanup's IsMergeInProgressDetached probe reads `true`,
            // so it proceeds to MergeAbortDetached.
            File.WriteAllText(Path.Combine(gitDir, "MERGE_HEAD"), "x\n")

            use cts = new System.Threading.CancellationTokenSource()
            cts.Cancel()

            let abortRan = ref false
            let mergeHeadPath = Path.Combine(gitDir, "MERGE_HEAD")

            let runner =
                ScriptedRunner()
                    .On([ "rev-parse"; "--git-dir" ], Reply.Ok(gitDir + "\n"))
                    .When(
                        (fun (cmd: Command) ->
                            if cmd.Arguments |> Seq.contains "--abort" then
                                abortRan.Value <- true
                                // Mirror real `git merge --abort`: it clears MERGE_HEAD from the
                                // repo, so the scripted reply must too — otherwise the test could
                                // pass on a regression that invokes `--abort` without the cleanup
                                // actually taking effect.
                                File.Delete mergeHeadPath
                                true
                            else
                                false),
                        Reply.Ok ""
                    )

            let git = (Git.WithRunner runner).DefaultCancelOn cts.Token
            let repo = Repo.FromGit(dir, dir, git)

            let r = repo.TryMerge("feature").GetAwaiter().GetResult()

            Assert.That(Result.isError r, Is.True, "the ambient-cancelled probe merge itself must surface as an error")

            Assert.That(
                abortRan.Value,
                Is.True,
                "merge --abort must still run on a fresh cancellation budget, not the ambient cancelled token"
            )

            Assert.That(
                File.Exists mergeHeadPath,
                Is.False,
                "MERGE_HEAD must be removed by cleanup — a scripted abort that doesn't actually clear it would let a real regression pass this test"
            ))

    [<Test>]
    member _.GitTryMergeComposesMergeAndRollbackFailures() =
        withTempDir (fun dir ->
            let gitDir = Path.Combine(dir, ".git")
            Directory.CreateDirectory gitDir |> ignore
            File.WriteAllText(Path.Combine(gitDir, "MERGE_HEAD"), "x\n")

            let runner =
                ScriptedRunner()
                    .On([ "merge"; "--no-commit"; "--no-ff"; "feature" ], Reply.Fail(1, "merge failed"))
                    .On([ "rev-parse"; "--git-dir" ], Reply.Ok(gitDir + "\n"))
                    .On([ "merge"; "--abort" ], Reply.Fail(1, "abort failed"))

            let repo = Repo.FromGit(dir, dir, Git.WithRunner runner)

            match repo.TryMerge("feature").GetAwaiter().GetResult() with
            | Error e ->
                Assert.That(e.Message, Does.Contain "merge failed")
                Assert.That(e.Message, Does.Contain "abort failed")
            | Ok result -> Assert.Fail $"expected composed merge/rollback error, got {result}")

    [<Test>]
    member _.GitTryMergeProbeMasksFailureAfterMergeFailure() =
        let runner =
            ScriptedRunner()
                .On([ "merge"; "--no-commit"; "--no-ff"; "feature" ], Reply.Fail(1, "merge failed"))
                .On([ "rev-parse"; "--git-dir" ], Reply.Fail(1, "git dir probe failed"))

        let repo = Repo.FromGit("/repo", "/repo", Git.WithRunner runner)

        match repo.TryMerge("feature").GetAwaiter().GetResult() with
        | Error e ->
            Assert.That(e.Message, Does.Contain "merge failed")
            Assert.That(e.Message, Does.Contain "git dir probe failed")
        | Ok result -> Assert.Fail $"expected composed merge/probe error, got {result}"

    [<Test>]
    member _.JjListWorktreesResolvesRootsAndBookmarks() : Task =
        task {
            // workspace list (name/commit/bookmarks) + a `workspace root` fan-out per name.
            let runner =
                ScriptedRunner()
                    .On([ "workspace"; "list" ], Reply.Ok "default\te2aa3420\tmain\nws1\t12345678\t\n")
                    .On([ "workspace"; "root"; "--name"; "default" ], Reply.Ok "/repo\n")
                    .On([ "workspace"; "root"; "--name"; "ws1" ], Reply.Ok "/repo/ws1\n")

            let repo = Repo.FromJj("/repo", "/repo", Jj.WithRunner runner)

            match! repo.ListWorktrees() with
            | Ok [ w0; w1 ] ->
                Assert.That(w0.Path, Is.EqualTo "/repo")
                Assert.That(w0.Branch, Is.EqualTo(Some "main"), "first bookmark becomes the branch")
                Assert.That(w0.Commit, Is.EqualTo(Some "e2aa3420"))
                Assert.That(w0.IsBare, Is.False)
                Assert.That(w1.Path, Is.EqualTo "/repo/ws1")
                Assert.That(w1.Branch, Is.EqualTo None, "no bookmark → None")
            | Ok other -> Assert.Fail $"expected two worktrees, got {other.Length}"
            | Error e -> Assert.Fail $"list worktrees failed: {e.Message}"
        }

    [<Test>]
    member _.JjHeadAndWorktreeCommitAreTheSameFullId() : Task =
        task {
            // For one commit, `RepoSnapshot.Head` and `WorktreeInfo.Commit` are the SAME
            // full commit id: the WORKSPACE_TEMPLATE now renders `target.commit_id()` (full,
            // not `.short()`), matching the snapshot template's full head, so the two
            // identities compare directly instead of a full-vs-short mismatch (T-014).
            let full = "abcdef0123456789abcdef0123456789abcdef01" // 40 hex chars

            let runner =
                ScriptedRunner()
                    // Snapshot spawn 1: head/empty/conflict for `@` (empty="1" ⇒ clean, so
                    // the change-count spawn is skipped).
                    .On([ "log"; "-r"; "@"; "--limit"; "1" ], Reply.Ok $"{full}\t1\t0\n")
                    // Snapshot spawn 2: the nearest reachable bookmark → branch.
                    .On([ "log"; "heads(::@ & bookmarks())" ], Reply.Ok "main\txyz\n")
                    // ListWorktrees: one workspace on that SAME full commit, then its root.
                    .On([ "workspace"; "list" ], Reply.Ok $"\"default\"\t{full}\t\"main\"\n")
                    .On([ "workspace"; "root"; "--name"; "default" ], Reply.Ok "/repo\n")

            let repo = Repo.FromJj("/repo", "/repo", Jj.WithRunner runner)

            let! snap = repo.Snapshot()
            let! worktrees = repo.ListWorktrees()

            match snap, worktrees with
            | Ok s, Ok [ w0 ] ->
                Assert.That(s.Head, Is.EqualTo(Some full))
                Assert.That(w0.Commit, Is.EqualTo(Some full))
                Assert.That(s.Head, Is.EqualTo w0.Commit, "Head and WorktreeInfo.Commit are one identity")
                Assert.That(full.Length, Is.EqualTo 40, "a full commit id, not a short prefix")
            | _ -> Assert.Fail $"snapshot={snap}, worktrees={worktrees}"
        }

    [<Test>]
    member _.JjRemoveWorktreeRefusesMainWorkspace() : Task =
        task {
            // Removing the repository's MAIN (default) workspace must be REFUSED — its directory
            // IS the whole checkout, and the facade deletes the dir itself, so removing it would
            // wipe `.jj`/`.git` and every file. Resolve "." to the "default" workspace; the guard
            // must fire before any deletion.
            let runner =
                ScriptedRunner()
                    .On([ "workspace"; "list" ], Reply.Ok "default\te2aa3420\tmain\n")
                    .On([ "workspace"; "root"; "--name"; "default" ], Reply.Ok "/repo\n")

            let repo = Repo.FromJj("/repo", "/repo", Jj.WithRunner runner)

            match! repo.RemoveWorktree(".", false) with
            | Error(RepoError.InvalidInput message) ->
                Assert.That(message, Does.Contain "main workspace", "the guard must name the reason")
            | Error e -> Assert.Fail $"expected InvalidInput, got: {e.Message}"
            | Ok() -> Assert.Fail "removing the main (default) workspace must be refused"
        }

    [<Test>]
    member _.JjWorkspaceProbeFailureIsNotMisreportedAsWorktreeNotFound() : Task =
        task {
            // Two registered workspaces; "default"'s root resolves (but doesn't match the
            // queried path) while "ws1"'s `workspace root --name` probe itself FAILS. The
            // queried path matches neither, so a naive fold would report the same
            // `WorktreeNotFound` as a genuine miss — masking that jj couldn't even resolve
            // "ws1". The facade must surface a distinct, diagnosable error naming "ws1"
            // instead.
            let runner =
                ScriptedRunner()
                    .On([ "workspace"; "list" ], Reply.Ok "default\te2aa3420\tmain\nws1\t12345678\t\n")
                    .On([ "workspace"; "root"; "--name"; "default" ], Reply.Ok "/repo\n")
                    .On([ "workspace"; "root"; "--name"; "ws1" ], Reply.Fail(1, "internal error: object not found"))

            let repo = Repo.FromJj("/repo", "/repo", Jj.WithRunner runner)

            match! repo.RemoveWorktree("/some/other/path", false) with
            | Ok() -> Assert.Fail "expected the probe failure to surface as an error"
            | Error(RepoError.WorktreeNotFound _) ->
                Assert.Fail
                    "a failed workspace-root probe must not collapse into the same result as a genuine not-found miss"
            | Error e -> Assert.That(e.Message, Does.Contain "ws1", "the diagnostic must name the unresolved workspace")
        }

    [<Test>]
    member _.JjCreateWorktreeRollbackSurfacesForgetFailureAlongsideBookmarkError() : Task =
        task {
            // `WorkspaceAdd` succeeds, `BookmarkCreate` fails, and the rollback's
            // `WorkspaceForget` ALSO fails. Both reasons must reach the caller — the
            // original `BookmarkCreate` failure must not be lost, and the failed rollback
            // must not be silently swallowed (the pre-fix code discarded the `WorkspaceForget`
            // result entirely via `let! _ = ...`).
            let runner =
                ScriptedRunner()
                    .On(
                        [ "workspace"
                          "add"
                          "--name"
                          "feature"
                          "-r"
                          "main"
                          "wt"
                          "--color"
                          "never" ],
                        Reply.Ok ""
                    )
                    .On([ "bookmark"; "create"; "feature"; "-r"; "feature@" ], Reply.Fail(1, "bookmark already exists"))
                    .On([ "workspace"; "forget"; "feature" ], Reply.Fail(1, "no such workspace: feature"))

            let repo = Repo.FromJj("/repo", "/repo", Jj.WithRunner runner)

            match! repo.CreateWorktree("wt", "feature", "main") with
            | Ok _ -> Assert.Fail "expected the BookmarkCreate failure to surface"
            | Error e ->
                Assert.That(
                    e.Message,
                    Does.Contain "bookmark already exists",
                    "the original BookmarkCreate failure must not be lost"
                )

                Assert.That(
                    e.Message,
                    Does.Contain "no such workspace",
                    "the failed rollback WorkspaceForget must be surfaced, not swallowed"
                )
        }

    [<Test>]
    member _.RepoGitAtAndJjAtMatchTheBackend() : Task =
        task {
            // `GitAt` is `Some` on a git repo (`JjAt` `None`); `JjAt` is the mirror on a jj repo.
            // The returned view forwards to the repo's cwd (dir dropped).
            let git = gitRepo [ "status"; "--porcelain=v1"; "-z" ] (Reply.Ok "")
            Assert.That(Option.isSome git.GitAt, "a git repo exposes GitAt")
            Assert.That(Option.isNone git.JjAt, "a git repo has no JjAt")

            match! git.GitAt.Value.Status() with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"GitAt.Status failed: {e.Message}"

            let jj = jjRepo [ "diff" ] (Reply.Ok "")
            Assert.That(Option.isSome jj.JjAt, "a jj repo exposes JjAt")
            Assert.That(Option.isNone jj.GitAt, "a jj repo has no GitAt")
        }

// ---------------------------------------------------------------------------
// git sequencer states — cherry-pick / revert / bisect detection + routing
// ---------------------------------------------------------------------------

[<TestFixture>]
type GitSequencerStateTests() =

    // A git repo bound to `dir` whose `rev-parse --git-dir` echoes `dir/.git` (created on
    // disk so the on-disk marker probes see it), with each requested command scripted and any
    // other command failing — so a wrong sequencer command dispatch surfaces as an error.
    let repoWithGitDir (dir: string) (rules: (string list * Reply) list) =
        let gitDir = Path.Combine(dir, ".git")
        Directory.CreateDirectory gitDir |> ignore

        let runner =
            (ScriptedRunner().On([ "rev-parse"; "--git-dir" ], Reply.Ok(gitDir + "\n")), rules)
            ||> List.fold (fun (r: ScriptedRunner) (tokens, reply) -> r.On(tokens, reply))

        gitDir, Repo.FromGit(dir, dir, Git.WithRunner(runner.Fallback(Reply.Fail(1, "unexpected command dispatched"))))

    [<Test>]
    member _.InProgressStateDetectsEachSequencerMarker() =
        // The facade maps each git-dir marker to its OperationState via the shared detection
        // precedence; a cherry-pick/revert marker must not be mis-read as a merge.
        withTempDir (fun dir ->
            let gitDir, repo = repoWithGitDir dir []

            let stateOf () =
                match repo.InProgressState().GetAwaiter().GetResult() with
                | Ok s -> s
                | Error e -> failwithf "InProgressState failed: %s" e.Message

            let touch name =
                File.WriteAllText(Path.Combine(gitDir, name), "x\n")

            let rm name = File.Delete(Path.Combine(gitDir, name))

            touch "CHERRY_PICK_HEAD"
            Assert.That(stateOf (), Is.EqualTo OperationState.CherryPick)
            rm "CHERRY_PICK_HEAD"

            touch "REVERT_HEAD"
            Assert.That(stateOf (), Is.EqualTo OperationState.Revert)
            rm "REVERT_HEAD"

            touch "BISECT_LOG"
            Assert.That(stateOf (), Is.EqualTo OperationState.Bisect)
            rm "BISECT_LOG"

            Assert.That(stateOf (), Is.EqualTo OperationState.Clear, "a clean git dir is Clear"))

    [<Test>]
    member _.InProgressStatePreservesMarkerPriority() =
        // Multiple markers should not occur in a healthy repository, but preserve the established
        // order if stale state does leave more than one behind: merge, am, rebase, cherry-pick,
        // revert, then bisect.
        withTempDir (fun dir ->
            let gitDir, repo = repoWithGitDir dir []

            let stateOf () =
                match repo.InProgressState().GetAwaiter().GetResult() with
                | Ok state -> state
                | Error e -> failwithf "InProgressState failed: %s" e.Message

            let touch name =
                File.WriteAllText(Path.Combine(gitDir, name), "x\n")

            let rebaseApply = Path.Combine(gitDir, "rebase-apply")

            touch "BISECT_LOG"
            Assert.That(stateOf (), Is.EqualTo OperationState.Bisect)

            touch "REVERT_HEAD"
            Assert.That(stateOf (), Is.EqualTo OperationState.Revert)

            touch "CHERRY_PICK_HEAD"
            Assert.That(stateOf (), Is.EqualTo OperationState.CherryPick)

            Directory.CreateDirectory(Path.Combine(gitDir, "rebase-merge")) |> ignore
            Assert.That(stateOf (), Is.EqualTo OperationState.Rebase)

            Directory.CreateDirectory rebaseApply |> ignore
            touch (Path.Combine("rebase-apply", "applying"))
            Assert.That(stateOf (), Is.EqualTo OperationState.ApplyMailbox)

            touch "MERGE_HEAD"
            Assert.That(stateOf (), Is.EqualTo OperationState.Merge))

    [<Test>]
    member _.AbortDuringCherryPickDispatchesCherryPickAbort() =
        // A cherry-pick abort must route `cherry-pick --abort` — the scripted runner fails any
        // other command, so a wrong route (e.g. `merge --abort`) surfaces as an error.
        withTempDir (fun dir ->
            let gitDir, repo = repoWithGitDir dir [ [ "cherry-pick"; "--abort" ], Reply.Ok "" ]

            File.WriteAllText(Path.Combine(gitDir, "CHERRY_PICK_HEAD"), "x\n")

            match repo.AbortInProgress().GetAwaiter().GetResult() with
            | Ok _ -> () // reached only if `cherry-pick --abort` was the dispatched command
            | Error e -> Assert.Fail $"abort must dispatch cherry-pick --abort: {e.Message}")

    [<Test>]
    member _.ContinueDuringRevertDispatchesRevertContinue() =
        // A revert continue must route `revert --continue`, not `rebase --continue`.
        withTempDir (fun dir ->
            let gitDir, repo =
                repoWithGitDir
                    dir
                    [ [ "diff"; "--name-only"; "--diff-filter=U"; "-z" ], Reply.Ok "" // no unresolved paths
                      [ "revert"; "--continue" ], Reply.Ok "" ]

            File.WriteAllText(Path.Combine(gitDir, "REVERT_HEAD"), "x\n")

            match repo.ContinueInProgress().GetAwaiter().GetResult() with
            | Ok _ -> () // reached only if `revert --continue` was the dispatched command
            | Error e -> Assert.Fail $"continue must dispatch revert --continue: {e.Message}")

    [<Test>]
    member _.ContinueDuringBisectIsUnsupportedAndRunsNoMutation() =
        // A bisect has no continue step: ContinueInProgress must refuse it with
        // RepoError.Unsupported (not silently report it still in progress), and no git mutation
        // may run — only the conflict probe + git-dir resolution the detection needs. The
        // Fallback fails any other command, so a stray mutation would surface as an error too.
        withTempDir (fun dir ->
            let gitDir, repo =
                repoWithGitDir dir [ [ "diff"; "--name-only"; "--diff-filter=U"; "-z" ], Reply.Ok "" ]

            File.WriteAllText(Path.Combine(gitDir, "BISECT_LOG"), "x\n")

            match repo.ContinueInProgress().GetAwaiter().GetResult() with
            | Error e ->
                Assert.That(e.IsUnsupported, Is.True, $"expected Unsupported, got {e.Message}")
                Assert.That(e.Message, Does.Contain "bisect", "the message must name the reason")
            | Ok state -> Assert.Fail $"bisect continue must be refused, got {state}")

// ---------------------------------------------------------------------------
// GitBackend.diffStat on an unborn repo — real git, both object formats (T-037)
// ---------------------------------------------------------------------------

[<TestFixture>]
type GitBackendDiffStatUnbornTests() =

    let requireGit () =
        try
            Raw.git "." [ "--version" ]
        with _ ->
            // git isn't on PATH (or failed to spawn) — a hermetic CI without it must skip,
            // not fail, this fixture.
            Assert.Ignore "git not available on PATH"

    /// A SHA-256 sandbox, or skip: not every git distribution is built with SHA-256
    /// object-format support, so a fixture that needs one must not hard-fail on those.
    let sha256Sandbox (tag: string) : GitSandbox =
        try
            GitSandbox.InitSha256 tag
        with _ ->
            Assert.Ignore "this git build does not support --object-format=sha256"
            failwith "unreachable: Assert.Ignore always throws"

    [<Test>]
    member _.Sha1UnbornDiffStatStillWorks() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "diffstat-unborn-sha1"
            repo.Write("a.txt", "one\n")
            repo.AddAll()

            let facade = Repo.FromGit(repo.Path, repo.Path, Git.Create())

            match! facade.DiffStat() with
            | Ok stat -> Assert.That(stat.FilesChanged, Is.EqualTo 1UL, "the unborn working tree reports the new file")
            | Error e -> Assert.Fail $"DiffStat failed: {e.Message}"
        }

    /// The decisive check (T-037): a SHA-256 unborn repo has no `4b825dc…` object at all,
    /// so if `diffStat` still used the hardcoded SHA-1 literal, `git diff <that sha>` would
    /// fail outright (`fatal: bad object` / `fatal: ambiguous argument`) instead of reporting
    /// additions.
    [<Test>]
    member _.Sha256UnbornDiffStatReportsAdditionsInsteadOfError() : Task =
        task {
            requireGit ()
            use repo = sha256Sandbox "diffstat-unborn-sha256"
            repo.Write("a.txt", "one\n")
            repo.AddAll()

            let facade = Repo.FromGit(repo.Path, repo.Path, Git.Create())

            match! facade.DiffStat() with
            | Ok stat -> Assert.That(stat.FilesChanged, Is.EqualTo 1UL, "the unborn working tree reports the new file")
            | Error e -> Assert.Fail $"DiffStat failed on a SHA-256 unborn repo: {e.Message}"
        }

// ---------------------------------------------------------------------------
// T-048: repo-root path anchoring for CommitPaths/LogPaths on a subdirectory-
// bound handle (Cwd ≠ Root) — the input-path counterpart of the root-relative
// output paths ChangedFiles/Status already return.
// ---------------------------------------------------------------------------

[<TestFixture>]
type PathAnchoringTests() =

    // A runner recording the last Command it ran (argv + working directory), always replying
    // `reply` — for asserting which directory a path-op's git command runs from.
    let capturing (reply: Reply) : (Command option ref) * ScriptedRunner =
        let captured = ref (None: Command option)

        let runner =
            ScriptedRunner()
                .When(
                    (fun (cmd: Command) ->
                        captured.Value <- Some cmd
                        true),
                    reply
                )

        captured, runner

    let requireGit () =
        try
            Raw.git "." [ "--version" ]
        with _ ->
            // git isn't on PATH — a hermetic CI without it must skip, not fail, this fixture.
            Assert.Ignore "git not available on PATH"

    let requireJj () =
        try
            Raw.jj "." [ "--version" ]
        with _ ->
            // jj isn't on PATH — skip rather than fail.
            Assert.Ignore "jj not available on PATH"

    // --- git: the pathspec command must run from Root, not the subdir Cwd (mock) ------------

    [<Test>]
    member _.GitCommitPathsRunsFromRepoRootNotTheSubdirectoryCwd() : Task =
        task {
            // A handle bound to a SUBDIRECTORY (Cwd ≠ Root) must resolve its repo-relative
            // pathspecs against the repo Root: `git commit --only` runs from Root, so the pathspec
            // anchors there rather than at the subdir Cwd (which would look for Root/sub/<path>).
            let captured, runner = capturing (Reply.Ok "")
            let repo = Repo.FromGit("/repo", "/repo/sub", Git.WithRunner runner)

            match! repo.CommitPaths([ "src/a.fs" ], "msg") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"CommitPaths failed: {e.Message}"

            match captured.Value with
            | Some cmd ->
                Assert.That(
                    cmd.WorkingDirectory,
                    Is.EqualTo(Some(Path.GetFullPath "/repo")),
                    "commit runs from Root, not the subdirectory Cwd"
                )

                Assert.That(
                    String.concat " " cmd.Arguments,
                    Is.EqualTo "--literal-pathspecs commit -m msg --only -- src/a.fs",
                    "the repo-relative path is forwarded verbatim as the pathspec"
                )
            | None -> Assert.Fail "no command captured"
        }

    [<Test>]
    member _.GitLogPathsRunsFromRepoRootNotTheSubdirectoryCwd() : Task =
        task {
            let captured, runner = capturing (Reply.Ok "")
            let repo = Repo.FromGit("/repo", "/repo/sub", Git.WithRunner runner)

            match! repo.LogPaths("HEAD", 50, [ "src/a.fs" ]) with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"LogPaths failed: {e.Message}"

            match captured.Value with
            | Some cmd ->
                Assert.That(
                    cmd.WorkingDirectory,
                    Is.EqualTo(Some(Path.GetFullPath "/repo")),
                    "log runs from Root, not the subdirectory Cwd"
                )
            | None -> Assert.Fail "no command captured"
        }

    // --- git: real-`git` end-to-end from a subdirectory-bound handle ------------------------

    [<Test>]
    member _.GitCommitPathsFromASubdirectoryHandleCommitsTheRootRelativeFile() : Task =
        task {
            requireGit ()
            use sandbox = GitSandbox.Init "t048-git-subdir"
            // Seed two files under a subdirectory, then dirty both.
            sandbox.Write("sub/a.txt", "a1\n")
            sandbox.Write("sub/b.txt", "b1\n")
            sandbox.AddAll()
            sandbox.Commit "seed"
            sandbox.Write("sub/a.txt", "a2\n")
            sandbox.Write("sub/b.txt", "b2\n")

            // Open the handle IN the subdirectory (Cwd = sub, Root = repo root).
            match Repo.Open(Path.Combine(sandbox.Path, "sub")) with
            | Error e -> Assert.Fail $"Repo.Open(subdir) failed: {e.Message}"
            | Ok repo ->
                Assert.That(repo.Kind, Is.EqualTo BackendKind.Git)
                Assert.That(repo.Cwd, Is.Not.EqualTo repo.Root, "the handle is bound to the subdirectory")

                // Commit ONLY sub/a.txt via its repo-root-relative path. A subdir-relative
                // resolution would look for sub/sub/a.txt and error / commit nothing.
                match! repo.CommitPaths([ "sub/a.txt" ], "commit a only") with
                | Error e -> Assert.Fail $"CommitPaths from a subdirectory handle failed: {e.Message}"
                | Ok() ->
                    match! repo.ChangedFiles() with
                    | Error e -> Assert.Fail $"ChangedFiles failed: {e.Message}"
                    | Ok changes ->
                        let paths = changes |> List.map (fun c -> c.Path)
                        Assert.That(paths, Does.Not.Contain "sub/a.txt", "the committed file must now be clean")
                        Assert.That(paths, Does.Contain "sub/b.txt", "the uncommitted sibling's edit must remain")
        }

    [<Test>]
    member _.GitLogPathsFromASubdirectoryHandleScopesToTheRootRelativeFile() : Task =
        task {
            requireGit ()
            use sandbox = GitSandbox.Init "t048-git-subdir-log"
            sandbox.CommitFile("sub/a.txt", "a1\n", "touch a")
            sandbox.CommitFile("sub/b.txt", "b1\n", "touch b")

            match Repo.Open(Path.Combine(sandbox.Path, "sub")) with
            | Error e -> Assert.Fail $"Repo.Open(subdir) failed: {e.Message}"
            | Ok repo ->
                // Scope history to sub/a.txt via its repo-root-relative path: only the "touch a"
                // commit qualifies. A subdir-relative pathspec (sub/sub/a.txt) would match nothing.
                match! repo.LogPaths("HEAD", 50, [ "sub/a.txt" ]) with
                | Error e -> Assert.Fail $"LogPaths from a subdirectory handle failed: {e.Message}"
                | Ok commits ->
                    let subjects = commits |> List.map (fun c -> c.Description)
                    Assert.That(subjects, Does.Contain "touch a", "the commit touching sub/a.txt must be found")
                    Assert.That(subjects, Does.Not.Contain "touch b", "an unrelated commit must be excluded")
        }

    // --- jj: real-`jj` end-to-end from a subdirectory-bound handle --------------------------

    [<Test>]
    member _.JjCommitPathsFromASubdirectoryHandleCommitsTheRootRelativeFile() : Task =
        task {
            requireJj ()
            use sandbox = JjSandbox.Init "t048-jj-subdir"
            // Two files under a subdirectory in the working-copy change `@` (jj auto-tracks).
            sandbox.Write("sub/a.txt", "a1\n")
            sandbox.Write("sub/b.txt", "b1\n")

            // Open the handle IN the subdirectory (Cwd = sub, Root = workspace root).
            match Repo.Open(Path.Combine(sandbox.Path, "sub")) with
            | Error e -> Assert.Fail $"Repo.Open(subdir) failed: {e.Message}"
            | Ok repo ->
                Assert.That(repo.Kind, Is.EqualTo BackendKind.Jj)
                Assert.That(repo.Cwd, Is.Not.EqualTo repo.Root, "the handle is bound to the subdirectory")

                // Finalise ONLY sub/a.txt via its workspace-root-relative path. A cwd-relative
                // `file:` fileset would resolve to sub/sub/a.txt and match nothing.
                match! repo.CommitPaths([ "sub/a.txt" ], "commit a only") with
                | Error e -> Assert.Fail $"CommitPaths from a subdirectory handle failed: {e.Message}"
                | Ok() ->
                    match! repo.ChangedFiles() with
                    | Error e -> Assert.Fail $"ChangedFiles failed: {e.Message}"
                    | Ok changes ->
                        let paths = changes |> List.map (fun c -> c.Path)
                        Assert.That(paths, Does.Not.Contain "sub/a.txt", "the committed file must no longer be pending")
                        Assert.That(paths, Does.Contain "sub/b.txt", "the uncommitted sibling must remain in @")
        }

// ---------------------------------------------------------------------------
// T-067: ShowFileBytes reads blob content byte-for-byte verbatim, where the
// UTF-8-decoding string ShowFile silently replaces non-UTF-8 bytes with U+FFFD.
// Real git/jj, both backends, the flagged non-UTF-8 read-modify-write case.
// ---------------------------------------------------------------------------

[<TestFixture>]
type ShowFileBytesTests() =

    let requireGit () =
        try
            Raw.git "." [ "--version" ]
        with _ ->
            // git isn't on PATH — a hermetic CI without it must skip, not fail, this fixture.
            Assert.Ignore "git not available on PATH"

    let requireJj () =
        try
            Raw.jj "." [ "--version" ]
        with _ ->
            // jj isn't on PATH — skip rather than fail.
            Assert.Ignore "jj not available on PATH"

    /// A blob whose bytes are NOT valid UTF-8: `0xFF`/`0xFE` are invalid UTF-8 lead bytes, so a
    /// UTF-8 decode replaces each with U+FFFD and can never reproduce the original bytes. Ends in
    /// `\n` so the trailing-newline capture is exercised too.
    let nonUtf8Blob: byte[] = [| 0x48uy; 0x69uy; 0xFFuy; 0xFEuy; 0x0Auy |] // "Hi" + 0xFF 0xFE + '\n'

    [<Test>]
    member _.GitShowFileBytesRoundTripsNonUtf8BlobWhereShowFileReplacesWithUFFFD() : Task =
        task {
            requireGit ()
            use sandbox = GitSandbox.Init "t067-git-bytes"
            // Write the raw bytes directly (the sandbox's string `Write` would UTF-8-encode them).
            File.WriteAllBytes(Path.Combine(sandbox.Path, "blob.bin"), nonUtf8Blob)
            sandbox.AddAll()
            sandbox.Commit "seed non-UTF-8 blob"

            let repo = Repo.FromGit(sandbox.Path, sandbox.Path, Git.Create())

            // The bytes API round-trips the blob verbatim — the whole point of the new API.
            match! repo.ShowFileBytes("HEAD", "blob.bin") with
            // Compare via F# structural equality (Assert.That(byte[], Is.EqualTo byte[]) is
            // FS0041-ambiguous under NUnit's overload set — see the Set/Nullable cases elsewhere).
            | Ok bytes ->
                Assert.That((bytes = nonUtf8Blob), Is.True, "ShowFileBytes must return the blob byte-for-byte")
            | Error e -> Assert.Fail $"ShowFileBytes failed: {e.Message}"

            // The string API UTF-8-decodes, silently corrupting the non-UTF-8 bytes to U+FFFD — the
            // documented limitation this task pins down.
            match! repo.ShowFile("HEAD", "blob.bin") with
            | Ok text ->
                // U+FFFD spelled via its code point so the source stays plain ASCII.
                let replacementChar = System.Char.ConvertFromUtf32 0xFFFD

                Assert.That(
                    text.Contains replacementChar,
                    Is.True,
                    "ShowFile UTF-8-decodes, so non-UTF-8 bytes become U+FFFD"
                )

                Assert.That(
                    System.Text.Encoding.UTF8.GetBytes text,
                    Is.Not.EqualTo nonUtf8Blob,
                    "the UTF-8-decoded string does NOT round-trip back to the original bytes"
                )
            | Error e -> Assert.Fail $"ShowFile failed: {e.Message}"
        }

    [<Test>]
    member _.JjShowFileBytesRoundTripsNonUtf8BlobWhereShowFileReplacesWithUFFFD() : Task =
        task {
            requireJj ()
            use sandbox = JjSandbox.Init "t067-jj-bytes"
            File.WriteAllBytes(Path.Combine(sandbox.Path, "blob.bin"), nonUtf8Blob)

            let repo = Repo.FromJj(sandbox.Path, sandbox.Path, Jj.Create())

            // `@` is the working-copy change; jj snapshots the freshly-written (auto-tracked) file
            // on the read command.
            match! repo.ShowFileBytes("@", "blob.bin") with
            // Compare via F# structural equality (Assert.That(byte[], Is.EqualTo byte[]) is
            // FS0041-ambiguous under NUnit's overload set — see the Set/Nullable cases elsewhere).
            | Ok bytes ->
                Assert.That((bytes = nonUtf8Blob), Is.True, "ShowFileBytes must return the blob byte-for-byte")
            | Error e -> Assert.Fail $"ShowFileBytes failed: {e.Message}"

            match! repo.ShowFile("@", "blob.bin") with
            | Ok text ->
                // U+FFFD spelled via its code point so the source stays plain ASCII.
                let replacementChar = System.Char.ConvertFromUtf32 0xFFFD

                Assert.That(
                    text.Contains replacementChar,
                    Is.True,
                    "ShowFile UTF-8-decodes, so non-UTF-8 bytes become U+FFFD"
                )

                Assert.That(
                    System.Text.Encoding.UTF8.GetBytes text,
                    Is.Not.EqualTo nonUtf8Blob,
                    "the UTF-8-decoded string does NOT round-trip back to the original bytes"
                )
            | Error e -> Assert.Fail $"ShowFile failed: {e.Message}"
        }
