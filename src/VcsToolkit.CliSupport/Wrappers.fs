namespace VcsToolkit.CliSupport

open System.IO
open System.Text
open System.Threading.Tasks
open ProcessKit

/// Plumbing shared by the CLI wrapper clients (Git/Jj/GitHub/GitLab/Gitea): the argv-guard
/// fan-out, the parse-error adapter, clone-destination cleanup, and untrimmed stdout capture in
/// both a UTF-8 `string` form and a verbatim raw-`byte[]` form.
/// Each was previously copied per client, differing only in a closed-over `BINARY` constant — so
/// every fix had to be repeated N times and the copies had already begun to drift. The single
/// implementations here take the driven program name as a `program` parameter instead, so one
/// definition serves every client. Auto-opened, so consumers reach them as plain functions after
/// `open VcsToolkit.CliSupport` (mirroring `Classify`'s flat re-exports).
[<AutoOpen>]
module Wrappers =

    /// Apply the argv injection guard (`rejectFlagLike`) to each `(what, value)` pair,
    /// short-circuiting on the first refusal. `program` names the driven CLI in the refusal
    /// message — pass the client's `BINARY` (`git`/`jj`/`gh`/`glab`/`tea`).
    let checkFlags (program: string) (checks: (string * string) list) : Result<unit, ProcessError> =
        let bad =
            checks
            |> List.tryPick (fun (what, value) ->
                match rejectFlagLike program what value with
                | Error e -> Some e
                | Ok() -> None)

        match bad with
        | Some e -> Error e
        | None -> Ok()

    /// Map a parser's `Result<_, string>` error message into a `ProcessError.Parse` naming
    /// `program` (the client's `BINARY`).
    let mapParse (program: string) (r: Result<'T, string>) : Result<'T, ProcessError> =
        match r with
        | Ok v -> Ok v
        | Error m -> Error(ProcessError.Parse(program, m))

    /// R7: whether `dest` is one a clone could have *created* — absent, unreadable, or an empty
    /// directory — as opposed to a non-empty pre-existing dir (the caller's data, which git/jj
    /// refuses to clone into). Captured **before** the clone so a failure can clean only its own
    /// partial output.
    let cloneDestCleanable (dest: string) : bool =
        try
            (not (Directory.Exists dest))
            || (Directory.EnumerateFileSystemEntries dest |> Seq.isEmpty)
        with _ ->
            // Unreadable → the clone would create/populate it; treat as cleanable.
            true

    /// R7: on a failed clone into a `cleanable` `dest`, best-effort remove the partial output so a
    /// retry isn't blocked by "destination path already exists and is not empty". A timeout grace
    /// alone can't prevent the partial (Windows' job-kill is atomic; the Unix grace is too short
    /// for a multi-GB partial). Never touches a non-`cleanable` dest (the caller's data).
    let cloneCleanupOnError (dest: string) (cleanable: bool) (result: Result<unit, ProcessError>) =
        match result with
        | Error _ when cleanable ->
            try
                if Directory.Exists dest then
                    Directory.Delete(dest, true)
            with _ ->
                // Best-effort cleanup on the error path; a leftover partial is not fatal.
                ()
        | _ -> ()

        result

    /// Run `cmd` on `core` returning **untrimmed** stdout as raw, verbatim **bytes** (unlike
    /// `core.Run`, which trims the trailing newline). This is the genuinely byte-exact capture:
    /// arbitrary content — a binary or legacy/non-UTF-8-encoded blob — round-trips byte-for-byte.
    /// Use it for a byte-exact read-modify-write of blob content that may not be UTF-8 text; for
    /// git/jj *text* output (diffs, templates, blame porcelain) `runUntrimmed` is more convenient.
    let runUntrimmedBytes (core: ManagedClient) (cmd: Command) : Task<Result<byte[], ProcessError>> =
        // Capture via `OutputBytes`, not the string verb: the latter reconstructs stdout from lines
        // and drops the trailing newline, which would defeat the verbatim capture.
        task {
            match! core.OutputBytes cmd with
            | Error e -> return Error e
            | Ok res ->
                match ProcessResult.ensureSuccess res with
                | Error e -> return Error e
                | Ok ok -> return Ok ok.Stdout
        }

    /// Run `cmd` on `core` returning **untrimmed** stdout as a `string` (unlike `core.Run`, which
    /// trims the trailing newline) — for git/jj text output where a trailing newline is
    /// significant: a diff's trailing blank context line keeps the last hunk's `@@` count valid on
    /// re-parse/re-apply, and a text blob's trailing newline survives a read-modify-write.
    ///
    /// The captured bytes are UTF-8-decoded, so this is byte-exact **only for UTF-8/text content**:
    /// any non-UTF-8 byte (a binary or legacy-encoded blob) is silently replaced with U+FFFD and
    /// does NOT round-trip. For a truly verbatim, byte-for-byte read of arbitrary content use
    /// `runUntrimmedBytes` (and the `*Bytes` client members built on it).
    let runUntrimmed (core: ManagedClient) (cmd: Command) : Task<Result<string, ProcessError>> =
        task {
            match! runUntrimmedBytes core cmd with
            | Error e -> return Error e
            | Ok bytes -> return Ok(Encoding.UTF8.GetString bytes)
        }
