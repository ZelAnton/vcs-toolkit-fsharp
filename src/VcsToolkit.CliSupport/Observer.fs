namespace VcsToolkit.CliSupport

open System
open System.Text.RegularExpressions
open ProcessKit

/// Fail-closed redaction for command identities crossing the observer boundary. Wrapper clients
/// normally keep credentials in environment variables, but the raw command escape hatches accept
/// arbitrary argv, so the observer must not rely on every caller following that convention.
[<RequireQualifiedAccess>]
module internal CommandRedaction =

    let private sensitiveFlags =
        set
            [ "--access-token"
              "--access_token"
              "--api-key"
              "--api_key"
              "--apikey"
              "--auth"
              "--auth-token"
              "--auth_token"
              "--authorization"
              "--client-secret"
              "--client_secret"
              "--credential"
              "--credentials"
              "--oauth-token"
              "--oauth_token"
              "--password"
              "--passwd"
              "--pat"
              "--private-token"
              "--private_token"
              "--secret"
              "--token" ]

    let private flagName (arg: string) =
        let separator = arg.IndexOf '='

        if separator < 0 then
            arg.ToLowerInvariant()
        else
            arg.Substring(0, separator).ToLowerInvariant()

    let private isSensitiveFlag (arg: string) =
        Set.contains (flagName arg) sensitiveFlags

    let private redactText (value: string) =
        let urlUserInfo =
            Regex("(?i)(\\b[a-z][a-z0-9+.-]*://)[^/\\s@]+@", RegexOptions.CultureInvariant)

        let keyValue =
            Regex(
                "(?i)(\\b(?:access[_-]?token|api[_-]?key|auth(?:orization)?|password|passwd|pat|secret|token)=)[^&\\s]+",
                RegexOptions.CultureInvariant
            )

        let bearer =
            Regex("(?i)(\\b(?:bearer|basic)\\s+)[^\\s\\\"']+", RegexOptions.CultureInvariant)

        let knownToken =
            Regex(
                "(?i)\\b(?:github_pat_[a-z0-9_]+|gh[pousr]_[a-z0-9_]+|glpat-[a-z0-9_-]+|gitea_[a-z0-9_-]+)\\b",
                RegexOptions.CultureInvariant
            )

        let replace (regex: Regex) (replacement: string) (text: string) = regex.Replace(text, replacement)

        value
        |> replace urlUserInfo "$1***@"
        |> replace keyValue "$1***"
        |> replace bearer "$1***"
        |> replace knownToken "***"

    let private redactArgument (arg: string) =
        let separator = arg.IndexOf '='

        if separator > 0 && isSensitiveFlag arg then
            arg.Substring(0, separator + 1) + "***"
        else
            redactText arg

    /// Redact argv values before an observer or command log can see them. Long sensitive flags
    /// redact their following value; inline assignments, URL userinfo, auth headers, and known
    /// forge-token shapes are redacted in every other argument as well.
    let argv (args: string list) : string list =
        let rec loop pending acc remaining =
            match remaining with
            | [] -> List.rev acc
            | _ :: tail when pending -> loop false ("***" :: acc) tail
            | arg :: tail when isSensitiveFlag arg && arg.IndexOf '=' < 0 -> loop true (arg :: acc) tail
            | arg :: tail -> loop false (redactArgument arg :: acc) tail

        loop false [] args

/// The identity of one command execution, shared by the start and finish notifications a
/// `ICommandObserver` receives. `ManagedClient` builds and hands one to the observer around
/// every process it spawns (once per retry attempt), so a consumer can log the command, time
/// it, or diagnose why an operation is slow or failing — without dropping down into ProcessKit.
///
/// SECURITY — this record NEVER carries a secret value. It exposes only `Program`, `Argv`, and
/// `WorkingDirectory`: the CLI wrappers keep tokens out of argv by construction (a forge token
/// rides an environment variable; git HTTPS uses an env-backed credential helper whose argv
/// snippet names the secret only by env-var name), and the command's *environment* — where an
/// injected secret actually lives — is deliberately not part of the event at all. `HasSecret`
/// reports only the *fact* that this client injected a token-env secret into the command, never
/// its value.
[<NoComparison>]
type CommandEvent =
    {
        /// The program being run (`git` / `jj` / `gh` / `glab` / `tea`).
        Program: string
        /// The command's positional arguments and flags, in order. Never a secret value.
        Argv: string list
        /// The working directory the command was bound to, if any.
        WorkingDirectory: string option
        /// 0-based attempt index within the lock-contention retry sequence (0 = first try,
        /// 1 = first retry, …). It stays 0 for the non-retrying verbs.
        Attempt: int
        /// Whether this client injected a token-env secret into the command — the fact only,
        /// never the value (see the security note on this type).
        HasSecret: bool
    }

/// A diagnostic observer notified as this client's commands start and finish. Attach one with
/// `ManagedClient.WithObserver` (opt-in; default is none, i.e. no observation and zero overhead);
/// it is threaded through every wrapper client (Git/Jj/GitHub/GitLab/Gitea) the same way
/// `WithRetry`/`WithCredentials` are.
///
/// It is a plain typed callback pair rather than an `ActivitySource`/`EventSource`: the library
/// stays dependency-light and hermetically testable (a unit test attaches a recording observer
/// with no ambient-diagnostics plumbing to stand up), and a consumer can bridge these two calls
/// to `ActivitySource`, `EventSource`, `ILogger`, or a metrics sink in a handful of lines.
///
/// Both callbacks run synchronously on the execution path, so an implementation should be quick
/// and non-throwing — `ManagedClient` isolates a throwing observer so it can never fail the
/// command it observes, but a slow observer still slows the command.
type ICommandObserver =
    /// A command execution attempt is about to start.
    abstract member OnStarted: command: CommandEvent -> unit

    /// A command execution attempt finished. `duration` is its wall-clock time; `outcome` is
    /// either the process exit code (`Ok`) or the `ProcessError` it failed with (`Error`) — pass
    /// that error to the `Classify` helpers (`isLockContention`, `isTransientFetchError`, …) to
    /// categorise it.
    abstract member OnFinished: command: CommandEvent * duration: TimeSpan * outcome: Result<int, ProcessError> -> unit

/// Notification helpers that isolate a throwing observer: a diagnostic hook must never take down
/// the operation it observes. Internal — `ManagedClient` is the only caller.
[<RequireQualifiedAccess>]
module internal Observer =

    let started (obs: ICommandObserver) (ev: CommandEvent) =
        try
            obs.OnStarted ev
        with _ ->
            // A diagnostic observer is arbitrary consumer code; swallow whatever it throws — an
            // observer fault has no bearing on the process execution and must not fail (or even
            // perturb) the command being observed. There is nowhere to report it *to*: emitting
            // diagnostics is precisely the job the observer failed at.
            ()

    let finished (obs: ICommandObserver) (ev: CommandEvent) (duration: TimeSpan) (outcome: Result<int, ProcessError>) =
        try
            obs.OnFinished(ev, duration, outcome)
        with _ ->
            // See `started`: an observer exception must not propagate into the caller.
            ()
