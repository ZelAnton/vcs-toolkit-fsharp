namespace VcsToolkit.CliSupport

open System
open ProcessKit

/// The argv injection guard and the `ProcessError` classifiers shared by the CLI
/// wrappers. Auto-opened so consumers reach them as plain functions after
/// `open VcsToolkit.CliSupport` (mirroring the Rust crate's flat re-exports).
[<AutoOpen>]
module Classify =

    /// Total attempts for a transient-retried `fetch` (1 try + 2 retries).
    [<Literal>]
    let FetchAttempts = 3

    /// Fixed backoff between fetch retries.
    let FetchBackoff = TimeSpan.FromMilliseconds 500.0

    /// Grace period for a timed-out fetch: signal the process tree, wait this long
    /// for a clean exit, then hard-kill. Only takes effect with a per-client timeout.
    let FetchTimeoutGrace = TimeSpan.FromSeconds 2.0

    /// Lower-case substrings marking a merge that stopped on conflicts.
    let private conflictMarkers = [ "conflict ("; "automatic merge failed" ]

    /// Lower-case substrings marking a commit that found nothing to record.
    let private nothingToCommitMarkers =
        [ "nothing to commit"; "nothing added to commit" ]

    /// Lower-case substrings marking a transient (retryable) network/fetch failure.
    /// Timeout markers stay specific so an unrelated "timed out" (a lock wait, a hook)
    /// does not trigger a spurious fetch retry.
    let private transientFetchMarkers =
        [ "could not resolve host"
          "couldn't resolve host"
          "temporary failure in name resolution"
          "connection timed out"
          "connection refused"
          "operation timed out"
          "network is unreachable"
          "failed to connect"
          "could not read from remote repository"
          "the remote end hung up"
          "early eof"
          "rpc failed" ]

    /// Lower-case substrings marking a whole-repository / working-copy lock contention
    /// failure — another process held the one repo-wide lock, so the command never
    /// started (clean, pre-execution) and touched nothing. Per-ref lock messages are
    /// deliberately excluded: a multi-ref push/fetch can fail a ref lock after earlier
    /// refs already moved, where a retry would not be idempotent.
    let private lockContentionMarkers =
        [ "index.lock': file exists"
          "another git process seems to be running"
          "failed to lock the working copy"
          "failed to lock op heads" ]

    /// ASCII-only lowercasing, matching Rust's `to_ascii_lowercase`. Avoids the
    /// spurious matches a full-Unicode fold (`ToLowerInvariant`) could introduce
    /// (e.g. U+212A KELVIN SIGN folding to `k`); the markers are all pure ASCII.
    let private asciiLower (s: string) =
        String(
            s.ToCharArray()
            |> Array.map (fun c -> if c >= 'A' && c <= 'Z' then char (int c + 32) else c)
        )

    /// Whether `err` is a `ProcessError.Exit` whose captured output contains any marker.
    let private exitOutputMatches (err: ProcessError) (markers: string list) =
        match err with
        | ProcessError.Exit(_, _, stdout, stderr) ->
            let out = asciiLower stdout
            let errt = asciiLower stderr
            markers |> List.exists (fun m -> out.Contains m || errt.Contains m)
        | _ -> false

    /// Injection guard for bare positional argv slots: a caller value that is
    /// empty/whitespace, starts with `-` (the CLI would parse it as a flag), or
    /// contains a NUL is refused before anything spawns. Flag-VALUE slots
    /// (`-m <msg>`) are consumed verbatim and skip the check.
    let rejectFlagLike (program: string) (what: string) (value: string) : Result<unit, ProcessError> =
        let trimmed = value.Trim()
        let hasNul = value |> Seq.exists (fun ch -> ch = char 0)

        if trimmed = "" || trimmed.StartsWith("-", StringComparison.Ordinal) || hasNul then
            Error(
                ProcessError.Spawn(
                    program,
                    sprintf
                        "%s \"%s\" would be parsed as a flag (or is empty / contains NUL) — refusing to pass it as a positional argument"
                        what
                        value
                )
            )
        else
            Ok()

    /// Whether a failed merge stopped on a merge conflict. (jj surfaces conflicts as
    /// state rather than errors, so this only fires on git output.)
    let isMergeConflict (err: ProcessError) = exitOutputMatches err conflictMarkers

    /// Whether a failed commit reported nothing to commit (a clean tree), as opposed
    /// to a real error.
    let isNothingToCommit (err: ProcessError) =
        exitOutputMatches err nothingToCommitMarkers

    /// Whether a failed fetch looks transient (DNS, timeout, dropped connection) and
    /// is worth retrying. A ProcessKit-level timeout, and an io-level transient from
    /// the spawn itself, are treated as transient too.
    let isTransientFetchError (err: ProcessError) =
        match err with
        | ProcessError.Timeout _ -> true
        | _ -> ProcessError.isTransient err || exitOutputMatches err transientFetchMarkers

    /// Whether `err` is a whole-repository lock-contention failure — another process
    /// held git's index lock or jj's working-copy / op-heads lock, so the command
    /// couldn't even start. Such a failure is pre-execution and therefore safe to
    /// retry even on a mutating operation.
    let isLockContention (err: ProcessError) =
        exitOutputMatches err lockContentionMarkers
