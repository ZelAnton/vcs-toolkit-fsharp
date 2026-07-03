module VcsToolkit.Watch.Tests

open System
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.Core
open VcsToolkit.Jj
open VcsToolkit.Watch

// ---------------------------------------------------------------------------
// The pure snapshot-diff (event.rs) — fully hermetic, the load-bearing logic
// ---------------------------------------------------------------------------

/// A clean baseline state on `main` at one commit, one branch.
let private baseState: WatchState =
    { Head = Some "aaaa"
      Branch = Some "main"
      Upstream = None
      Ahead = None
      Behind = None
      Dirty = false
      ChangeCount = 0UL
      Conflicted = false
      Operation = OperationState.Clear
      Branches = [ "main" ] }

[<TestFixture>]
type DiffTests() =

    [<Test>]
    member _.IdenticalStatesYieldNoEvents() =
        Assert.That(Diff.diff baseState baseState, Is.Empty)

    [<Test>]
    member _.HeadMoveIsDetected() =
        let next = { baseState with Head = Some "bbbb" }
        Assert.That((Diff.diff baseState next = [ RepoEvent.HeadMoved(From = Some "aaaa", To = Some "bbbb") ]), Is.True)

    [<Test>]
    member _.BranchSwitchAndDetachAreDetected() =
        let switched =
            { baseState with
                Branch = Some "feature" }

        Assert.That(
            (Diff.diff baseState switched = [ RepoEvent.BranchSwitched(From = Some "main", To = Some "feature") ]),
            Is.True
        )

        let detached = { baseState with Branch = None }

        Assert.That(
            (Diff.diff baseState detached = [ RepoEvent.BranchSwitched(From = Some "main", To = None) ]),
            Is.True
        )

    [<Test>]
    member _.BranchCreateAndDeleteAreSortedAndPaired() =
        let added =
            { baseState with
                Branches = [ "main"; "feat-b"; "feat-a" ] }

        Assert.That(
            (Diff.diff baseState added = [ RepoEvent.BranchCreated "feat-a"; RepoEvent.BranchCreated "feat-b" ]),
            Is.True,
            "created names come out sorted"
        )

        let emptied = { baseState with Branches = [] }
        Assert.That((Diff.diff baseState emptied = [ RepoEvent.BranchDeleted "main" ]), Is.True)

    [<Test>]
    member _.WorkingCopyChangeFiresOnDirtyOrCount() =
        let dirtied =
            { baseState with
                Dirty = true
                ChangeCount = 3UL }

        Assert.That(
            (Diff.diff baseState dirtied = [ RepoEvent.WorkingCopyChanged(Dirty = true, ChangeCount = 3UL) ]),
            Is.True
        )

        // A count change while already dirty still fires (1 → 2 edits).
        let one =
            { baseState with
                Dirty = true
                ChangeCount = 1UL }

        let two =
            { baseState with
                Dirty = true
                ChangeCount = 2UL }

        Assert.That((Diff.diff one two = [ RepoEvent.WorkingCopyChanged(Dirty = true, ChangeCount = 2UL) ]), Is.True)

    [<Test>]
    member _.UpstreamAndAheadBehindAreSeparateEvents() =
        let next =
            { baseState with
                Upstream = Some "origin/main"
                Ahead = Some 2UL
                Behind = Some 0UL }

        Assert.That(
            (Diff.diff baseState next = [ RepoEvent.UpstreamChanged(Upstream = Some "origin/main")
                                          RepoEvent.AheadBehindChanged(Ahead = Some 2UL, Behind = Some 0UL) ]),
            Is.True
        )

    [<Test>]
    member _.OperationAndConflictTransitionsAreDetected() =
        let merging =
            { baseState with
                Operation = OperationState.Merge }

        Assert.That(
            (Diff.diff baseState merging = [ RepoEvent.OperationChanged(
                                                 From = OperationState.Clear,
                                                 To = OperationState.Merge
                                             ) ]),
            Is.True
        )

        let conflicted = { baseState with Conflicted = true }
        Assert.That((Diff.diff baseState conflicted = [ RepoEvent.ConflictChanged(Conflicted = true) ]), Is.True)

    [<Test>]
    member _.JjConflictEmitsOnlyConflictChangedNotOperation() =
        // jj derives `operation` and `conflicted` from the same bit, so a conflict flips
        // BOTH (Clear→Conflict and false→true). The redundant OperationChanged is suppressed.
        let next =
            { baseState with
                Operation = OperationState.Conflict
                Conflicted = true }

        Assert.That(
            (Diff.diff baseState next = [ RepoEvent.ConflictChanged(Conflicted = true) ]),
            Is.True,
            "Clear→Conflict must not also emit OperationChanged"
        )

    [<Test>]
    member _.GitMergeWithConflictEmitsBothOperationAndConflict() =
        // A merge that conflicts is two distinct facts — the Merge endpoint isn't Conflict.
        let next =
            { baseState with
                Operation = OperationState.Merge
                Conflicted = true }

        Assert.That(
            (Diff.diff baseState next = [ RepoEvent.OperationChanged(
                                              From = OperationState.Clear,
                                              To = OperationState.Merge
                                          )
                                          RepoEvent.ConflictChanged(Conflicted = true) ]),
            Is.True
        )

    [<Test>]
    member _.MultipleChangesEmitInStableOrder() =
        let prev =
            { baseState with
                Dirty = true
                ChangeCount = 2UL }

        let next = { baseState with Head = Some "cccc" } // clean again, new head

        Assert.That(
            (Diff.diff prev next = [ RepoEvent.HeadMoved(From = Some "aaaa", To = Some "cccc")
                                     RepoEvent.WorkingCopyChanged(Dirty = false, ChangeCount = 0UL) ]),
            Is.True
        )

