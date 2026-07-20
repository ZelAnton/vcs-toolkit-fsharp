namespace VcsToolkit.Mcp

open System
open System.IO
open ProcessKit
open VcsToolkit.CliSupport

/// Where `--log-commands` diagnostic lines go.
[<RequireQualifiedAccess>]
type internal LogSink =
    /// The server process's stderr — free for diagnostics, since stdout carries the MCP
    /// JSON-RPC protocol.
    | Stderr
    /// Append to the file at this path.
    | File of path: string

/// The `--log-commands` diagnostic sink: an `ICommandObserver` that writes one line per
/// command start/finish to a `TextWriter`, plus the pure line formatters it is built on (kept
/// separate so the exact line shape is unit-testable without spawning a real process). Wired
/// into the git/jj/forge clients only when `--log-commands` is given — see `Program.fs`.
[<RequireQualifiedAccess>]
module internal CommandLog =

    /// Render argv as a bracketed, double-quoted list — diagnostic text, not machine-parsed,
    /// so the escaping only needs to keep an embedded quote from looking like a boundary.
    let private renderArgv (argv: string list) : string =
        argv
        |> List.map (fun a -> "\"" + a.Replace("\"", "\\\"") + "\"")
        |> String.concat " "
        |> sprintf "[%s]"

    let private cwdOrDash (cwd: string option) : string = cwd |> Option.defaultValue "-"

    /// The line logged when a command execution attempt is about to start.
    let formatStarted (ev: CommandEvent) : string =
        sprintf
            "vcs-mcp: start program=%s argv=%s cwd=%s attempt=%d"
            ev.Program
            (renderArgv ev.Argv)
            (cwdOrDash ev.WorkingDirectory)
            ev.Attempt

    /// The line logged when a command execution attempt finished — successfully (`ok(<exit
    /// code>)`) or with a classified `ProcessError` (`error(<message>)`, via `ProcessError.Message`,
    /// which never carries a secret — see `CommandEvent`'s security note).
    let formatFinished (ev: CommandEvent) (duration: TimeSpan) (outcome: Result<int, ProcessError>) : string =
        let outcomeText =
            match outcome with
            | Ok code -> sprintf "ok(%d)" code
            | Error err -> sprintf "error(%s)" err.Message

        sprintf
            "vcs-mcp: done  program=%s argv=%s cwd=%s attempt=%d duration=%dms outcome=%s"
            ev.Program
            (renderArgv ev.Argv)
            (cwdOrDash ev.WorkingDirectory)
            ev.Attempt
            (int64 duration.TotalMilliseconds)
            outcomeText

    /// An `ICommandObserver` that writes `formatStarted`/`formatFinished` lines to `writer`,
    /// flushed immediately after each line — a buffered writer could lose the log's tail on
    /// exactly the abrupt exit (a hang killed, a crash) this diagnostic exists to explain.
    /// Writes are serialized with a lock: `ManagedClient` may call this concurrently across
    /// commands issued by different clients (git/jj/forge) sharing one sink.
    [<Sealed>]
    type Writer(writer: TextWriter) =
        let gate = obj ()

        interface ICommandObserver with
            member _.OnStarted(ev) =
                lock gate (fun () ->
                    writer.WriteLine(formatStarted ev)
                    writer.Flush())

            member _.OnFinished(ev, duration, outcome) =
                lock gate (fun () ->
                    writer.WriteLine(formatFinished ev duration outcome)
                    writer.Flush())
