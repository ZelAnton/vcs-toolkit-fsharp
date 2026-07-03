namespace VcsToolkit.Watch

open VcsToolkit.Core

// The typed events and the **pure** snapshot-diff that derives them. The watcher
// re-queries repo state on each filesystem change and diffs the new state against the old;
// `diff` turns a (previous, next) pair into the list of `RepoEvent`s that changed. Pure
// data in, pure data out — no filesystem, no process, no async — so the load-bearing logic
// is hermetically unit-tested.

/// One typed change to a repository's observable state, derived by diffing two consecutive
/// `RepoSnapshot`s (plus the branch set).
[<RequireQualifiedAccess>]
type RepoEvent =
    /// The working-copy commit moved (a commit, checkout, reset, `jj` op, …). `From`/`To`
    /// are the full object ids; `None` on an unborn git repo.
    | HeadMoved of From: string option * To: string option
    /// The *current* branch (git) / bookmark (jj) changed — a switch/checkout, or going
    /// (in)to a detached/unset state (`None`).
    | BranchSwitched of From: string option * To: string option
    /// A local branch/bookmark appeared.
    | BranchCreated of CreatedName: string
    /// A local branch/bookmark was removed.
    | BranchDeleted of DeletedName: string
    /// The working-copy dirtiness or change count changed (an edit was staged, committed,
    /// stashed, snapshotted, …).
    | WorkingCopyChanged of Dirty: bool * ChangeCount: uint64
    /// The upstream tracking branch changed (git only; always absent on jj).
    | UpstreamChanged of Upstream: string option
    /// The ahead/behind counts versus the upstream changed (git only).
    | AheadBehindChanged of Ahead: uint64 option * Behind: uint64 option
    /// The in-progress **operation** changed — a git merge or rebase started or finished. A
    /// transition to/from `OperationState.Conflict` (jj's conflict marker) is **not**
    /// reported here (`ConflictChanged` already signals it on both backends), so this fires
    /// only on git, with `From`/`To` in `Clear`/`Merge`/`Rebase`.
    | OperationChanged of From: OperationState * To: OperationState
    /// Whether the working copy has an unresolved conflict changed.
    | ConflictChanged of Conflicted: bool

/// A batch of changes observed in one settled re-query: the **new full `RepoSnapshot`**
/// (ready to render a prompt/status line) plus the typed `RepoEvent`s that produced it. A
/// `RepoWatcher` only yields a `RepoChange` when at least one event fired.
type RepoChange =
    {
        /// The repository state after the change.
        Snapshot: RepoSnapshot
        /// The typed deltas from the previous state (never empty).
        Events: RepoEvent list
    }

/// The observable state the watcher diffs across re-queries: the snapshot's fields plus
/// the full local-branch set. Internal — constructed from a `RepoSnapshot` by the loop.
type internal WatchState =
    { Head: string option
      Branch: string option
      Upstream: string option
      Ahead: uint64 option
      Behind: uint64 option
      Dirty: bool
      ChangeCount: uint64
      Conflicted: bool
      Operation: OperationState
      Branches: string list }

module internal WatchState =

    /// Mirror a `RepoSnapshot` plus the branch list, flattening the bundled tracking back
    /// into per-field deltas so `UpstreamChanged`/`AheadBehindChanged` stay distinct.
    let fromSnapshot (snapshot: RepoSnapshot) (branches: string list) : WatchState =
        { Head = snapshot.Head
          Branch = snapshot.Branch
          Upstream = snapshot.Tracking |> Option.map (fun t -> t.Branch)
          Ahead = snapshot.Tracking |> Option.map (fun t -> t.Ahead)
          Behind = snapshot.Tracking |> Option.map (fun t -> t.Behind)
          Dirty = snapshot.Dirty
          ChangeCount = snapshot.ChangeCount
          Conflicted = snapshot.Conflicted
          Operation = snapshot.Operation
          Branches = branches }

/// Diff two consecutive states into the events that changed. Pure; the order is stable
/// (head, branch switch, created, deleted, working copy, upstream, ahead/behind, operation,
/// conflict — created/deleted names sorted).
module internal Diff =

    let diff (prev: WatchState) (next: WatchState) : RepoEvent list =
        [ if prev.Head <> next.Head then
              RepoEvent.HeadMoved(From = prev.Head, To = next.Head)

          if prev.Branch <> next.Branch then
              RepoEvent.BranchSwitched(From = prev.Branch, To = next.Branch)

          // Branch-set delta (F# `Set` iterates sorted, so output is deterministic
          // regardless of the order git/jj listed them in).
          let before = Set.ofList prev.Branches
          let after = Set.ofList next.Branches

          for name in Set.difference after before do
              RepoEvent.BranchCreated(CreatedName = name)

          for name in Set.difference before after do
              RepoEvent.BranchDeleted(DeletedName = name)

          if prev.Dirty <> next.Dirty || prev.ChangeCount <> next.ChangeCount then
              RepoEvent.WorkingCopyChanged(Dirty = next.Dirty, ChangeCount = next.ChangeCount)

          if prev.Upstream <> next.Upstream then
              RepoEvent.UpstreamChanged(Upstream = next.Upstream)

          if prev.Ahead <> next.Ahead || prev.Behind <> next.Behind then
              RepoEvent.AheadBehindChanged(Ahead = next.Ahead, Behind = next.Behind)

          // Only the git merge/rebase lifecycle: a transition to/from `Conflict` (jj's
          // conflict marker, which tracks the same bit as `conflicted`) is left to
          // `ConflictChanged` so a jj conflict isn't double-signalled.
          if
              prev.Operation <> next.Operation
              && prev.Operation <> OperationState.Conflict
              && next.Operation <> OperationState.Conflict
          then
              RepoEvent.OperationChanged(From = prev.Operation, To = next.Operation)

          if prev.Conflicted <> next.Conflicted then
              RepoEvent.ConflictChanged(Conflicted = next.Conflicted) ]
