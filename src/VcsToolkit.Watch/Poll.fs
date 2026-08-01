namespace VcsToolkit.Watch

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open VcsToolkit.Core

// Poll a git/jj repository on a fixed interval and emit the same typed state-change events a
// `RepoWatcher` does. A `RepoPoller` never touches `FileSystemWatcher`: it simply **re-queries**
// the repo state through `VcsToolkit.Core`'s batched `Snapshot` (+ `LocalBranches`) every
// `Interval`, **diffs** it against the previous observation with the public `RepoDiff.diff`, and
// pushes the resulting `RepoChange` down the same channel-backed `Recv`/`ReadAll` interface. That
// makes it the portable counterpart to `RepoWatcher`: it works wherever a subprocess can run —
// network shares, docker volume mounts, NFS/container filesystems without inotify, across the WSL
// boundary — at the cost of a detection latency bounded below by the poll interval.

/// Timing defaults specific to the polling monitor (the watcher's own live in `Constants`).
module internal PollConstants =

    /// Default poll period: re-query the repository this often. Deliberately far coarser than
    /// the watcher's debounce — every tick spawns git/jj, so the default trades latency for a
    /// small, predictable process budget.
    let defaultPollInterval = TimeSpan.FromSeconds 2.0

    /// Floor on the poll period. A zero/negative/sub-millisecond interval would turn the loop
    /// into a subprocess busy-loop hammering the repository, so it is clamped here instead.
    let minPollInterval = TimeSpan.FromMilliseconds 10.0

