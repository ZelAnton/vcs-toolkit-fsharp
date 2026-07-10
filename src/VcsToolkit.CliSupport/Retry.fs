namespace VcsToolkit.CliSupport

open System
open System.Threading
open System.Threading.Tasks
open ProcessKit

/// A bounded retry strategy: how many attempts, the (exponential) backoff between
/// them, and whether to add full jitter. Used by `ManagedClient` to retry
/// lock-contention failures. The default is `None` (no retry) — retry is opt-in.
[<NoComparison>]
type RetryPolicy =
    {
        /// Total attempts including the first; `1` means no retry.
        Attempts: int
        /// Delay before the first retry; doubles each subsequent retry (capped by
        /// `MaxBackoff`). `Zero` means retry immediately.
        BaseBackoff: TimeSpan
        /// Upper bound on the (pre-jitter) backoff delay. `Zero` means uncapped.
        MaxBackoff: TimeSpan
        /// Apply full jitter — the actual delay is uniform in `[0, computed]` — to
        /// avoid a thundering herd when many workers retry against one repository.
        Jitter: bool
    }

    /// No retry: a single attempt. The default.
    static member None =
        { Attempts = 1
          BaseBackoff = TimeSpan.Zero
          MaxBackoff = TimeSpan.Zero
          Jitter = false }

    /// A sensible default for repository lock contention: a handful of attempts with
    /// short, jittered, exponential backoff (25 ms -> 500 ms).
    static member LockContention =
        { Attempts = 5
          BaseBackoff = TimeSpan.FromMilliseconds 25.0
          MaxBackoff = TimeSpan.FromMilliseconds 500.0
          Jitter = true }

    /// Set the total number of attempts (clamped to at least 1).
    member this.WithAttempts(attempts: int) = { this with Attempts = max 1 attempts }

    /// Set the base backoff (the delay before the first retry).
    member this.WithBaseBackoff(backoff: TimeSpan) = { this with BaseBackoff = backoff }

    /// Cap the (pre-jitter) backoff delay; `Zero` leaves it uncapped.
    member this.WithMaxBackoff(maxBackoff: TimeSpan) = { this with MaxBackoff = maxBackoff }

    /// Toggle full jitter on the backoff delay.
    member this.WithJitter(jitter: bool) = { this with Jitter = jitter }

/// The retry executor.
[<RequireQualifiedAccess>]
module Retry =

    /// Full jitter: a uniform delay in `[0, max]`. Good enough to de-correlate
    /// retries, not cryptographic.
    let private fullJitter (maxDelay: TimeSpan) =
        if maxDelay <= TimeSpan.Zero then
            TimeSpan.Zero
        else
            // `+ 1L` makes the range inclusive of `Ticks`; guard the overflow when the
            // (saturated, pathological) delay is already `Int64.MaxValue`.
            let upper =
                if maxDelay.Ticks = Int64.MaxValue then
                    maxDelay.Ticks
                else
                    maxDelay.Ticks + 1L

            TimeSpan(Random.Shared.NextInt64 upper)

    /// The (possibly jittered) backoff before the `retryIndex`-th retry (0 = first).
    let internal backoffFor (policy: RetryPolicy) (retryIndex: int) =
        if policy.BaseBackoff <= TimeSpan.Zero then
            TimeSpan.Zero
        else
            let shift = min retryIndex 20

            // Saturate (don't wrap) on a pathological base backoff: checked multiply
            // mirrors Rust's `saturating_mul` so a huge base can't overflow to a small
            // positive value that slips under the cap.
            let scaled =
                try
                    Checked.(*) policy.BaseBackoff.Ticks (1L <<< shift)
                with :? OverflowException ->
                    Int64.MaxValue

            let capTicks =
                if policy.MaxBackoff <= TimeSpan.Zero then
                    Int64.MaxValue
                else
                    policy.MaxBackoff.Ticks

            let capped = min scaled capTicks

            let delay = TimeSpan capped

            if policy.Jitter then fullJitter delay else delay

    /// Run `op`, retrying its result while `shouldRetry` says so and `policy` has
    /// attempts left, sleeping the (jittered, exponential) backoff between tries. The
    /// op is re-invoked from scratch each attempt, so it must be idempotent for the
    /// errors `shouldRetry` selects. Returns the first `Ok`, or the last `Error`.
    ///
    /// The first attempt always runs, regardless of `ct`'s state going in — `ct` is
    /// only observed around the backoff sleep *between* attempts. A cancellation that
    /// fires during that sleep aborts immediately (it does not wait out the rest of
    /// the delay) and does NOT start another attempt; it is reported as
    /// `ProcessError.Cancelled`, mirroring the shape the ProcessKit runner itself uses
    /// for a cancelled run. `retryAsync` has no direct line to the program name being
    /// retried, so it reuses the `Program` carried by the last `ProcessError` seen
    /// (falling back to `""` on the — practically unreachable, since every case that
    /// reaches here came from a real process error — chance it carries none).
    let retryAsync
        (policy: RetryPolicy)
        (shouldRetry: ProcessError -> bool)
        (ct: CancellationToken)
        (op: unit -> Task<Result<'T, ProcessError>>)
        : Task<Result<'T, ProcessError>> =
        let attempts = max 1 policy.Attempts

        let rec attemptLoop (attempt: int) =
            task {
                match! op () with
                | Ok value -> return Ok value
                | Error err ->
                    if attempt >= attempts || not (shouldRetry err) then
                        return Error err
                    else
                        let delay = backoffFor policy (attempt - 1)

                        if delay > TimeSpan.Zero then
                            try
                                do! Task.Delay(delay, ct)
                                return! attemptLoop (attempt + 1)
                            with :? OperationCanceledException ->
                                // Cancelled mid-backoff: stop here rather than sleeping out the
                                // remainder or starting another attempt.
                                return Error(ProcessError.Cancelled(err.Program |> Option.defaultValue ""))
                        else
                            return! attemptLoop (attempt + 1)
            }

        attemptLoop 1
