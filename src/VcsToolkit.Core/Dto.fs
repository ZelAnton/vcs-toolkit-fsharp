namespace VcsToolkit.Core

open VcsToolkit.Diff

// Backend-agnostic data types the facade returns, generalising the per-tool shapes of
// VcsToolkit.Git and VcsToolkit.Jj into one set a consumer can use without knowing
// which backend is in play. `FileChange.Kind` and `DiffStat` are the shared
// `VcsToolkit.Diff` types (one type across the wrappers and the facade, no remapping).

/// Which version-control tool backs a `Repo`.
///
/// Treat this as potentially extensible (the Rust model is `#[non_exhaustive]`) — add a `| _ ->`
/// arm when pattern-matching so a future backend doesn't break your code.
[<RequireQualifiedAccess>]
type BackendKind =
    /// A plain Git repository.
    | Git
    /// A Jujutsu repository (possibly colocated with Git).
    | Jj

    /// The tool's short name (`"git"` / `"jj"`).
    member this.AsString =
        match this with
        | BackendKind.Git -> "git"
        | BackendKind.Jj -> "jj"

/// Which backend (and, for jj, git-colocation) `Repo.Clone`/`Repo.CloneWith` should drive.
/// Deliberately its own type rather than reusing `BackendKind`: jj's clone has two distinct
/// modes (`--colocate`/`--no-colocate`) that `BackendKind.Jj` alone doesn't distinguish.
///
/// Treat this as potentially extensible (the Rust model is `#[non_exhaustive]`) — add a `| _ ->`
/// arm when pattern-matching so a future clone mode doesn't break your code.
[<RequireQualifiedAccess>]
type CloneKind =
    /// A plain git clone (`git clone`).
    | Git
    /// A jj clone of a git remote, colocated with a `.git` working copy (`jj git clone --colocate`).
    | JjColocated
    /// A jj clone of a git remote, NOT colocated with a `.git` working copy
    /// (`jj git clone --no-colocate`).
    | JjNonColocated

/// Backend-agnostic options for `Repo.Clone`/`Repo.CloneWith`: at minimum, which
/// backend/mode to clone with. Neither backend's clone command currently exposes an
/// option the other one also supports (e.g. jj's `git clone` has no equivalent to git's
/// `--branch`/`--depth`), so there is nothing to add here yet beyond `Kind` — extend this
/// record, not `Repo.Clone`'s signature, if/when that changes.
type CloneOptions =
    {
        /// Which backend (and, for jj, colocation) to clone with.
        Kind: CloneKind
    }

    /// A plain full clone with the given `kind`.
    static member Create(kind: CloneKind) = { Kind = kind }

/// One changed path in the working copy, unified across `git status` / `jj diff --summary`.
type FileChange =
    {
        /// The path (the *new* path for a rename).
        Path: string
        /// The original path for a rename, populated by **both** backends (git's
        /// `R old -> new`; jj's `{old => new}` diff-summary form); `None` for non-renames.
        OldPath: string option
        /// How the file changed (the shared `VcsToolkit.Diff.ChangeKind`).
        Kind: ChangeKind
    }

/// One commit/change from the repository history — the honest least common denominator between
/// git's typed `git log` (hash/short-hash/author/date/subject) and jj's typed `jj log`
/// (change-id/commit-id/empty/description). See `Repo.Log`/`Repo.LogPaths`.
///
/// `Author`/`Date` are `Some` only on git: jj's typed log doesn't currently surface authorship or a
/// timestamp (its template renders only the id/empty/description columns), so this DTO leaves them
/// `None` on jj rather than fabricating a value.
type Commit =
    {
        /// The commit's identifying hash: git's full object id (`%H`) / jj's (already-short) commit id.
        Id: string
        /// Commit message: git's subject line (`%s`) / jj's first description line.
        Description: string
        /// Author name (git `%an`); `None` on jj (see the type docs).
        Author: string option
        /// Author date, strict ISO-8601 on git (`%aI`); `None` on jj (see the type docs).
        Date: string option
    }

/// One attached worktree (git) / workspace (jj).
type WorktreeInfo =
    {
        /// Filesystem path of the worktree's working copy.
        Path: string
        /// The branch (git) or first bookmark (jj) on it; `None` when detached/none.
        Branch: string option
        /// The checked-out commit's **full** object id — the same identity
        /// `RepoSnapshot.Head` carries (git `HEAD` oid / jj workspace commit id), so the
        /// two compare directly without a short-prefix collision; `None` when unavailable
        /// (e.g. a bare git entry). Truncate for display.
        Commit: string option
        /// A bare git worktree entry (always `false` for jj).
        IsBare: bool
    }

