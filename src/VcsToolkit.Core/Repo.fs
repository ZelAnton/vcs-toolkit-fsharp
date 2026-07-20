namespace VcsToolkit.Core

open System.IO
open VcsToolkit.Git
open VcsToolkit.Jj

/// The per-tool client behind a `Repo`. Git/Jj are reference types, so a sibling handle
/// from `Repo.At` shares the same client instance without rebuilding it.
[<RequireQualifiedAccess>]
type internal Backend =
    | Git of Git
    | Jj of Jj

/// A cwd-bound, backend-agnostic VCS handle: write code against "the repository" without
/// caring whether it's git or jj. `Repo.Open` auto-detects the backend; every common
/// method dispatches to the underlying `VcsToolkit.Git` / `VcsToolkit.Jj` client and hands
/// back plain result types (`RepoSnapshot`, `FileChange`, `MergeProbe`, …) that don't
/// mention the backend.
///
/// It is a thin common layer: the operations the two tools model too differently (a full
/// merge, jj's op-restore, range/revset queries) stay on the raw client — reach them via
/// the `Git` / `Jj` accessors (each `Some` only for its backend), or the dir-bound `GitAt` /
/// `JjAt` views (bound to this handle's `Cwd`). The synchronous `cleanupWorktreeBlocking`
/// Drop-guard helper is not ported — .NET's `IAsyncDisposable` can `await RemoveWorktree`.
[<Sealed>]
type Repo private (root: string, cwd: string, backend: Backend) =

    /// Make a caller-supplied path stable before storing it on a cwd-bound handle. Keeping
    /// this in one place means every constructor applies the same lexical interpretation.
    static member private NormalizePath(parameterName: string, path: string) : Result<string, RepoError> =
        try
            Ok(Path.GetFullPath path)
        with ex ->
            // Invalid paths must be reported as caller input, never leak a platform-specific
            // Path exception from the public facade.
            Error(RepoError.InvalidInput $"{parameterName} must be a valid path: {ex.Message}")

    /// Static factories retain their non-Result public shape, so turn the shared diagnostic
    /// into ArgumentException rather than introduce a breaking Result return type. `Open`
    /// already returns Result and therefore exposes the same failure as InvalidInput.
    static member private NormalizePathOrThrow(parameterName: string, path: string) =
        match Repo.NormalizePath(parameterName, path) with
        | Ok absPath -> absPath
        | Error(RepoError.InvalidInput message) -> invalidArg parameterName message
        | Error _ -> failwith "unreachable: path normalization only returns InvalidInput"

    /// Detect the repository at or above `dir` and open a handle bound to `dir`, using
    /// the real job-backed runner. `NotARepository` when no `.git`/`.jj` is found.
    static member Open(dir: string) : Result<Repo, RepoError> =
        // The plain-default case of `OpenWith`: build the standard client for whichever
        // backend is detected. Both factories are lazy, so only the detected one is built.
        Repo.OpenWith(dir, (fun () -> Git.Create()), (fun () -> Jj.Create()))

    /// Like `Open`, but the client for the detected backend is built by an injected factory
    /// instead of the plain `Git.Create()`/`Jj.Create()` default — for a caller that needs a
    /// pre-configured client (e.g. a hardened, timeout-bound one) without re-implementing
    /// detection, path absolutisation, and error mapping. Only the factory for the backend
    /// `detect` actually finds is invoked, so hardening/configuring both clients costs nothing
    /// for the backend this repository does not use. Same contract as `Open`: `dir` is
    /// absolutised first (via the shared `NormalizePath`, so `detect` can walk parents),
    /// `InvalidInput` on a bad path, `NotARepository` when no `.git`/`.jj` is found at or above it.
    static member OpenWith(dir: string, git: unit -> Git, jj: unit -> Jj) : Result<Repo, RepoError> =
        // Absolutise first: `detect` walks parents, and a relative path like "." has no
        // real ancestor chain, so a relative input would never find a repo above the cwd.
        let absResult = Repo.NormalizePath("dir", dir)

        match absResult with
        | Error e -> Error e
        | Ok absDir ->
            match Detect.detect absDir with
            | None -> Error(RepoError.NotARepository absDir)
            | Some located ->
                let backend =
                    match located.Kind with
                    | BackendKind.Git -> Backend.Git(git ())
                    | BackendKind.Jj -> Backend.Jj(jj ())

                Ok(Repo(located.Root, absDir, backend))

    /// Clone `url` into `dest` with the backend/mode `spec.Kind` selects, then open the
    /// freshly-cloned repository — the backend-agnostic answer to `Git.CloneRepo`/
    /// `Jj.GitClone`: a single "URL → open `Repo` handle" entry point for provisioning
    /// flows (CI agents, orchestrators, integration tests) that shouldn't have to branch
    /// on backend themselves. Uses the real job-backed runner for whichever client
    /// `spec.Kind` needs; see `CloneWith` to inject a pre-configured client instead.
    static member Clone(url: string, dest: string, spec: CloneOptions) =
        Repo.CloneWith(url, dest, spec, (fun () -> Git.Create()), (fun () -> Jj.Create()))

    /// Like `Clone`, but the client that drives the clone — and, on success, the handle's
    /// client — is built by an injected factory instead of the plain `Git.Create()`/
    /// `Jj.Create()` default, mirroring `OpenWith`'s rationale. Only the factory for
    /// `spec.Kind`'s backend is ever invoked (the other stays unbuilt).
    ///
    /// Delegates the clone itself to the `GitBackend.cloneRepo`/`JjBackend.gitClone` adapters
    /// (`Git.CloneRepo`/`Jj.GitClone` under the hood — their own argv guards on `url`/`dest`,
    /// both bare positionals, apply unchanged, and error normalization stays on the same
    /// adapter boundary every other facade operation uses, rather than mapping `ofVcs` here)
    /// — then reuses `OpenWith` to build the handle, so `dest` is absolutised via the shared
    /// `NormalizePath` and both a bad `dest` path and a post-clone detection failure are
    /// reported exactly like `Open`/`OpenWith`.
    static member CloneWith(url: string, dest: string, spec: CloneOptions, git: unit -> Git, jj: unit -> Jj) =
        task {
            match Repo.NormalizePath("dest", dest) with
            | Error e -> return Error e
            | Ok absDest ->
                let! cloned =
                    match spec.Kind with
                    | CloneKind.Git -> GitBackend.cloneRepo (git ()) url absDest (VcsToolkit.Git.CloneSpec.Create())
                    | CloneKind.JjColocated -> JjBackend.gitClone (jj ()) url absDest true
                    | CloneKind.JjNonColocated -> JjBackend.gitClone (jj ()) url absDest false

                match cloned with
                | Error e -> return Error e
                | Ok() -> return Repo.OpenWith(absDest, git, jj)
        }

    /// Build a git-backed handle from an explicit client — for a custom runner (e.g. a
    /// test seam) or a pre-configured `Git`. Both paths are absolutised at construction so
    /// later operations remain bound to this handle rather than the process's current directory.
    static member FromGit(root: string, cwd: string, client: Git) =
        let absRoot = Repo.NormalizePathOrThrow("root", root)
        let absCwd = Repo.NormalizePathOrThrow("cwd", cwd)
        Repo(absRoot, absCwd, Backend.Git client)

    /// Build a jj-backed handle from an explicit client. Both paths are absolutised at
    /// construction for the same cwd-stability contract as `FromGit`.
    static member FromJj(root: string, cwd: string, client: Jj) =
        let absRoot = Repo.NormalizePathOrThrow("root", root)
        let absCwd = Repo.NormalizePathOrThrow("cwd", cwd)
        Repo(absRoot, absCwd, Backend.Jj client)

    // --- Identity / re-anchoring / escape hatches ----------------------------

    /// Which backend drives this handle.
    member _.Kind =
        match backend with
        | Backend.Git _ -> BackendKind.Git
        | Backend.Jj _ -> BackendKind.Jj

    /// The repository root detected at open time.
    member _.Root = root

    /// The directory operations run against.
    member _.Cwd = cwd

    /// A sibling handle bound to `dir`, sharing this handle's client and root. `dir` is
    /// absolutised now so later worktree operations do not inherit the process cwd.
    member _.At(dir: string) =
        let absDir = Repo.NormalizePathOrThrow("dir", dir)
        Repo(root, absDir, backend)

    /// The underlying `Git` client, or `None` when jj-backed — an escape hatch to
    /// git-only operations not on the common surface (pass this handle's `Cwd` as `dir`).
    member _.Git =
        match backend with
        | Backend.Git g -> Some g
        | Backend.Jj _ -> None

    /// The underlying `Jj` client, or `None` when git-backed.
    member _.Jj =
        match backend with
        | Backend.Jj j -> Some j
        | Backend.Git _ -> None

    /// The underlying `Git` client bound to this handle's `Cwd` (a `GitAt` view whose modelled
    /// methods drop `dir`), or `None` when jj-backed. The view is bound to THIS handle's `Cwd`,
    /// so to work in another worktree re-anchor first: `repo.At(path).GitAt`.
    member _.GitAt =
        match backend with
        | Backend.Git g -> Some(g.At cwd)
        | Backend.Jj _ -> None

    /// The underlying `Jj` client bound to this handle's `Cwd` (a `JjAt` view whose modelled
    /// methods drop `dir`), or `None` when git-backed. See `GitAt` for the re-anchor caveat.
    member _.JjAt =
        match backend with
        | Backend.Jj j -> Some(j.At cwd)
        | Backend.Git _ -> None

    // --- Refs ----------------------------------------------------------------

    /// The current branch (git) or bookmark (jj). On jj this is the nearest bookmark
    /// reachable from the working copy, so it stays set across a `jj describe`/`new`/
    /// `commit`; when several are equally near `@` the lexicographically-smallest name is
    /// returned. `None` only when detached / no bookmark on or above `@`.
    member _.CurrentBranch() =
        match backend with
        | Backend.Git g -> GitBackend.currentBranch g cwd
        | Backend.Jj j -> JjBackend.currentBranch j cwd

    /// The trunk branch/bookmark. Resolution order: the backend's own notion (git's
    /// `origin/HEAD`, jj's `trunk()` revset), then a fallback to a local `main`, then
    /// `master`; `None` when none of those resolve.
    member this.Trunk() =
        task {
            let! native =
                match backend with
                | Backend.Git g -> GitBackend.trunk g cwd
                | Backend.Jj j -> JjBackend.trunk j cwd

            match native with
            | Error e -> return Error e
            | Ok(Some t) -> return Ok(Some t)
            | Ok None ->
                let! mainExists = this.BranchExists "main"

                match mainExists with
                | Error e -> return Error e
                | Ok true -> return Ok(Some "main")
                | Ok false ->
                    let! masterExists = this.BranchExists "master"

                    match masterExists with
                    | Error e -> return Error e
                    | Ok true -> return Ok(Some "master")
                    | Ok false -> return Ok None
        }

    /// Local branch (git) / bookmark (jj) names.
    member _.LocalBranches() =
        match backend with
        | Backend.Git g -> GitBackend.localBranches g cwd
        | Backend.Jj j -> JjBackend.localBranches j cwd

    /// Whether a local branch/bookmark named `name` exists.
    member _.BranchExists(name: string) =
        match backend with
        | Backend.Git g -> GitBackend.branchExists g cwd name
        | Backend.Jj j -> JjBackend.branchExists j cwd name

    /// Delete a local branch (git) / bookmark (jj). `force` applies to git only
    /// (`branch -D` vs `-d`); jj has no force and ignores it.
    member _.DeleteBranch(name: string, force: bool) =
        match backend with
        | Backend.Git g -> GitBackend.deleteBranch g cwd name force
        | Backend.Jj j -> JjBackend.deleteBranch j cwd name

    /// Rename a local branch (git) / bookmark (jj).
    member _.RenameBranch(oldName: string, newName: string) =
        match backend with
        | Backend.Git g -> GitBackend.renameBranch g cwd oldName newName
        | Backend.Jj j -> JjBackend.renameBranch j cwd oldName newName

    // --- Status --------------------------------------------------------------

    /// Whether the working copy has uncommitted changes (git: a non-empty `status`; jj: a
    /// non-empty working-copy change `@`).
    member _.HasUncommittedChanges() =
        match backend with
        | Backend.Git g -> GitBackend.hasUncommittedChanges g cwd
        | Backend.Jj j -> JjBackend.hasUncommittedChanges j cwd

    /// Whether the working copy has uncommitted changes to *tracked* files. Backend
    /// nuance: git ignores untracked files here; jj auto-tracks new files, so this equals
    /// `HasUncommittedChanges`.
    member _.HasTrackedChanges() =
        match backend with
        | Backend.Git g -> GitBackend.hasTrackedChanges g cwd
        | Backend.Jj j -> JjBackend.hasUncommittedChanges j cwd

    /// Paths with unresolved merge conflicts in the working copy, repo-relative with `/`
    /// separators. Empty when there are none.
    member _.ConflictedFiles() =
        match backend with
        | Backend.Git g -> GitBackend.conflictedFiles g cwd
        | Backend.Jj j -> JjBackend.conflictedFiles j cwd

    /// The working-copy changes (git `status` / jj `diff -r @ --summary`).
    member _.ChangedFiles() =
        match backend with
        | Backend.Git g -> GitBackend.changedFiles g cwd
        | Backend.Jj j -> JjBackend.changedFiles j cwd

    /// Aggregate insertion/deletion counts for the working copy. Backend nuance: git
    /// counts the working tree against `HEAD` (excludes untracked files; against the empty
    /// tree on an unborn repo), while jj counts the `@` change against its parent
    /// (includes newly-added files).
    member _.DiffStat() =
        match backend with
        | Backend.Git g -> GitBackend.diffStat g cwd
        | Backend.Jj j -> JjBackend.diffStat j cwd

    /// A batched `RepoSnapshot` of the common repo state in a small fixed number of
    /// spawns instead of a call per field. `Tracking` is always `None` on jj.
    member _.Snapshot() =
        match backend with
        | Backend.Git g -> GitBackend.snapshot g cwd
        | Backend.Jj j -> JjBackend.snapshot j cwd

    /// Recent history: up to `max` commits reachable from `revspecOrRevset` (a git revspec, e.g.
    /// `"HEAD"` or `"main..HEAD"`; a jj revset, e.g. `"@"`), most-recent-first (git `log`'s default
    /// order / jj `log`'s order). Backend nuance: `Commit.Author`/`Commit.Date` are `Some` only on
    /// git — jj's typed log surfaces no authorship or timestamp, so they are `None` there.
    member _.Log(revspecOrRevset: string, max: int) =
        match backend with
        | Backend.Git g -> GitBackend.log g cwd revspecOrRevset max
        | Backend.Jj j -> JjBackend.log j cwd revspecOrRevset max

    /// Like `Log`, but scoped to commits that touched `paths` — e.g. "who changed this module".
    /// `paths` are **repo-root-relative** and resolved against the repository `Root` even when this
    /// handle is bound to a subdirectory (`Cwd` ≠ `Root`), matching the root-relative paths
    /// `ChangedFiles` reports — so a path taken from `ChangedFiles` scopes the exact same file.
    /// `paths` must be non-empty: an empty set is refused up front, because a path-less scope would
    /// degrade to `Log`'s **unrestricted** history on both backends (git's `-- ` with no pathspec,
    /// jj's bare `-r <revset>`), the opposite of "scoped to these paths". On git the paths become
    /// `--literal-pathspecs` pathspecs (glob metacharacters matched literally, argv-budget chunking
    /// transparent) run from `Root` so they anchor there rather than at `Cwd`; on jj they become
    /// exact-path `root-file:"…"` filesets (workspace-root-relative by construction). Same `Commit`
    /// DTO and author/date backend nuance as `Log`.
    member _.LogPaths(revspecOrRevset: string, max: int, paths: string list) =
        task {
            if List.isEmpty paths then
                return
                    Error(
                        RepoError.InvalidInput
                            "logPaths requires at least one path: an empty set would log unrestricted history, not history scoped to the named paths"
                    )
            else
                match backend with
                // Anchor the git pathspecs at the repo root: git resolves a `-- <pathspec>`
                // relative to the command's cwd, so run from `root` (not this handle's `cwd`,
                // which may be a subdirectory) to honour the repo-relative contract. jj's filesets
                // are `root-file:` (self-anchoring to the workspace root), so `cwd` is passed
                // as-is — it correctly scopes which workspace the query runs against.
                | Backend.Git g -> return! GitBackend.logPaths g root revspecOrRevset max paths
                | Backend.Jj j -> return! JjBackend.logPaths j cwd revspecOrRevset max paths
        }

    // --- Mutations -----------------------------------------------------------

    /// Commit exactly `paths` with `message` (git `commit --only`, jj `commit <filesets>`).
    /// `paths` are **repo-root-relative** and resolved against the repository `Root` even when this
    /// handle is bound to a subdirectory (`Cwd` ≠ `Root`), matching the root-relative paths
    /// `ChangedFiles` reports — so committing a path taken from `ChangedFiles` commits that exact
    /// file. `paths` must be non-empty: an empty set is refused up front, because the backends
    /// diverge dangerously — git errors, while jj's `commit` with no filesets would silently commit
    /// the **entire** working copy.
    member _.CommitPaths(paths: string list, message: string) =
        task {
            if List.isEmpty paths then
                return
                    Error(
                        RepoError.InvalidInput
                            "commitPaths requires at least one path: an empty set would error on git but commit the entire working copy on jj"
                    )
            else
                match backend with
                // Anchor the git pathspecs at the repo root: git resolves a `-- <pathspec>` relative
                // to the command's cwd, so run from `root` (not this handle's `cwd`, which may be a
                // subdirectory) to honour the repo-relative contract. jj's filesets are `root-file:`
                // (self-anchoring to the workspace root), so `cwd` is passed as-is — it correctly
                // scopes which workspace the commit runs against.
                | Backend.Git g -> return! GitBackend.commitPaths g root paths message
                | Backend.Jj j -> return! JjBackend.commitPaths j cwd paths message
        }

    /// Fetch from the default remote (git `fetch` / jj `git fetch`).
    member _.Fetch() =
        match backend with
        | Backend.Git g -> GitBackend.fetch g cwd
        | Backend.Jj j -> JjBackend.fetch j cwd

    /// Fetch from a *named* remote. Transient network failures are retried by the client.
    member _.FetchFrom(remote: string) =
        match backend with
        | Backend.Git g -> GitBackend.fetchFrom g cwd remote
        | Backend.Jj j -> JjBackend.fetchFrom j cwd remote

    /// Fetch a single branch/bookmark from `origin` into its remote-tracking ref.
    member _.FetchBranch(branch: string) =
        match backend with
        | Backend.Git g -> GitBackend.fetchBranch g cwd branch
        | Backend.Jj j -> JjBackend.fetchBranch j cwd branch

    /// Push `branch` to `origin` (git `push -u origin <branch>` / jj `git push -b
    /// <branch>`). The branch (jj: bookmark) must already exist locally. For renamed
    /// refspecs or non-`origin` remotes, use the `Git` escape hatch.
    member _.Push(branch: string) =
        match backend with
        | Backend.Git g -> GitBackend.push g cwd branch
        | Backend.Jj j -> JjBackend.push j cwd branch

    /// Switch the working copy to `reference` (git `checkout` / jj `edit`).
    ///
    /// ⚠ **Backend divergence — this is NOT "detach and build on top" on jj.** On **git**, a
    /// subsequent commit *appends* on top of `reference` (its tip is untouched). On **jj**,
    /// `Checkout` maps to `jj edit`, which makes `reference`'s commit *itself* the working-copy
    /// change — so a following `CommitPaths` (or any edit) **rewrites that commit in place** (new
    /// change-id, replaced description), silently amending a possibly-already-pushed commit rather
    /// than adding a new one. Backend-agnostic "start fresh work on top of `main`" code must not
    /// rely on `Checkout` alone — use `NewChild` instead, which maps to `jj new <reference>` on
    /// jj and is equivalent to this method on git.
    member _.Checkout(reference: string) =
        match backend with
        | Backend.Git g -> GitBackend.checkout g cwd reference
        | Backend.Jj j -> JjBackend.checkout j cwd reference

    /// Start new work on top of `reference` **without modifying it** (git `checkout
    /// <reference> --`; jj `new <reference>`) — the backend-agnostic answer to the
    /// append-on-top caveat called out on `Checkout`. On **git**, this is exactly
    /// `Checkout`: switching the working copy to `reference` and letting the next commit
    /// append naturally is already non-destructive on git. On **jj**, this is *not*
    /// `Checkout` — it runs `jj new <reference>`, which creates a fresh, undescribed
    /// **child** change stacked on top of `reference` and leaves `reference`'s own commit
    /// untouched (unlike `jj edit`, which makes `reference` itself the working-copy
    /// change and rewrites it in place). Use this whenever "start fresh work on top of
    /// `main`" must behave the same way on both backends.
    member _.NewChild(reference: string) =
        match backend with
        | Backend.Git g -> GitBackend.newChild g cwd reference
        | Backend.Jj j -> JjBackend.newChild j cwd reference

    /// Rebase the current work onto `onto` (git `rebase` / jj `rebase -d`).
    member _.Rebase(onto: string) =
        match backend with
        | Backend.Git g -> GitBackend.rebase g cwd onto
        | Backend.Jj j -> JjBackend.rebase j cwd onto

    // --- Merge & operation state ---------------------------------------------

    /// Probe whether merging `source` into the current work would conflict, **without
    /// leaving any trace**: the probe is rolled back before returning (git: `merge
    /// --no-commit --no-ff` then `merge --abort`; jj: a merge change probed and undone via
    /// `op restore`). A failing rollback propagates as an error rather than a result that
    /// misdescribes the on-disk state.
    ///
    /// **Cancellation-safe cleanup on both backends.** The rollback never inherits the
    /// (possibly already-fired) cancellation token of the operation whose failure/cancellation
    /// triggered it: on git, the merge-in-progress probe and `merge --abort` run detached, on
    /// their own fresh cancellation budget (`Git.IsMergeInProgressDetached`/
    /// `Git.MergeAbortDetached`); on jj, `Jj.RollbackTo` runs its `op log`/`op restore` cleanup
    /// the same way. So a cancelled or timed-out probe merge still gets cleaned up on either
    /// backend, instead of leaving a staged probe merge (git) or an un-restored op log (jj)
    /// behind.
    member _.TryMerge(source: string) =
        match backend with
        | Backend.Git g -> GitBackend.tryMerge g cwd source
        | Backend.Jj j -> JjBackend.tryMerge j cwd source

    /// Abort the in-progress operation, if any (git: `merge`/`rebase --abort`; jj: a
    /// no-op). Returns the fresh *post-call* `OperationState`.
    member _.AbortInProgress() =
        match backend with
        | Backend.Git g -> GitBackend.abortInProgress g cwd
        | Backend.Jj j -> JjBackend.abortInProgress j cwd

    /// Continue the in-progress operation after conflict resolution (git: `commit
    /// --no-edit` for a merge / `rebase --continue`; jj: a no-op). Returns the fresh
    /// *post-call* `OperationState`: `Conflict` when unresolved paths still block (also on
    /// git, unlike `InProgressState`), `Clear` when finished.
    member _.ContinueInProgress() =
        match backend with
        | Backend.Git g -> GitBackend.continueInProgress g cwd
        | Backend.Jj j -> JjBackend.continueInProgress j cwd

    /// Whether the working copy is mid-operation or conflicted. Note the asymmetry: *this
    /// method* reports `Merge`/`Rebase` (never `Conflict`) on git — a git conflict *is*
    /// that paused state — while jj has no paused op and reports `Conflict` directly.
    member _.InProgressState() =
        match backend with
        | Backend.Git g -> GitBackend.inProgressState g cwd
        | Backend.Jj j -> JjBackend.inProgressState j cwd

    // --- Worktrees / workspaces ----------------------------------------------

    /// List attached worktrees (git) / workspaces (jj).
    member _.ListWorktrees() =
        match backend with
        | Backend.Git g -> GitBackend.listWorktrees g cwd
        | Backend.Jj j -> JjBackend.listWorktrees j cwd

    /// Create a worktree/workspace at `path` on a **new** `branch` based on `baseRef`.
    /// Always `CreateOutcome.Plain`. `branch` must not already exist; the jj path is two
    /// non-atomic steps but a failed bookmark step rolls back the half-made worktree.
    member _.CreateWorktree(path: string, branch: string, baseRef: string) =
        match backend with
        | Backend.Git g -> GitBackend.createWorktree g cwd path branch baseRef
        | Backend.Jj j -> JjBackend.createWorktree j cwd path branch baseRef

    /// Remove the worktree/workspace at `path`. For jj this resolves the workspace name by
    /// matching `path`, deletes the directory, then forgets it; a `path` that matches no
    /// attached jj workspace returns `WorktreeNotFound`.
    member _.RemoveWorktree(path: string, force: bool) =
        match backend with
        | Backend.Git g -> GitBackend.removeWorktree g cwd path force
        | Backend.Jj j -> JjBackend.removeWorktree j cwd path force

    // --- File content ----------------------------------------------------------

    /// The content of `path` as it exists at `rev`, untrimmed and UTF-8-decoded. Byte-exact for
    /// UTF-8/text content (trailing newlines survive a read-modify-write); a non-UTF-8 byte (a
    /// binary or legacy-encoded blob) is replaced with U+FFFD and does NOT round-trip — use
    /// `ShowFileBytes` for a verbatim read of such content. `rev` is passed through as-is to the
    /// underlying client — git accepts a commit-ish, jj a revset; the two syntaxes are NOT
    /// interchangeable, so this is not a cross-backend-portable revision string.
    member _.ShowFile(rev: string, path: string) =
        match backend with
        | Backend.Git g -> GitBackend.showFile g cwd rev path
        | Backend.Jj j -> JjBackend.showFile j cwd rev path

    /// The content of `path` at `rev` as raw, verbatim **bytes** — arbitrary (binary,
    /// legacy-encoded, non-UTF-8) content round-trips byte-for-byte, unlike `ShowFile`, which
    /// UTF-8-decodes and replaces any non-UTF-8 byte with U+FFFD. The byte-exact form for a
    /// read-modify-write of blob content that may not be UTF-8 text. `rev` is passed through as
    /// for `ShowFile` (git commit-ish / jj revset, not cross-backend-portable).
    member _.ShowFileBytes(rev: string, path: string) =
        match backend with
        | Backend.Git g -> GitBackend.showFileBytes g cwd rev path
        | Backend.Jj j -> JjBackend.showFileBytes j cwd rev path

    /// Per-line authorship of `path` — "who last touched this line, and when" (git `blame
    /// --line-porcelain`; jj `file annotate`). `rev` is passed through as-is to the underlying
    /// client (git commit-ish / jj revset, not cross-backend-portable) — `None` annotates the
    /// working copy on git and `@` on jj.
    ///
    /// `path` is anchored at `Root` on both backends, like `LogPaths`/`CommitPaths`: git's
    /// `blame -- <path>` resolves like most git pathspecs — relative to the invocation directory,
    /// not root-relative like `ShowFile`'s `<rev>:<path>` syntax — so this runs from `Root`. jj's
    /// `file annotate <path>` takes a **plain path**, not a `root-file:` fileset the way `FileShow`
    /// does, so there is no self-anchoring prefix to reach for here — instead the underlying `jj`
    /// invocation itself runs from `Root` (not `Cwd`), which resolves the plain path the same way
    /// jj resolves any other cwd-relative path argument, just anchored at the repo root.
    member _.Annotate(path: string, rev: string option) =
        match backend with
        | Backend.Git g -> GitBackend.annotate g root path rev
        | Backend.Jj j -> JjBackend.annotate j root path rev