// ---------------------------------------------------------------------------
// State-directory resolution (gitlinks + worktree commondir) — needs temp dirs
// ---------------------------------------------------------------------------

let private withTemp (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"vcs-watch-test-{Guid.NewGuid():N}")
    Directory.CreateDirectory dir |> ignore

    try
        f dir
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            // best-effort cleanup; a leaked temp dir must not fail the run.
            ()

[<TestFixture>]
type PathTests() =

    [<Test>]
    member _.NoCommondirFileYieldsNone() =
        withTemp (fun scratch ->
            let gitDir = Path.Combine(scratch, ".git")
            Directory.CreateDirectory gitDir |> ignore
            Assert.That(Paths.commonDir gitDir, Is.EqualTo None))

    [<Test>]
    member _.RelativeCommondirResolvesToSharedGitDir() =
        withTemp (fun scratch ->
            let shared = Path.Combine(scratch, ".git")
            let priv = Path.Combine(shared, "worktrees", "wt")
            Directory.CreateDirectory priv |> ignore
            File.WriteAllText(Path.Combine(priv, "commondir"), "../..\n")

            match Paths.commonDir priv with
            | Some resolved ->
                Assert.That(resolved, Is.EqualTo(Paths.lexicallyNormalized shared))
                Assert.That(resolved.Contains "..", Is.False, "the `..` segments must be resolved")
            | None -> Assert.Fail "expected the shared dir")

    [<Test>]
    member _.StateDirsIncludesPrivateAndSharedForWorktree() =
        withTemp (fun scratch ->
            let root = Path.Combine(scratch, "wt-worktree")
            let shared = Path.Combine(scratch, ".git")
            let priv = Path.Combine(shared, "worktrees", "wt")
            Directory.CreateDirectory priv |> ignore
            Directory.CreateDirectory root |> ignore
            File.WriteAllText(Path.Combine(priv, "commondir"), "../..\n")
            File.WriteAllText(Path.Combine(root, ".git"), $"gitdir: {priv}\n")

            match Paths.stateDirs BackendKind.Git root with
            | Ok dirs -> Assert.That(dirs.Length, Is.EqualTo 2, "private + shared")
            | Error e -> Assert.Fail $"stateDirs failed: {e.Message}")

    [<Test>]
    member _.SelfReferentialCommondirIsDeduped() =
        withTemp (fun scratch ->
            let gitDir = Path.Combine(scratch, ".git")
            Directory.CreateDirectory gitDir |> ignore
            File.WriteAllText(Path.Combine(gitDir, "commondir"), ".\n")
            let root = Path.Combine(scratch, "root")
            Directory.CreateDirectory root |> ignore
            File.WriteAllText(Path.Combine(root, ".git"), $"gitdir: {gitDir}\n")

            match Paths.stateDirs BackendKind.Git root with
            | Ok dirs -> Assert.That(dirs.Length, Is.EqualTo 1, "self-reference deduped")
            | Error e -> Assert.Fail $"stateDirs failed: {e.Message}")

