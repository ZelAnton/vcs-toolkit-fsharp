namespace VcsToolkit.Core

open ProcessKit
open VcsToolkit.CliSupport

/// An error from a `Repo` operation: a thin wrapper that adds repo-detection and
/// filesystem failures on top of the underlying `ProcessError` the per-tool clients
/// return. Prefer the `Is*` classifiers to branch on intent rather than matching the
/// wrapped error's internals. This type is extensible (the Rust model is `#[non_exhaustive]`):
/// add a `| _ ->` arm if you match its cases, so a future case doesn't break your code.
[<RequireQualifiedAccess>]
type RepoError =
    /// `Repo.Open` found no `.git`/`.jj` from the start dir up to the filesystem root.
    | NotARepository of dir: string
    /// A worktree/workspace lookup by path matched no attached worktree.
    | WorktreeNotFound of path: string
    /// A filesystem operation failed (e.g. removing a workspace directory).
    | Io of message: string
    /// An underlying `git`/`jj` (i.e. ProcessKit) error, carried verbatim.
    | Vcs of ProcessError

    /// Whether this wraps a merge/rebase **conflict** from the backend — so a caller can
    /// branch on "conflict, resolve it" vs a hard failure without matching on
    /// `ProcessError` internals. (Recognises git's conflict markers; jj surfaces
    /// conflicts as state, not errors — see `Repo.InProgressState`.) Named to match the
    /// wrapper classifier `VcsToolkit.CliSupport.isMergeConflict`.
    member this.IsMergeConflict =
        match this with
        | RepoError.Vcs e -> isMergeConflict e
        | _ -> false

    /// Whether this is a benign "nothing to commit" — an empty commit attempt the caller
    /// likely wants to treat as a no-op.
    member this.IsNothingToCommit =
        match this with
        | RepoError.Vcs e -> isNothingToCommit e
        | _ -> false

    /// Whether this is a whole-repository **lock-contention** failure — another process
    /// held git's index lock or jj's working-copy/op-heads lock, so the command couldn't
    /// even start. Such a failure is pre-execution and therefore safe to retry, even on a
    /// mutating operation. `Repo.Open` builds clients with retry off, so this surfaces to
    /// the caller; branch on it to retry a higher-level flow rather than matching the
    /// wrapped error's internals. Delegates to `VcsToolkit.CliSupport.isLockContention`.
    member this.IsLockContention =
        match this with
        | RepoError.Vcs e -> isLockContention e
        | _ -> false

    /// Whether this is a **transient** fetch/network failure worth retrying (DNS,
    /// connection reset, timeout). The underlying clients already retry their own
    /// fetches; this is for retrying higher-level flows.
    member this.IsTransientFetchError =
        match this with
        | RepoError.Vcs e -> isTransientFetchError e
        | _ -> false

    /// Whether the underlying error is a **transient** io/spawn failure (interrupted /
    /// would-block / resource-busy) — delegates to `ProcessError.isTransient`. Narrower
    /// than `IsTransientFetchError` (which also treats a timeout and the network markers
    /// as retryable); use this to retry *any* operation past a momentary io hiccup. The
    /// facade's own `Io`/`NotARepository`/`WorktreeNotFound` variants are never transient.
    member this.IsTransient =
        match this with
        | RepoError.Vcs e -> ProcessError.isTransient e
        | _ -> false

    /// Whether the underlying CLI binary (`git`/`jj`) **wasn't found** — a setup problem
    /// (the tool isn't installed or isn't on `PATH`), not a repository or usage error.
    /// Lets a caller surface a "please install git/jj" hint instead of a raw spawn
    /// failure. The facade's own `Io`/`NotARepository`/`WorktreeNotFound` are never this.
    member this.IsNotFound =
        match this with
        | RepoError.Vcs e -> ProcessError.isNotFound e
        | _ -> false

    /// A short, human-readable description for logs and diagnostics.
    member this.Message =
        match this with
        | RepoError.NotARepository dir -> sprintf "no git or jj repository found at or above %s" dir
        | RepoError.WorktreeNotFound path -> sprintf "no worktree found at %s" path
        | RepoError.Io message -> message
        | RepoError.Vcs e -> e.Message

/// Lift a `Result<_, ProcessError>` from a backend client into the facade's
/// `Result<_, RepoError>`. Auto-opened so the backend mappers use it unqualified.
[<AutoOpen>]
module internal RepoInterop =

    /// Map a backend client's `Result` into the facade `Result`, wrapping the error.
    let ofVcs (r: Result<'T, ProcessError>) : Result<'T, RepoError> =
        match r with
        | Ok v -> Ok v
        | Error e -> Error(RepoError.Vcs e)