/// Channel-reader-to-`IAsyncEnumerable` plumbing for a monitor's `ReadAll`.
module internal Streaming =

    /// Expose `reader` as an `IAsyncEnumerable`, invoking `onYield` for each yielded item (the
    /// monitors use it to advance their last-observed snapshot).
    ///
    /// Both `requested` (the `ReadAll` argument token) and the `[EnumeratorCancellation]` token a
    /// consumer supplies via `WithCancellation(...)` on `await foreach` are honoured: when both are
    /// cancelable they are linked (`CancellationTokenSource.CreateLinkedTokenSource`) so cancelling
    /// *either* stops the enumeration; when only one is cancelable, it alone is used; when neither
    /// is, enumeration is uncancelable. A linked source is disposed on every exit from the
    /// enumerator — normal completion, an early consumer `break`, or an exception — via
    /// `DisposeAsync`.
    ///
    /// `RepoWatcher.ReadAll` implements this identical contract inline and is deliberately left
    /// alone rather than re-pointed here, so that adding the poller cannot perturb the already
    /// shipped watcher.
    let readAll
        (reader: ChannelReader<'T>)
        (onYield: 'T -> unit)
        (requested: CancellationToken)
        : IAsyncEnumerable<'T> =
        { new IAsyncEnumerable<'T> with
            member _.GetAsyncEnumerator(enumeratorCancellation) =
                let linkedCts =
                    if requested.CanBeCanceled && enumeratorCancellation.CanBeCanceled then
                        Some(CancellationTokenSource.CreateLinkedTokenSource(requested, enumeratorCancellation))
                    else
                        None

                let effectiveCancellation =
                    match linkedCts with
                    | Some cts -> cts.Token
                    | None ->
                        if requested.CanBeCanceled then
                            requested
                        else
                            enumeratorCancellation

                let inner = reader.ReadAllAsync(effectiveCancellation).GetAsyncEnumerator()

                { new IAsyncEnumerator<'T> with
                    member _.Current = inner.Current

                    member _.MoveNextAsync() =
                        let pending = inner.MoveNextAsync()

                        if pending.IsCompletedSuccessfully then
                            if pending.Result then
                                onYield inner.Current

                            pending
                        else
                            ValueTask<bool>(
                                task {
                                    let! hasItem = pending.AsTask()

                                    if hasItem then
                                        onYield inner.Current

                                    return hasItem
                                }
                            )

                    member _.DisposeAsync() =
                        match linkedCts with
                        | None -> inner.DisposeAsync()
                        | Some cts ->
                            let innerDispose = inner.DisposeAsync()

                            if innerDispose.IsCompletedSuccessfully then
                                cts.Dispose()
                                innerDispose
                            else
                                ValueTask(
                                    task {
                                        try
                                            do! innerDispose.AsTask()
                                        finally
                                            cts.Dispose()
                                    }
                                ) } }

/// The tick-to-re-query-to-diff polling pipeline.
module internal PollLoop =

    /// Clamp a caller-supplied period into `[minPollInterval, maxTimerDelay]`: below the floor a
    /// poller would busy-spawn git/jj, and above `Task.Delay`'s ~49.7-day ceiling the timer throws.
    let clampInterval (interval: TimeSpan) : TimeSpan =
        if interval < PollConstants.minPollInterval then
            PollConstants.minPollInterval
        elif interval > Constants.maxTimerDelay then
            Constants.maxTimerDelay
        else
            interval

    /// The background loop: wait out the interval, re-query the state, diff it against the
    /// previous observation, and emit a `RepoChange` when anything changed.
    ///
    /// The wait is **between** re-queries, not a fixed wall-clock schedule: a tick starts only
    /// after the previous one finished, so a slow query delays the next one instead of stacking
    /// overlapping queries onto a struggling repository.
    ///
    /// Re-query failures follow exactly the policy `Loop.watchLoop` runs (it is literally the same
    /// `Loop.requeryWithRetry`): a **transient** failure (`WatchError.IsTransient` — a momentarily
    /// held lock, a re-query timeout) is retried in place with a bounded backoff and the loop keeps
    /// running afterwards; a **terminal** failure (not transient, or the retry budget ran out) is
    /// signalled to the consumer exactly once by closing `out` with
    /// `WatcherTerminated error`, which `RepoPoller.Recv()`/`ReadAll` re-raise — so a broken poll is
    /// distinguishable from an ordinary disposal (which still yields `None`, via the unconditional
    /// `TryComplete` in the `finally`). Either way the loop stops; it never spins.
    let pollLoop
        (repo: Repo)
        (out: Channel<RepoChange>)
        (initialPrev: RepoSnapshot * string list)
        (interval: TimeSpan)
        (config: LoopConfig)
        (stats: StatsInner)
        (ct: CancellationToken)
        : Task =
        task {
            let mutable prev = initialPrev
            let tick = clampInterval interval

            try
                try
                    let mutable running = true

                    while running do
                        do! Task.Delay(tick, ct)

                        if ct.IsCancellationRequested then
                            running <- false
                        else
                            stats.NoteRequery()
                            let! outcome = Loop.requeryWithRetry repo config stats ct

                            match outcome with
                            | Choice2Of2 error ->
                                // Terminal: not transient, or the transient-retry budget ran out.
                                // Signal the consumer and stop — never spin.
                                out.Writer.Complete(WatcherTerminated error)
                                running <- false
                            | Choice1Of2(snapshot, branches) ->
                                let next = (snapshot, branches)
                                let events = RepoDiff.diff prev next
                                prev <- next

                                if not (List.isEmpty events) then
                                    do! out.Writer.WriteAsync({ Snapshot = snapshot; Events = events }, ct)
                                    stats.NoteChange()
                with
                | :? OperationCanceledException ->
                    // the poller was disposed (ct cancelled) — clean shutdown.
                    ()
                | :? ChannelClosedException ->
                    // the output receiver was dropped — stop.
                    ()
            finally
                // Always close the output channel — however the loop ends (incl. an unexpected
                // throw) — so a pending `Recv` returns None instead of hanging forever. No `do!`
                // may appear in a `task` CE's `finally`, so this stays a synchronous TryComplete.
                out.Writer.TryComplete() |> ignore
        }

/// Builder for a `RepoPoller` — set the poll period and the per-re-query deadline, then `Build`.
[<Sealed>]
type PollerBuilder internal (repo: Repo, interval: TimeSpan, requeryTimeout: TimeSpan option) =

    /// How long to wait between re-queries (default 2 s) — the polling analogue of the watcher's
    /// `Debounce`/`MaxWait` pair, and the lower bound on how fast a change can be reported. The
    /// wait sits *between* re-queries, so the effective period is `period` plus one query's
    /// duration. Clamped to `[10 ms, 24 days]`: a smaller value would busy-spawn git/jj, a larger
    /// one would overflow the underlying timer.
    member _.Interval(period: TimeSpan) =
        PollerBuilder(repo, period, requeryTimeout)

    /// Deadline on a single re-query (default 30 s); `None` disables it. Same best-effort
    /// semantics as `RepoWatcher`'s: it stops the loop *waiting*, but cannot kill the in-flight
    /// git/jj process — configure the client's `DefaultTimeout` to hard-bound a wedged command.
    member _.RequeryTimeout(timeout: TimeSpan option) = PollerBuilder(repo, interval, timeout)

    /// Start polling. Captures the baseline state (bounded by `RequeryTimeout`) and starts the
    /// background poll task. Returns the built `RepoPoller`, or the baseline-query error — which
    /// is the only way `Build` can fail, since a poller registers no OS watch and so cannot fail
    /// on a missing/unreadable state directory the way `RepoWatcher.Builder.Build` can.
    member _.Build() : Task<Result<RepoPoller, WatchError>> =
        task {
            // Observe the repo **read-only**, for the same reason `RepoWatcher` does — and more
            // sharply so here: on jj a plain `Snapshot`/`LocalBranches` snapshots the working copy
            // and records a new operation as a side effect, so an unconditionally polling monitor
            // would append to the op log every single tick, forever, purely by observing. Swap the
            // jj client for its read-only view (`Jj.ReadOnly()` -> `--ignore-working-copy`) via the
            // `Jj` escape hatch, keeping the same `Root`/`Cwd`; git has no such side effect and is
            // polled unchanged. Both the baseline and every loop re-query go through this view, so
            // they stay consistent (no spurious first-event diff).
            let repo =
                match repo.Jj with
                | Some jj -> Repo.FromJj(repo.Root, repo.Cwd, jj.ReadOnly())
                | None -> repo

            // `Debounce`/`MaxWait` configure the watcher's burst settling and are unused on the
            // polling path (`PollLoop` waits on its own interval instead); only `RequeryTimeout`
            // and `OutputCapacity` are read here, via the reused `Loop.requeryOnce`/
            // `Loop.requeryWithRetry`. They are set to the interval rather than left at zero so the
            // record never reads as an accidentally-defaulted config.
            let config =
                { Debounce = interval
                  MaxWait = interval
                  RequeryTimeout = requeryTimeout
                  OutputCapacity = Constants.outputCapacity }

            // The baseline goes through the very same timeout-bounded query the loop uses, so a
            // wedged repo (held `index.lock`, hung fsmonitor, dead network FS) fails `Build()`
            // instead of hanging it.
            let! baseline = Loop.requeryOnce repo config CancellationToken.None

            match baseline with
            | Choice2Of2(_, error) -> return Error error
            | Choice1Of2(snapshot, branches) ->
                // Bounded output channel: a slow consumer applies backpressure (the loop pauses
                // polling) rather than growing an unbounded queue of stale changes.
                let out =
                    Channel.CreateBounded<RepoChange>(
                        BoundedChannelOptions(config.OutputCapacity, FullMode = BoundedChannelFullMode.Wait)
                    )

                let stats = StatsInner()
                let cts = new CancellationTokenSource()

                let loopTask =
                    PollLoop.pollLoop repo out (snapshot, branches) interval config stats cts.Token

                return Ok(new RepoPoller(out, snapshot, stats, cts, loopTask))
        }

/// A live **poll** over a repository, yielding the same `RepoChange`s a `RepoWatcher` does, but
/// driven by a timer rather than by filesystem notifications. `Dispose` stops the background task.
///
/// Pick between the two by how the repository is reached, not by preference:
///
/// * `RepoWatcher` — `FileSystemWatcher` over the `.git`/`.jj` state dir. Sub-second latency and no
///   polling cost while the repo is idle, but it inherits every limitation of the OS watch: it is
///   unreliable or unavailable on network shares (SMB/NFS), on docker/podman volume mounts, and
///   across the WSL/host boundary, and an OS-level watch failure (e.g. the state dir removed and
///   re-created) can only be reported, not recovered from (see `WatcherStats.WatchErrors`).
/// * `RepoPoller` — a plain timer plus the same re-query-and-diff. It works anywhere a subprocess
///   runs, including all of the above, and has no OS-watch failure mode at all; in exchange, the
///   detection latency is bounded below by `Interval`, and it spawns git/jj on every tick whether
///   or not anything changed.
///
/// Both are built on the same public `RepoDiff.diff`, so a given series of repository mutations
/// produces the same `RepoEvent`s through either monitor.
and [<Sealed>] RepoPoller
    internal
    (
        out: Channel<RepoChange>,
        baseline: RepoSnapshot,
        stats: StatsInner,
        cts: CancellationTokenSource,
        // The running loop is rooted by the task scheduler and stopped via `cts`, so the handle
        // needn't be held; named `_` to say so.
        _loopTask: Task
    ) =

    let mutable current = baseline
    // 0 = live, 1 = disposed. `Interlocked` rather than a plain `bool`, so two threads racing to
    // dispose the poller (a common shape when one thread owns the `Recv` loop and another owns
    // shutdown) run the teardown exactly once.
    let mutable disposed = 0

    /// The default poll period (2 s) used unless overridden via the builder.
    static member DefaultInterval = PollConstants.defaultPollInterval

    /// The default per-re-query deadline (30 s) used unless overridden via the builder — the same
    /// default `RepoWatcher` uses.
    static member DefaultRequeryTimeout = Constants.defaultRequeryTimeout

    /// A builder over `repo`.
    static member Builder(repo: Repo) : PollerBuilder =
        PollerBuilder(repo, PollConstants.defaultPollInterval, Some Constants.defaultRequeryTimeout)

    /// Start polling `repo` with the defaults (2 s interval, 30 s re-query deadline).
    static member Poll(repo: Repo) : Task<Result<RepoPoller, WatchError>> = RepoPoller.Builder(repo).Build()

    /// Await the next observed change. Returns `None` once the poller is disposed and its
    /// background loop ends cleanly. If instead the loop stopped after a **terminal** re-query
    /// failure (see `PollLoop.pollLoop`), this throws `ChannelClosedException` whose
    /// `InnerException` is `WatcherTerminated error` — so a caller can distinguish "polling broke"
    /// from an ordinary disposal, exactly as with `RepoWatcher.Recv()`.
    member _.Recv() : Task<RepoChange option> =
        task {
            try
                let! change = out.Reader.ReadAsync()
                current <- change.Snapshot
                return Some change
            with :? ChannelClosedException as e when isNull e.InnerException ->
                // the loop ended cleanly (disposed / task done) — no more changes.
                return None
        }

    /// Enumerate observed changes until the poller is disposed. A terminal re-query failure ends
    /// the stream by throwing its `WatcherTerminated` error. Each yielded change advances
    /// `Current`; `Recv` and `ReadAll` consume the same channel, so using both concurrently divides
    /// changes between them and shares the last-observed snapshot.
    ///
    /// Both `cancellationToken` (this argument) and the `[EnumeratorCancellation]` token a consumer
    /// supplies via `WithCancellation(...)` on `await foreach` are honoured — cancelling either
    /// stops the enumeration.
    member _.ReadAll(?cancellationToken: CancellationToken) : IAsyncEnumerable<RepoChange> =
        Streaming.readAll
            out.Reader
            (fun (change: RepoChange) -> current <- change.Snapshot)
            (defaultArg cancellationToken CancellationToken.None)

    /// The most recent known snapshot — the baseline captured at `Build`, then the snapshot from
    /// each `Recv` or yielded `ReadAll` change.
    member _.Current = current

    /// The poller's health counters (re-queries run / changes emitted / skips, and what the last
    /// skip failed on). Shares `WatcherStats` with `RepoWatcher`; `WatchErrors` is always `0` here,
    /// since a poller registers no OS filesystem watch that could fail.
    member _.Stats = stats.Snapshot()

    interface IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&disposed, 1) = 0 then
                try
                    cts.Cancel()
                with :? ObjectDisposedException ->
                    // already disposed concurrently while tearing down; nothing to recover.
                    ()

                cts.Dispose()