// ---------------------------------------------------------------------------
// Stats counters
// ---------------------------------------------------------------------------

[<TestFixture>]
type StatsTests() =

    [<Test>]
    member _.CountsWatchErrorsIndependently() =
        let stats = StatsInner()
        Assert.That((stats.Snapshot()).WatchErrors, Is.EqualTo 0UL)
        stats.NoteWatchError()
        stats.NoteWatchError()
        let snap = stats.Snapshot()
        Assert.That(snap.WatchErrors, Is.EqualTo 2UL)
        Assert.That((snap.Requeries, snap.Changes, snap.Skipped), Is.EqualTo((0UL, 0UL, 0UL)))
        Assert.That(snap.LastError, Is.EqualTo None)

    [<Test>]
    member _.BaselineTimeoutIsTransient() =
        // A baseline-query timeout (`Io` wrapping `TimeoutException`, raised when the startup
        // snapshot exceeds `RequeryTimeout`) is transient — a wedged repo may un-wedge, so
        // `Build()` is worth retrying. A non-timeout `Io` is not.
        Assert.That((WatchError.Io(TimeoutException "baseline exceeded")).IsTransient, Is.True)
        Assert.That((WatchError.Io(IOException "disk error")).IsTransient, Is.False)

    [<Test>]
    member _.NoteSkipRecordsTheLastError() =
        let stats = StatsInner()
        stats.NoteSkip WatcherErrorKind.Timeout
        stats.NoteSkip WatcherErrorKind.Branches
        let snap = stats.Snapshot()
        Assert.That(snap.Skipped, Is.EqualTo 2UL)
        Assert.That(snap.LastError, Is.EqualTo(Some WatcherErrorKind.Branches), "the most recent skip wins")

// ---------------------------------------------------------------------------
// The debounce → re-query → diff pipeline (drive watchLoop directly)
// ---------------------------------------------------------------------------

/// A scripted runner whose snapshot reads `head`, clean, on `main` with one bookmark.
let private scriptedRunner (head: string) =
    ScriptedRunner()
        .On([ "log"; "-r"; "@"; "--limit"; "1" ], Reply.Ok $"{head}\t1\t0\n") // empty=1 clean, conflict=0
        .On([ "log"; "heads(::@ & bookmarks())" ], Reply.Ok "main\txyz\n")
        .On([ "bookmark"; "list" ], Reply.Ok "main\tabc\n")

/// A jj-backed `Repo` over `scriptedRunner`.
let private scriptedJj (head: string) =
    Repo.FromJj("/r", "/r", Jj.WithRunner(scriptedRunner head))

let private fastConfig: LoopConfig =
    { Debounce = TimeSpan.FromMilliseconds 20.0
      MaxWait = TimeSpan.FromMilliseconds 60.0
      RequeryTimeout = Some(TimeSpan.FromSeconds 5.0)
      OutputCapacity = 64 }

let private channels () =
    let raw = Channel.CreateUnbounded<unit>()

    let out =
        Channel.CreateBounded<RepoChange>(BoundedChannelOptions(64, FullMode = BoundedChannelFullMode.Wait))

    raw, out

