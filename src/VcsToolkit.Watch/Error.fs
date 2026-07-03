namespace VcsToolkit.Watch

open System
open ProcessKit
open VcsToolkit.Core

/// An error from setting up or running a `RepoWatcher`: a filesystem-watcher failure plus
/// the underlying `VcsToolkit.Core` re-query errors.
[<RequireQualifiedAccess>]
type WatchError =
    /// The filesystem watcher (`FileSystemWatcher`) failed to start or register a path.
    | Notify of exn
    /// A `VcsToolkit.Core` query (detection / `Snapshot` / `LocalBranches`) failed — chiefly
    /// while *building* the watcher (capturing the baseline state). A re-query failure
    /// *during* watching is skipped and retried, not surfaced here (see `RepoWatcher`).
    | Vcs of RepoError
    /// A filesystem operation failed (e.g. resolving a worktree gitlink).
    | Io of exn

    /// Whether this wraps a **transient** failure worth retrying — a transient io/spawn error
    /// from the underlying `VcsToolkit.Core` query (delegates to `RepoError.IsTransient`), or a
    /// baseline-query **timeout** (`Io` wrapping a `TimeoutException`, raised when the startup
    /// snapshot exceeds `RequeryTimeout`): a wedged repo may un-wedge, and the loop already treats
    /// a re-query timeout as a transient skip, so `Build()` agrees. Other `Io`/`Notify` are `false`.
    member this.IsTransient =
        match this with
        | WatchError.Vcs e -> e.IsTransient
        | WatchError.Io e when (e :? TimeoutException) -> true
        | _ -> false

    /// Whether the underlying VCS binary (`git`/`jj`) **wasn't found** — a setup problem
    /// (not installed / not on `PATH`), surfaced while building the watcher's baseline.
    /// Delegates to `RepoError.IsNotFound`.
    member this.IsNotFound =
        match this with
        | WatchError.Vcs e -> e.IsNotFound
        | _ -> false

    /// The structured underlying `ProcessError`, if this error came from a VCS subprocess —
    /// flattening the two-level `Vcs (RepoError.Vcs _)` nesting so a caller (or a language
    /// binding) can read its structured fields (`program`, plus `code`/`stdout`/`stderr` on
    /// an `Exit`) without hand-walking it. `None` for a `Notify`/`Io` failure or a
    /// non-subprocess `VcsToolkit.Core` error (e.g. "not a repository").
    member this.ProcessError =
        match this with
        | WatchError.Vcs(RepoError.Vcs e) -> Some e
        | _ -> None

    /// A short, human-readable description for logs and diagnostics.
    member this.Message =
        match this with
        | WatchError.Notify e -> sprintf "filesystem watch failed: %s" e.Message
        | WatchError.Vcs e -> e.Message
        | WatchError.Io e -> e.Message
