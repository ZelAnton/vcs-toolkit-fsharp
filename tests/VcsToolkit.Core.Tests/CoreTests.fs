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

// Create a unique temp directory, run `f` against it, then remove it.
let private withTempDir (f: string -> unit) =
    let dir =
        Path.Combine(Path.GetTempPath(), "vcs-core-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore

    try
        f dir
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            // Best-effort temp cleanup; a locked/again-deleted dir must not fail the test.
            ()

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
        // The facade's own io/detection variants are never a vcs/transient/not-found error.
        let io = RepoError.Io "disk full"
        Assert.That(io.IsTransient, Is.False)
        Assert.That(io.IsTransientFetchError, Is.False)
        Assert.That(io.IsNothingToCommit, Is.False)
        Assert.That(io.IsLockContention, Is.False)
        Assert.That((RepoError.WorktreeNotFound "/wt").IsTransient, Is.False)

    [<Test>]
    member _.RepoErrorMessageIncludesContext() =
        Assert.That((RepoError.NotARepository "/x").Message, Does.Contain "/x")
        Assert.That((RepoError.WorktreeNotFound "/wt").Message, Does.Contain "/wt")
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
        Assert.That(jj.Cwd, Is.EqualTo "/repo")
        Assert.That(jj.At("/elsewhere").Cwd, Is.EqualTo "/elsewhere", "At re-anchors the cwd")

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
            | Error _ -> ()
            | Ok() -> Assert.Fail "an empty path set must be refused before spawning"
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
            // `file show -r <revset> file:"<path>"`; the trailing newline must survive untrimmed.
            let repo =
                jjRepo [ "file"; "show"; "-r"; "@"; "file:\"file.txt\"" ] (Reply.Ok "hello world\n")

            match! repo.ShowFile("@", "file.txt") with
            | Ok content -> Assert.That(content, Is.EqualTo "hello world\n")
            | Error e -> Assert.Fail $"show file failed: {e.Message}"
        }

    [<Test>]
    member _.JjShowFileSurfacesAMissingPathAsError() : Task =
        task {
            let repo =
                jjRepo [ "file"; "show"; "-r"; "@"; "file:\"missing.txt\"" ] (Reply.Fail(1, "Error: No such path"))

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
            // is_conflicted(true) → resolve --list → MergeProbe.Conflicts, still rolled back.
            let runner =
                ScriptedRunner()
                    .On([ "op"; "log"; "--limit"; "1" ], Reply.Ok "opabc\n") // op-head capture
                    .On([ "op"; "log"; "--limit"; "32" ], Reply.Ok(opRow "opabc")) // divergence probe: captured op present
                    .On([ "new" ], Reply.Ok "")
                    .On([ "log"; "-T" ], Reply.Ok "1\n") // conflicted
                    .On([ "resolve"; "--list" ], Reply.Ok "a.rs    2-sided conflict\n")
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
            | Error e -> Assert.That(e.Message, Does.Contain "main workspace", "the guard must name the reason")
            | Ok() -> Assert.Fail "removing the main (default) workspace must be refused"
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