[<TestFixture>]
type PipelineTests() =

    [<Test>]
    member _.SignalTriggersRequeryAndEmitsChange() : Task =
        task {
            let raw, out = channels ()
            let stats = StatsInner()
            use cts = new CancellationTokenSource()

            let _loop =
                Loop.watchLoop (scriptedJj "bbbb") raw out baseState fastConfig stats cts.Token

            raw.Writer.TryWrite(()) |> ignore

            use readCts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
            let! change = out.Reader.ReadAsync readCts.Token

            Assert.That(
                (change.Events = [ RepoEvent.HeadMoved(From = Some "aaaa", To = Some "bbbb") ]),
                Is.True,
                "a settled burst re-queries and emits the head move"
            )

            Assert.That(change.Snapshot.Head, Is.EqualTo(Some "bbbb"), "the change carries the fresh snapshot")
            cts.Cancel()
        }

    [<Test>]
    member _.NoChangeReQueryEmitsNothing() : Task =
        task {
            let raw, out = channels ()
            let stats = StatsInner()
            use cts = new CancellationTokenSource()
            // Same head as the baseline → the re-query diffs to nothing.
            let _loop =
                Loop.watchLoop (scriptedJj "aaaa") raw out baseState fastConfig stats cts.Token

            raw.Writer.TryWrite(()) |> ignore
            do! Task.Delay 250

            let snap = stats.Snapshot()
            Assert.That(snap.Requeries >= 1UL, Is.True, "the burst re-queried")
            Assert.That(snap.Changes, Is.EqualTo 0UL, "an unchanged state emits no RepoChange")
            Assert.That(out.Reader.TryRead() |> fst, Is.False, "nothing was queued on the output")
            cts.Cancel()
        }

    [<Test>]
    member _.RequeryErrorIsSkippedAndCounted() : Task =
        task {
            // The snapshot command fails → the re-query is a transient skip, not a change.
            let runner =
                ScriptedRunner().On([ "log"; "-r"; "@"; "--limit"; "1" ], Reply.Fail(1, "boom"))

            let repo = Repo.FromJj("/r", "/r", Jj.WithRunner runner)
            let raw, out = channels ()
            let stats = StatsInner()
            use cts = new CancellationTokenSource()
            let _loop = Loop.watchLoop repo raw out baseState fastConfig stats cts.Token
            raw.Writer.TryWrite(()) |> ignore
            do! Task.Delay 250

            let snap = stats.Snapshot()
            Assert.That(snap.Skipped >= 1UL, Is.True, "the failed re-query was skipped")
            Assert.That(snap.LastError, Is.EqualTo(Some WatcherErrorKind.Snapshot))
            Assert.That(snap.Changes, Is.EqualTo 0UL)
            cts.Cancel()
        }

    [<Test>]
    member _.CancellingTheLoopCompletesTheOutputChannel() : Task =
        task {
            let raw, out = channels ()
            let stats = StatsInner()
            let cts = new CancellationTokenSource()

            let _loop =
                Loop.watchLoop (scriptedJj "aaaa") raw out baseState fastConfig stats cts.Token

            cts.Cancel()

            // The loop's `finally` completes the output channel however it ends, so a pending
            // read yields None (ChannelClosedException) rather than hanging forever.
            use readCts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

            let! result =
                task {
                    try
                        let! _ = out.Reader.ReadAsync readCts.Token
                        return "got-item"
                    with :? ChannelClosedException ->
                        return "completed"
                }

            Assert.That(result, Is.EqualTo "completed", "the cancelled loop completes the channel")
        }

    [<Test>]
    member _.BuildAndDisposeLifecycleOverARealStateDir() : Task =
        task {
            // A real temp dir with a `.jj` state dir for the FileSystemWatcher to register on.
            let dir = Path.Combine(Path.GetTempPath(), $"vcs-watch-live-{Guid.NewGuid():N}")
            Directory.CreateDirectory(Path.Combine(dir, ".jj")) |> ignore

            try
                let repo = Repo.FromJj(dir, dir, Jj.WithRunner(scriptedRunner "aaaa"))

                match! RepoWatcher.Watch repo with
                | Ok watcher ->
                    Assert.That(watcher.Current.Head, Is.EqualTo(Some "aaaa"), "the baseline snapshot was captured")
                    (watcher :> IDisposable).Dispose()
                    // Give the cancelled loop a moment to run its `finally` and close the channel.
                    do! Task.Delay 150
                    let! change = watcher.Recv()
                    Assert.That(change, Is.EqualTo None, "after dispose the watcher yields no more changes")
                | Error e -> Assert.Fail $"build failed: {e.Message}"
            finally
                try
                    Directory.Delete(dir, true)
                with _ ->
                    // best-effort cleanup.
                    ()
        }
