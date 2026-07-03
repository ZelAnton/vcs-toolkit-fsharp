namespace VcsToolkit.Forge

open System
open ProcessKit
open VcsToolkit.CliSupport

/// Lowercase phrase markers for classifying a forge CLI's output.
[<AutoOpen>]
module private ForgeErrorMarkers =

    /// **Authentication** failure markers (`gh`/`glab`/`tea`). Phrase-based and
    /// conservative; status-qualified (`http 401`, not a bare `401` that a PR number
    /// could echo). Strictly auth (401 / bad-or-missing token / not-logged-in) — a 403
    /// permission refusal stays a generic error.
    let authMarkers =
        [ "unauthorized"
          "http 401"
          "bad credentials"
          "requires authentication"
          "authentication required"
          "authentication failed"
          "not logged in"
          "auth login" ]

    /// **Rate-limit** failure markers. Keyed on the message, not the ambiguous 403.
    let rateLimitMarkers =
        [ "rate limit"
          "http 429"
          "too many requests"
          "retry-after"
          "abuse detection" ]

/// An error from a `Forge` operation: the underlying `ProcessError` the wrapper clients
/// return, plus `Unsupported` (an operation a forge's CLI does not provide) and
/// `InvalidInput` (caller input refused before any spawn).
[<RequireQualifiedAccess>]
type ForgeError =
    /// An underlying GitHub/GitLab/Gitea (i.e. ProcessKit) error, carried verbatim.
    | Forge of ProcessError
    /// The operation isn't available on this forge's CLI — e.g. `repoView` /
    /// `prMarkReady` / `prChecks` / `releaseView` on Gitea. `operation` is the method name.
    | Unsupported of forge: ForgeKind * operation: string
    /// The caller's input was refused by the facade before any CLI spawn — e.g. `prEdit`
    /// with both `Title` and `Body` `None`. Carries a short message naming what was wrong.
    | InvalidInput of string

    /// Lowercased `stdout`+`stderr` of an underlying non-zero `Exit` — the CLI's message
    /// body, for marker classification. `None` for non-`Exit` errors and the facade's own
    /// variants, which carry no CLI message.
    member private this.CliOutput =
        match this with
        | ForgeError.Forge(ProcessError.Exit(_, _, stdout, stderr)) -> Some((stdout + "\n" + stderr).ToLowerInvariant())
        | _ -> None

    /// Whether the forge CLI reported an **authentication** failure — a missing, expired,
    /// or invalid token, or "not logged in" — as opposed to a transient network error or
    /// a generic non-zero exit. Lets a caller surface a dedicated auth error.
    member this.IsUnauthorized =
        match this.CliOutput with
        | Some out -> authMarkers |> List.exists (fun m -> out.Contains(m, StringComparison.Ordinal))
        | None -> false

    /// Whether the forge CLI was **rate-limited** (HTTP 429, "API rate limit exceeded",
    /// or a secondary/abuse limit) — a back-off-and-retry-later signal, distinct from a
    /// transient network blip.
    member this.IsRateLimited =
        match this.CliOutput with
        | Some out ->
            rateLimitMarkers
            |> List.exists (fun m -> out.Contains(m, StringComparison.Ordinal))
        | None -> false

    /// Whether this is a **transient** network failure worth retrying (DNS, connection
    /// reset, timeout) — forge commands are network-bound, so a higher flow may retry.
    member this.IsTransientFetchError =
        match this with
        | ForgeError.Forge e -> isTransientFetchError e
        | _ -> false

    /// Whether the underlying error is a **transient io/spawn** failure (interrupted /
    /// would-block / resource-busy). Narrower than `IsTransientFetchError`.
    member this.IsTransient =
        match this with
        | ForgeError.Forge e -> ProcessError.isTransient e
        | _ -> false

    /// Whether the underlying forge CLI binary (`gh`/`glab`/`tea`) **wasn't found** — a
    /// setup problem, not a usage or network error. Lets a caller surface an "install
    /// gh/glab/tea" hint.
    member this.IsNotFound =
        match this with
        | ForgeError.Forge e -> ProcessError.isNotFound e
        | _ -> false

    // (Whether this is an `Unsupported` operation is the compiler-generated `IsUnsupported`
    // case tester — no custom member needed.)

    /// A short, human-readable description for logs and diagnostics.
    member this.Message =
        match this with
        | ForgeError.Forge e -> e.Message
        | ForgeError.Unsupported(forge, operation) -> sprintf "%s does not support `%s`" forge.AsString operation
        | ForgeError.InvalidInput msg -> msg

/// Lift a `Result<_, ProcessError>` from a wrapper client into the facade's Result.
/// Auto-opened so the backend adapters use it unqualified.
[<AutoOpen>]
module internal ForgeInterop =

    /// Map a wrapper client's `Result` into the facade `Result`, wrapping the error.
    let ofForge (r: Result<'T, ProcessError>) : Result<'T, ForgeError> =
        match r with
        | Ok v -> Ok v
        | Error e -> Error(ForgeError.Forge e)
