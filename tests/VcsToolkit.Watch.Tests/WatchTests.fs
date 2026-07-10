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

    [<Test>]
    member _.ColocatedJjAndGitWatchesBothStateDirs() =
        withTemp (fun scratch ->
            let root = Path.Combine(scratch, "colocated")
            Directory.CreateDirectory root |> ignore
            let jjDir = Path.Combine(root, ".jj")
            let gitDir = Path.Combine(root, ".git")
            Directory.CreateDirectory jjDir |> ignore
            Directory.CreateDirectory gitDir |> ignore

            match Paths.stateDirs BackendKind.Jj root with
            | Ok dirs ->
                Assert.That(dirs.Length, Is.EqualTo 2, "both .jj and .git are watched")
                Assert.That(List.exists (Paths.pathsEqual jjDir) dirs, Is.True, ".jj is watched")
                Assert.That(List.exists (Paths.pathsEqual gitDir) dirs, Is.True, ".git is watched")
            | Error e -> Assert.Fail $"stateDirs failed: {e.Message}")

    [<Test>]
    member _.PureJjRepoWatchesOnlyDotJj() =
        withTemp (fun scratch ->
            let root = Path.Combine(scratch, "pure-jj")
            Directory.CreateDirectory root |> ignore
            let jjDir = Path.Combine(root, ".jj")
            Directory.CreateDirectory jjDir |> ignore

            match Paths.stateDirs BackendKind.Jj root with
            | Ok dirs ->
                Assert.That(dirs.Length, Is.EqualTo 1, "no .git next to .jj")
                Assert.That(List.exists (Paths.pathsEqual jjDir) dirs, Is.True)
            | Error e -> Assert.Fail $"stateDirs failed: {e.Message}")

    [<Test>]
    member _.PureGitRepoBehaviourIsUnchanged() =
        withTemp (fun scratch ->
            let root = Path.Combine(scratch, "pure-git")
            Directory.CreateDirectory root |> ignore
            let gitDir = Path.Combine(root, ".git")
            Directory.CreateDirectory gitDir |> ignore

            match Paths.stateDirs BackendKind.Git root with
            | Ok dirs ->
                Assert.That(dirs.Length, Is.EqualTo 1, "no commondir, no colocation logic for git backend")
                Assert.That(List.exists (Paths.pathsEqual gitDir) dirs, Is.True)
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

/// Whether `pattern` appears as an ordered (non-contiguous) subsequence of `args` — the
/// same relaxed matching `ScriptedRunner.On` uses, so a `When` predicate matches the same
/// shorthand arg list even though the real jj CLI wrapper interleaves extra flags (e.g.
/// `--no-graph`, `-T <template>`) that a strict list-equality check would miss.
let private isOrderedSubsequence (pattern: string list) (args: string list) =
    let rec go pattern args =
        match pattern, args with
        | [], _ -> true
        | _, [] -> false
        | p :: ptail, a :: atail -> if p = a then go ptail atail else go pattern atail

    go pattern args

/// A jj runner whose snapshot `log` command fails with a **transient** process error
/// (`ProcessError.Spawn` — a momentary spawn hiccup) the first `failCount` calls, then
/// succeeds and reads `head`. `ScriptedRunner` tries rules in registration order and stops
/// at the first match, so the stateful `When` (registered first) wins while its predicate
/// still returns `true`; once the call count exceeds `failCount` it falls through to the
/// unconditional `On` fallback.
let private transientThenOkRunner (head: string) (failCount: int) =
    let mutable calls = 0

    ScriptedRunner()
        .When(
            (fun (cmd: Command) ->
                let isSnapshotQuery =
                    isOrderedSubsequence [ "log"; "-r"; "@"; "--limit"; "1" ] (List.ofSeq cmd.Arguments)

                if isSnapshotQuery then
                    calls <- calls + 1

                isSnapshotQuery && calls <= failCount),
            Reply.Error(ProcessError.Spawn("jj", "transient spawn failure"))
        )
        .On([ "log"; "-r"; "@"; "--limit"; "1" ], Reply.Ok $"{head}\t1\t0\n")
        .On([ "log"; "heads(::@ & bookmarks())" ], Reply.Ok "main\txyz\n")
        .On([ "bookmark"; "list" ], Reply.Ok "main\tabc\n")

/// A jj runner whose snapshot `log` command always fails with a plain non-zero exit — a
/// **non-transient** `ProcessError.Exit`, modelling an unrecoverable failure (e.g. the
/// `.jj` state directory having been removed underneath the watch).
let private terminalFailureRunner () =
    ScriptedRunner().On([ "log"; "-r"; "@"; "--limit"; "1" ], Reply.Fail(1, "state directory gone"))

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
    member _.TransientRequeryFailureRetriesWithBackoffAndRecovers() : Task =
        task {
            // The snapshot command fails transiently twice, then succeeds — the loop must
            // ride that out with a bounded backoff instead of treating it as terminal.
            let raw, out = channels ()
            let stats = StatsInner()
            use cts = new CancellationTokenSource()
            let repo = Repo.FromJj("/r", "/r", Jj.WithRunner(transientThenOkRunner "bbbb" 2))
            let _loop = Loop.watchLoop repo raw out baseState fastConfig stats cts.Token
            raw.Writer.TryWrite(()) |> ignore

            // Generous bound: two retry backoffs (≈200ms + 400ms) plus settle/debounce.
            use readCts = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
            let! change = out.Reader.ReadAsync readCts.Token

            Assert.That(
                (change.Events = [ RepoEvent.HeadMoved(From = Some "aaaa", To = Some "bbbb") ]),
                Is.True,
                "the loop retried past the transient failures and eventually emitted the change"
            )

            let snap = stats.Snapshot()
            Assert.That(snap.Skipped >= 2UL, Is.True, "each transient attempt was counted as a skip")
            Assert.That(snap.LastError, Is.EqualTo(Some WatcherErrorKind.Snapshot))
            Assert.That(snap.Changes, Is.EqualTo 1UL)

            Assert.That(
                out.Reader.Completion.IsCompleted,
                Is.False,
                "the output channel stays open — a transient failure never terminates the loop"
            )

            cts.Cancel()
        }

    [<Test>]
    member _.TerminalRequeryFailureClosesTheOutputChannelWithAnError() : Task =
        task {
            // The snapshot command always fails non-transiently — the loop must signal the
            // consumer with a terminal error (not just skip-and-continue) and then stop.
            let raw, out = channels ()
            let stats = StatsInner()
            use cts = new CancellationTokenSource()
            let repo = Repo.FromJj("/r", "/r", Jj.WithRunner(terminalFailureRunner ()))

            let baselineSnapshot: RepoSnapshot =
                { Head = Some "aaaa"
                  Branch = Some "main"
                  Tracking = None
                  Dirty = false
                  ChangeCount = 0UL
                  Conflicted = false
                  Operation = OperationState.Clear }

            let loopTask = Loop.watchLoop repo raw out baseState fastConfig stats cts.Token

            use watcher =
                new RepoWatcher(out, baselineSnapshot, stats, ResizeArray<FileSystemWatcher>(), cts, loopTask)

            raw.Writer.TryWrite(()) |> ignore

            let recvTask = watcher.Recv()
            let! winner = Task.WhenAny(recvTask :> Task, Task.Delay(TimeSpan.FromSeconds 5.0))

            Assert.That(
                Object.ReferenceEquals(winner, recvTask),
                Is.True,
                "Recv should surface the terminal failure promptly"
            )

            try
                let! _ = recvTask
                Assert.Fail "expected Recv to throw once the loop signals a terminal failure"
            with :? ChannelClosedException as e ->
                match e.InnerException with
                | :? WatcherTerminated as terminal ->
                    match terminal :> exn with
                    | WatcherTerminated err ->
                        Assert.That(err.IsTransient, Is.False, "a plain exit failure is not transient")
                    | _ -> Assert.Fail "unreachable"
                | other -> Assert.Fail $"expected the ChannelClosedException to wrap WatcherTerminated, got {other}"

            let snap = stats.Snapshot()
            Assert.That(snap.Skipped >= 1UL, Is.True, "the failed re-query was counted before the terminal signal")
            Assert.That(snap.LastError, Is.EqualTo(Some WatcherErrorKind.Snapshot))

            // The loop stops after signalling — it never spins on a terminal failure.
            do! Task.Delay 100
            Assert.That(loopTask.IsCompleted, Is.True, "the loop must not keep running after a terminal signal")
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
