namespace VcsToolkit.Watch

open System
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open VcsToolkit.Core

// Filesystem-watch a git/jj repository and emit typed state-change events. A `RepoWatcher`
// watches a repository's `.git`/`.jj` state directory (and, optionally, the working tree),
// **debounces** the burst of writes a VCS operation makes, **re-queries** the repo state
// through `VcsToolkit.Core`'s batched `Snapshot`, and **diffs** it against the previous
// state to yield typed `RepoEvent`s. Re-query-and-diff (rather than interpreting raw FS
// events) is what makes it robust — ref temp-file renames, `index.lock` churn, and reflog
// noise all coalesce into one "re-check the settled state".

/// Timing/capacity defaults and internal helpers.
[<AutoOpen>]
module internal Constants =

    /// Default quiet window: re-query once the watched dir has been silent this long.
    let defaultDebounce = TimeSpan.FromMilliseconds 250.0
    /// Default ceiling: even under a continuous event stream, re-query at least this often.
    let defaultMaxWait = TimeSpan.FromSeconds 1.0
    /// Upper clamp for `maxWait`, so an "effectively unbounded" caller value (a huge
    /// `TimeSpan` to disable the ceiling) can't wedge the loop.
    let maxWaitCeiling = TimeSpan.FromDays 365.0
    /// Upper clamp for any single `Task.Delay` — it throws above ~49.7 days
    /// (`Timer.MaxSupportedTimeout`); 24 days is safely under and larger than any sane
    /// debounce/requery timeout. A huge caller value is clamped, not crashed.
    let maxTimerDelay = TimeSpan.FromDays 24.0
    /// Default deadline on a single re-query.
    let defaultRequeryTimeout = TimeSpan.FromSeconds 30.0
    /// Bounded output channel: a slow consumer applies backpressure (the loop pauses
    /// re-querying), and pending filesystem signals coalesce into one catch-up query.
    let outputCapacity = 64

/// What the last skipped re-query failed on (see `WatcherStats.LastError`).
[<RequireQualifiedAccess>]
type WatcherErrorKind =
    /// The snapshot re-query returned an error (e.g. a transiently held lock).
    | Snapshot
    /// The branch-list re-query returned an error.
    | Branches
    /// The re-query exceeded the configured `requeryTimeout` and was abandoned.
    | Timeout

/// A cheap point-in-time copy of the watcher's health counters — see `RepoWatcher.Stats`.
/// Lets a long-running consumer notice a watcher that is silently skipping re-queries.
type WatcherStats =
    {
        /// Re-query attempts started (settled bursts that reached the query step).
        Requeries: uint64
        /// Re-queries that emitted a `RepoChange` (the rest found no difference).
        Changes: uint64
        /// Re-queries skipped — transient query failures plus deadline overruns.
        Skipped: uint64
        /// What the most recent skip failed on; `None` when nothing was ever skipped.
        LastError: WatcherErrorKind option
        /// Filesystem-watch **errors** reported by the OS backend. A non-zero — especially
        /// *climbing* — count means the underlying watch is failing (most often the watched
        /// `.git`/`.jj` directory was removed and re-created, which invalidates the watch).
        /// The watcher does **not** auto-re-register; treat a rising count as "rebuild the
        /// watcher". Best-effort and platform-dependent.
        WatchErrors: uint64
    }

/// Lock-free counter cell shared between the loop and `Stats` readers. Relaxed reads/writes
/// are enough: independent monotonic telemetry, not a synchronization protocol.
type internal StatsInner() =
    let mutable requeries = 0L
    let mutable changes = 0L
    let mutable skipped = 0L
    let mutable lastError = 0 // 0 = none, 1 = Snapshot, 2 = Branches, 3 = Timeout
    let mutable watchErrors = 0L

    member _.NoteRequery() =
        Interlocked.Increment(&requeries) |> ignore

    member _.NoteChange() =
        Interlocked.Increment(&changes) |> ignore

    member _.NoteWatchError() =
        Interlocked.Increment(&watchErrors) |> ignore

    member _.NoteSkip(kind: WatcherErrorKind) =
        Interlocked.Increment(&skipped) |> ignore

        let code =
            match kind with
            | WatcherErrorKind.Snapshot -> 1
            | WatcherErrorKind.Branches -> 2
            | WatcherErrorKind.Timeout -> 3

        Volatile.Write(&lastError, code)

    member _.Snapshot() : WatcherStats =
        let lastErr =
            match Volatile.Read(&lastError) with
            | 1 -> Some WatcherErrorKind.Snapshot
            | 2 -> Some WatcherErrorKind.Branches
            | 3 -> Some WatcherErrorKind.Timeout
            | _ -> None

        { Requeries = uint64 (Volatile.Read(&requeries))
          Changes = uint64 (Volatile.Read(&changes))
          Skipped = uint64 (Volatile.Read(&skipped))
          LastError = lastErr
          WatchErrors = uint64 (Volatile.Read(&watchErrors)) }