/// Whether the working copy is mid-operation, unified across the backends' different
/// models: git exposes an in-progress merge or rebase as on-disk state (`MERGE_HEAD` /
/// a `rebase-*` dir), while jj has no multi-step operations — it records a conflict
/// directly on the working-copy change.
///
/// Treat this as potentially extensible (the Rust model is `#[non_exhaustive]`) — add a `| _ ->`
/// arm when pattern-matching so a future operation kind doesn't break your code.
[<RequireQualifiedAccess>]
type OperationState =
    /// No operation in progress and no conflict.
    | Clear
    /// A git merge is in progress (`MERGE_HEAD` present).
    | Merge
    /// A git rebase is in progress (a `rebase-merge`/`rebase-apply` dir present).
    | Rebase
    /// A git `am` (mailbox patch apply) is in progress. Distinct from `Rebase` because it
    /// aborts with `am --abort`, not `rebase --abort` (M20).
    | ApplyMailbox
    /// A git cherry-pick is in progress (`CHERRY_PICK_HEAD` present). Distinct from `Merge`:
    /// it aborts/continues with `cherry-pick --abort` / `cherry-pick --continue` (a
    /// cherry-pick conflict writes `CHERRY_PICK_HEAD`, **not** `MERGE_HEAD`). git only.
    | CherryPick
    /// A git revert is in progress (`REVERT_HEAD` present). Aborts/continues with
    /// `revert --abort` / `revert --continue`. git only.
    | Revert
    /// A git bisect session is in progress (`BISECT_LOG` present). Aborts with `bisect reset`
    /// — there is no continue step, so `Repo.ContinueInProgress` reports it unsupported rather
    /// than silently doing nothing. git only.
    | Bisect
    /// The working copy has an unresolved conflict (chiefly jj, which records conflicts
    /// on the change rather than pausing an operation).
    | Conflict

/// Upstream tracking for the current branch: the upstream ref and how far the branch is
/// ahead/behind it. Only meaningful as a whole — git reports the three together or not
/// at all — so `RepoSnapshot` carries it as one `UpstreamTracking option`.
type UpstreamTracking =
    {
        /// The upstream tracking branch, e.g. `"origin/main"`.
        Branch: string
        /// Commits the local branch is ahead of the upstream; `None` when the upstream is set
        /// but git couldn't count against it (a gone / not-yet-fetched remote) — distinct from
        /// `Some 0UL`, which is genuinely in sync.
        Ahead: uint64 option
        /// Commits the local branch is behind the upstream; `None` when uncountable (see `Ahead`).
        Behind: uint64 option
    }

/// A one-shot snapshot of the common repository state — branch, upstream tracking,
/// ahead/behind, dirtiness, and operation state — gathered in a **small fixed** number
/// of process spawns instead of a call per field. The data a prompt, status line, or
/// TUI refresh needs. See `Repo.Snapshot`.
type RepoSnapshot =
    {
        /// The working-copy commit's **full** object id (git `HEAD` oid / jj `@` commit
        /// id) on both backends; `None` on an unborn git repo. Truncate for display.
        Head: string option
        /// Current branch (git) / bookmark (jj). On jj this is the nearest bookmark
        /// reachable from `@`, so it stays set across a `jj describe`/`jj new`/`jj
        /// commit`; `None` when detached / no bookmark on or above `@`. Matches
        /// `Repo.CurrentBranch` by construction.
        Branch: string option
        /// Upstream tracking and how far the branch is ahead/behind it, as one unit —
        /// `Some` only when an upstream is configured, `None` otherwise (and **always
        /// `None` on jj**, which has no git-style upstream tracking).
        Tracking: UpstreamTracking option
        /// Whether the working copy has any uncommitted change (tracked or untracked).
        Dirty: bool
        /// Number of changed paths (tracked + untracked on git; the `@` change's files on jj).
        ChangeCount: uint64
        /// Whether the working copy has an unresolved conflict.
        Conflicted: bool
        /// In-progress operation / conflict state (see `OperationState`).
        Operation: OperationState
    }

/// The outcome of a `Repo.TryMerge` probe. The probe itself is rolled back before it
/// returns, whatever the outcome — this only *reports* what a real merge would do.
[<RequireQualifiedAccess>]
type MergeProbe =
    /// The merge would apply without conflicts. (Test with the compiler-generated
    /// `IsClean`; the `Conflicts` case has the generated `IsConflicts`.)
    | Clean
    /// The merge would conflict in these paths (repo-relative, `/` separators — the same
    /// contract as `Repo.ConflictedFiles`).
    | Conflicts of string list

/// How a worktree was materialised. The facade always reports `Plain`; the `CowCloned`
/// variant exists so a consumer that layers a copy-on-write strategy on top can reuse
/// this type.
[<RequireQualifiedAccess>]
type CreateOutcome =
    /// The tool materialised the working copy itself.
    | Plain
    /// A copy-on-write clone populated the working copy (consumer-supplied).
    | CowCloned