/// The timing/capacity knobs the background loop runs under.
type internal LoopConfig =
    {
        Debounce: TimeSpan
        MaxWait: TimeSpan
        /// `None` disables the per-re-query deadline.
        RequeryTimeout: TimeSpan option
        OutputCapacity: int
    }

/// State-directory resolution (git gitlinks + worktree `commondir`).
module internal Paths =

    /// Resolve `.`/`..` lexically (no filesystem access) to an absolute path.
    let lexicallyNormalized (p: string) : string = Path.GetFullPath p

    /// Best-effort absolute form for dedup comparison; falls back to the input.
    let normalize (p: string) : string =
        try
            Path.GetFullPath p
        with _ ->
            // an un-normalisable path (e.g. invalid chars) — compare it byte-for-byte
            // instead; equal spellings still dedup, distinct ones stay distinct.
            p

    /// Ordinal path equality on the normalized forms — conservatively *distinct* on any
    /// doubt (a false "distinct" only double-watches, which is harmless; a false "equal"
    /// would drop a needed watch).
    let pathsEqual (a: string) (b: string) =
        String.Equals(normalize a, normalize b, StringComparison.Ordinal)

    /// Whether `child` is at or under `parent` (ordinal, normalized).
    let pathStartsWith (child: string) (parent: string) : bool =
        let c = normalize child

        let p =
            (normalize parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

        String.Equals(c, p, StringComparison.Ordinal)
        || c.StartsWith(p + string Path.DirectorySeparatorChar, StringComparison.Ordinal)

    /// The directory to watch for a backend: `.jj` for jj, `.git` for git. A worktree's
    /// `.git` is a gitlink *file* (`gitdir: <path>`); resolve it to the real git directory.
    let stateDir (kind: BackendKind) (root: string) : Result<string, WatchError> =
        match kind with
        | BackendKind.Jj -> Ok(Path.Combine(root, ".jj"))
        | BackendKind.Git ->
            let dotGit = Path.Combine(root, ".git")

            if File.Exists dotGit then
                try
                    let content = File.ReadAllText(dotGit).Trim()

                    if content.StartsWith("gitdir:", StringComparison.Ordinal) then
                        let rest = content.Substring(7).Trim()

                        Ok(
                            if Path.IsPathRooted rest then
                                rest
                            else
                                Path.Combine(root, rest)
                        )
                    else
                        Ok dotGit
                with e ->
                    Error(WatchError.Io e)
            else
                Ok dotGit

    /// The **shared** git directory for a linked worktree, or `None` for a plain repo. A
    /// linked worktree's resolved gitdir holds a `commondir` file whose content is a path
    /// (typically relative, e.g. `../..`) to the shared `.git`, where `refs/heads/*` and
    /// `packed-refs` live.
    let commonDir (stateDir: string) : string option =
        let commondir = Path.Combine(stateDir, "commondir")

        try
            if not (File.Exists commondir) then
                None
            else
                let rel = File.ReadAllText(commondir).Trim()

                if rel = "" then
                    None
                else
                    let joined =
                        if Path.IsPathRooted rel then
                            rel
                        else
                            Path.Combine(stateDir, rel)

                    Some(lexicallyNormalized joined)
        with _ ->
            // best-effort: an unreadable/absent commondir means "no shared dir" (a plain
            // repo), so behaviour falls back to the single state-dir watch.
            None

    /// The directories to watch for a backend, deduplicated. Normally one (the state dir);
    /// a linked git worktree adds its shared git dir (where branch create/delete lands).
    let stateDirs (kind: BackendKind) (root: string) : Result<string list, WatchError> =
        match stateDir kind root with
        | Error e -> Error e
        | Ok sd ->
            match commonDir sd with
            | Some shared when not (pathsEqual shared sd) -> Ok [ sd; shared ]
            | _ -> Ok [ sd ]

/// The debounce → ceiling → re-query → diff pipeline, plus the FileSystemWatcher bridge.
module internal Loop =

    /// Drop every already-queued signal — the burst is one observation.
    let drain (reader: ChannelReader<unit>) =
        let mutable more = true

        while more do
            match reader.TryRead() with
            | true, _ -> ()
            | false, _ -> more <- false

    /// Settle the burst: coalesce signals with a `debounce` quiet window, capped at
    /// `maxWait`. A single timer of `min(debounce, remaining-to-ceiling)` suffices — a timer
    /// win always settles (quiet window *or* ceiling), only a fresh signal continues — which
    /// also keeps the delay small (no huge `Task.Delay`). Returns `true` to re-query,
    /// `false` if the signal channel closed.
    let settleBurst (reader: ChannelReader<unit>) (config: LoopConfig) (ct: CancellationToken) : Task<bool> =
        task {
            let sw = System.Diagnostics.Stopwatch.StartNew()
            // Clamp to [0, maxTimerDelay]: `TimeSpan` is signed (unlike Rust's `Duration`), so a
            // negative value would make `Task.Delay` throw; a > ~49.7-day value would too.
            let deadlineMs =
                max 0L (int64 (min config.MaxWait maxWaitCeiling).TotalMilliseconds)

            let debounceMs =
                max 0L (int64 (min config.Debounce maxTimerDelay).TotalMilliseconds)

            let mutable settled = false
            let mutable requery = true

            while not settled do
                let remaining = deadlineMs - sw.ElapsedMilliseconds

                if remaining <= 0L then
                    settled <- true // ceiling reached
                else
                    let waitMs = min debounceMs remaining // ≤ debounce; never huge
                    use iterCts = CancellationTokenSource.CreateLinkedTokenSource ct
                    let signalTask = reader.WaitToReadAsync(ct).AsTask()
                    let timerTask = Task.Delay(TimeSpan.FromMilliseconds(float waitMs), iterCts.Token)
                    let! _ = Task.WhenAny(signalTask, timerTask)
                    // Cancel the losing timer (biased toward the signal below).
                    iterCts.Cancel()

                    if signalTask.IsCompletedSuccessfully then
                        if not signalTask.Result then
                            settled <- true
                            requery <- false // channel closed
                        else
                            drain reader
                    // else: a new signal — loop resets the quiet window (ceiling clock runs on)
                    else
                        settled <- true // timer won (quiet window or ceiling) → settle

            return requery
        }

    /// Re-query the settled state (`Snapshot` + `LocalBranches`), bounded by the configured
    /// deadline. Returns `Choice1Of2 (snapshot, branches)` on success, or `Choice2Of2 kind`
    /// on a skip (query error / timeout).
    ///
    /// **Timeout is best-effort on .NET:** it stops the loop *waiting*, but cannot kill the
    /// in-flight git/jj process (the `Core.Snapshot` API takes no `CancellationToken`). The
    /// overrun query runs to completion in the background, its result discarded; configure
    /// the underlying client's `DefaultTimeout` to hard-bound a truly wedged command.
    let requeryOnce
        (repo: Repo)
        (config: LoopConfig)
        (ct: CancellationToken)
        : Task<Choice<RepoSnapshot * string list, WatcherErrorKind>> =
        task {
            let work =
                task {
                    match! repo.Snapshot() with
                    | Error _ -> return Choice2Of2 WatcherErrorKind.Snapshot
                    | Ok snapshot ->
                        match! repo.LocalBranches() with
                        | Error _ -> return Choice2Of2 WatcherErrorKind.Branches
                        | Ok branches -> return Choice1Of2(snapshot, branches)
                }

            match config.RequeryTimeout with
            | None -> return! work
            | Some limit ->
                // Clamp to [0, maxTimerDelay] (signed TimeSpan, and Task.Delay throws above
                // ~49.7 days) so an out-of-range deadline can't throw.
                let limit =
                    if limit < TimeSpan.Zero then TimeSpan.Zero
                    elif limit > maxTimerDelay then maxTimerDelay
                    else limit

                use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource ct
                let timeoutTask = Task.Delay(limit, timeoutCts.Token)
                let! winner = Task.WhenAny(work :> Task, timeoutTask)

                if Object.ReferenceEquals(winner, timeoutTask) then
                    return Choice2Of2 WatcherErrorKind.Timeout
                else
                    timeoutCts.Cancel() // stop the timer; work already completed
                    return! work
        }

    /// The background loop: coalesce a burst of filesystem signals, re-query the settled
    /// state, diff against the previous, and emit a `RepoChange` when anything changed.
    let watchLoop
        (repo: Repo)
        (raw: Channel<unit>)
        (out: Channel<RepoChange>)
        (initialPrev: WatchState)
        (config: LoopConfig)
        (stats: StatsInner)
        (ct: CancellationToken)
        : Task =
        task {
            let mutable prev = initialPrev

            try
                try
                    let mutable running = true

                    while running do
                        // Block until the first signal (or exit when the channel/loop closes).
                        let! hasFirst = raw.Reader.WaitToReadAsync ct

                        if not hasFirst || ct.IsCancellationRequested then
                            running <- false
                        else
                            drain raw.Reader
                            let! requery = settleBurst raw.Reader config ct

                            if not requery || ct.IsCancellationRequested then
                                running <- false
                            else
                                stats.NoteRequery()
                                let! outcome = requeryOnce repo config ct

                                match outcome with
                                | Choice2Of2 kind -> stats.NoteSkip kind
                                | Choice1Of2(snapshot, branches) ->
                                    let next = WatchState.fromSnapshot snapshot branches
                                    let events = Diff.diff prev next
                                    prev <- next

                                    if not (List.isEmpty events) then
                                        do! out.Writer.WriteAsync({ Snapshot = snapshot; Events = events }, ct)
                                        stats.NoteChange()
                with
                | :? OperationCanceledException ->
                    // the watcher was disposed (ct cancelled) — clean shutdown.
                    ()
                | :? ChannelClosedException ->
                    // the output receiver was dropped — stop.
                    ()
            finally
                // Always close the output channel — however the loop ends (incl. an
                // unexpected throw) — so a pending `Recv` returns None instead of hanging
                // forever (Rust drops the sender on task exit, which closes the channel).
                out.Writer.TryComplete() |> ignore
        }

    /// A `FileSystemWatcher` over `dir` (recursive) that pushes a re-check signal on any
    /// event, and counts a backend error before signalling a re-check.
    let makeWatcher (dir: string) (onSignal: unit -> unit) (onWatchError: unit -> unit) : FileSystemWatcher =
        let fsw = new FileSystemWatcher(dir)
        fsw.IncludeSubdirectories <- true

        fsw.NotifyFilter <-
            NotifyFilters.FileName
            ||| NotifyFilters.DirectoryName
            ||| NotifyFilters.LastWrite
            ||| NotifyFilters.Size
            ||| NotifyFilters.CreationTime
            ||| NotifyFilters.Attributes

        fsw.Changed.Add(fun _ -> onSignal ())
        fsw.Created.Add(fun _ -> onSignal ())
        fsw.Deleted.Add(fun _ -> onSignal ())
        fsw.Renamed.Add(fun _ -> onSignal ())

        fsw.Error.Add(fun _ ->
            onWatchError ()
            onSignal ())

        fsw.EnableRaisingEvents <- true
        fsw

/// Builder for a `RepoWatcher` — set the watch scope and debounce timing, then `Build`.
[<Sealed>]
type Builder
    internal (repo: Repo, workingTree: bool, debounce: TimeSpan, maxWait: TimeSpan, requeryTimeout: TimeSpan option) =

    /// Also watch the **working tree** recursively, so a bare unstaged edit fires
    /// `WorkingCopyChanged` immediately. Off by default (only the `.git`/`.jj` state dir).
    /// Note: `FileSystemWatcher` is `.gitignore`-unaware, so this also watches ignored and
    /// build directories — heavier on a large tree.
    member _.WorkingTree(yes: bool) =
        Builder(repo, yes, debounce, maxWait, requeryTimeout)

    /// The quiet window: re-query once the watched dir has been silent this long after the
    /// last event (default 250 ms).
    member _.Debounce(window: TimeSpan) =
        Builder(repo, workingTree, window, maxWait, requeryTimeout)

    /// The ceiling on how long a continuous event stream defers the re-query (default 1 s).
    member _.MaxWait(ceiling: TimeSpan) =
        Builder(repo, workingTree, debounce, ceiling, requeryTimeout)

    /// Deadline on a single re-query (default 30 s); `None` disables it. Orthogonal to
    /// `MaxWait`. See `RepoWatcher` on the best-effort nature of this on .NET.
    member _.RequeryTimeout(timeout: TimeSpan option) =
        Builder(repo, workingTree, debounce, maxWait, timeout)

    /// Start watching. Captures the baseline state, registers the filesystem watch, and
    /// starts the background re-query task. Returns the built `RepoWatcher`, or the setup
    /// error (missing binary / unreadable state dir / watch-registration failure).
    member _.Build() : Task<Result<RepoWatcher, WatchError>> =
        task {
            match Paths.stateDirs repo.Kind repo.Root with
            | Error e -> return Error e
            | Ok dirs ->
                let raw = Channel.CreateUnbounded<unit>()
                let stats = StatsInner()
                let onSignal () = raw.Writer.TryWrite(()) |> ignore
                let onWatchError () = stats.NoteWatchError()
                let watchers = ResizeArray<FileSystemWatcher>()

                let registered =
                    try
                        if workingTree then
                            watchers.Add(Loop.makeWatcher repo.Root onSignal onWatchError)

                            for d in dirs do
                                if not (Paths.pathStartsWith d repo.Root) then
                                    watchers.Add(Loop.makeWatcher d onSignal onWatchError)
                        else
                            for d in dirs do
                                watchers.Add(Loop.makeWatcher d onSignal onWatchError)

                        Ok()
                    with e ->
                        for w in watchers do
                            w.Dispose()

                        Error(WatchError.Notify e)

                let disposeWatchers () =
                    for w in watchers do
                        w.Dispose()

                match registered with
                | Error e -> return Error e
                | Ok() ->
                    // Register paths BEFORE the baseline snapshot, so a change racing the
                    // baseline is queued (not lost). A baseline-query failure — whether a
                    // returned `Error` or an unexpected throw — must dispose the watchers so a
                    // failed build never leaks an OS watch pushing into the dropped channel.
                    let! baseline =
                        task {
                            try
                                match! repo.Snapshot() with
                                | Error e -> return Error(WatchError.Vcs e)
                                | Ok snapshot ->
                                    match! repo.LocalBranches() with
                                    | Error e -> return Error(WatchError.Vcs e)
                                    | Ok branches -> return Ok(snapshot, branches)
                            with e ->
                                // `reraise` can't be used inside a `task` CE handler; rethrow
                                // via ExceptionDispatchInfo to preserve the original stack.
                                disposeWatchers ()
                                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw e
                                return Unchecked.defaultof<_>
                        }

                    match baseline with
                    | Error e ->
                        disposeWatchers ()
                        return Error e
                    | Ok(snapshot, branches) ->
                        let prev = WatchState.fromSnapshot snapshot branches

                        let config =
                            { Debounce = debounce
                              MaxWait = maxWait
                              RequeryTimeout = requeryTimeout
                              OutputCapacity = outputCapacity }

                        let out =
                            Channel.CreateBounded<RepoChange>(
                                BoundedChannelOptions(config.OutputCapacity, FullMode = BoundedChannelFullMode.Wait)
                            )

                        let cts = new CancellationTokenSource()
                        let loopTask = Loop.watchLoop repo raw out prev config stats cts.Token
                        return Ok(new RepoWatcher(out, snapshot, stats, watchers, cts, loopTask))
        }

/// A live watch over a repository, yielding `RepoChange`s as the repo's state changes.
/// `Dispose` stops the filesystem watch and the background task.
and [<Sealed>] RepoWatcher
    internal
    (
        out: Channel<RepoChange>,
        baseline: RepoSnapshot,
        stats: StatsInner,
        watchers: ResizeArray<FileSystemWatcher>,
        cts: CancellationTokenSource,
        // The running loop is rooted by the task scheduler and stopped via `cts`, so the
        // handle needn't be held; named `_` to say so.
        _loopTask: Task
    ) =

    let mutable current = baseline
    let mutable disposed = false

    /// The default per-re-query deadline (30 s) used unless overridden via the builder.
    static member DefaultRequeryTimeout = defaultRequeryTimeout

    /// A builder over `repo`.
    static member Builder(repo: Repo) : Builder =
        Builder(repo, false, defaultDebounce, defaultMaxWait, Some defaultRequeryTimeout)

    /// Start watching `repo` with the defaults (state dir only, 250 ms debounce).
    static member Watch(repo: Repo) : Task<Result<RepoWatcher, WatchError>> = RepoWatcher.Builder(repo).Build()

    /// Await the next settled change. Returns `None` once the watcher is disposed or its
    /// background task ends.
    member _.Recv() : Task<RepoChange option> =
        task {
            try
                let! change = out.Reader.ReadAsync()
                current <- change.Snapshot
                return Some change
            with :? ChannelClosedException ->
                // the loop ended (disposed / task done) — no more changes.
                return None
        }

    /// The most recent known snapshot — the baseline captured at `Build`, then the snapshot
    /// from each `Recv`. It advances **only when you call `Recv`**.
    member _.Current = current

    /// The watcher's health counters (re-queries run / changes emitted / skips, what the
    /// last skip failed on, and OS-watch errors). Cheap relaxed-atomic reads.
    member _.Stats = stats.Snapshot()

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true

                for w in watchers do
                    w.Dispose()

                try
                    cts.Cancel()
                with :? ObjectDisposedException ->
                    // already disposed concurrently while tearing down; nothing to recover.
                    ()

                cts.Dispose()
