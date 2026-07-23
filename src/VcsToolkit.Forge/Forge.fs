namespace VcsToolkit.Forge

open System.IO
open System.Threading.Tasks

/// The per-CLI client behind a `Forge`, paired with that handle's one-shot version-probe
/// cache (`GitHubVersionProbe`/`GitLabVersionProbe`/`GiteaVersionProbe` — see
/// `GitHubVersionProbe`'s doc comment for the caching rationale). The cache is built once,
/// alongside the client, wherever a `Backend` case is constructed (`Forge`'s constructors
/// below) — so two separately-constructed handles never share one, while a `.At` sibling,
/// which reuses this same `Backend` value, shares both the client and its cache. `Unknown`
/// carries no client and no cache — the remote URL didn't classify as a known forge, so no
/// CLI can be picked; the handle exists only to surface the all-`false` capability map.
[<RequireQualifiedAccess>]
type internal Backend =
    | GitHub of client: VcsToolkit.GitHub.GitHub * versionProbe: GitHubVersionProbe
    | GitLab of client: VcsToolkit.GitLab.GitLab * versionProbe: GitLabVersionProbe
    | Gitea of client: VcsToolkit.Gitea.Gitea * versionProbe: GiteaVersionProbe
    | Unknown

/// Build the `Backend` case for a freshly created client, wiring up its version-probe
/// cache. Kept in one place so every construction site (below) gets the caching wired
/// identically.
[<AutoOpen>]
module private BackendCtor =

    let githubBackend (c: VcsToolkit.GitHub.GitHub) : Backend =
        Backend.GitHub(c, GitHubVersionProbe(fun () -> c.Capabilities()))

    let gitlabBackend (c: VcsToolkit.GitLab.GitLab) : Backend =
        Backend.GitLab(c, GitLabVersionProbe(fun () -> c.Capabilities()))

    let giteaBackend (c: VcsToolkit.Gitea.Gitea) : Backend =
        Backend.Gitea(c, GiteaVersionProbe(fun () -> c.Capabilities()))

/// Static capability maps and the auth-intersection helper.
[<AutoOpen>]
module private ForgeCaps =

    /// The "what the CLI ships" map for GitHub (`authed`/`version`/`kind` overlaid later).
    let staticGitHubCaps: ForgeCapabilities =
        { PrCreate = true
          PrComment = true
          PrEdit = true
          PrChecks = true
          PrMerge = true
          IssueCreate = true
          IssueReopen = true
          ReleaseDelete = true
          Authed = false
          Version = None
          Kind = ForgeKind.Unknown }

    /// GitLab ships the same command set as GitHub on the lean surface.
    let staticGitLabCaps: ForgeCapabilities = staticGitHubCaps

    /// Gitea's `tea` has no checks command, issue reopen command, or release delete command, so
    /// `PrChecks`, `IssueReopen`, and `ReleaseDelete` are `false`; and tea 0.9.2 has no
    /// `pr edit` command at all (an unrecognised `pr edit` silently falls through to `pr list`
    /// — K-063), so `PrEdit` is `false` too.
    let staticGiteaCaps: ForgeCapabilities =
        { staticGitHubCaps with
            PrChecks = false
            IssueReopen = false
            ReleaseDelete = false
            PrEdit = false }

    /// Intersect a static "ships the command" map with the auth probe, then overlay the
    /// detected `version` and backend `kind`. When authed → the static map with
    /// `Authed = true`; when not → the all-`false` shape. The version and kind are reported
    /// either way — they describe the installed CLI, not the session.
    let applyAuth
        (staticCaps: ForgeCapabilities)
        (kind: ForgeKind)
        (version: VcsToolkit.Diff.Version option)
        (authed: bool)
        : ForgeCapabilities =
        let baseCaps =
            if authed then
                { staticCaps with Authed = true }
            else
                ForgeCapabilities.AllFalse

        { baseCaps with
            Version = version
            Kind = kind }

/// Version-gate for the mutating operations: run the backend's `ensureVersion` pre-check
/// and only dispatch `run` when the CLI meets the wrapper's floor (or its version can't be
/// confirmed too old — the gate fails open). The inert `Unknown` backend passes straight
/// through to `run`, which returns its own `Unsupported` without any spawn.
[<AutoOpen>]
module private VersionGate =

    let gated
        (backend: Backend)
        (op: string)
        (run: unit -> Task<Result<'T, ForgeError>>)
        : Task<Result<'T, ForgeError>> =
        task {
            let! gate =
                match backend with
                | Backend.GitHub(_, probe) -> GitHubForge.ensureVersion probe op
                | Backend.GitLab(_, probe) -> GitLabForge.ensureVersion probe op
                | Backend.Gitea(_, probe) -> GiteaForge.ensureVersion probe op
                | Backend.Unknown -> task { return Ok() }

            match gate with
            | Error e -> return Error e
            | Ok() -> return! run ()
        }

/// A cwd-bound, forge-agnostic handle: one PR/MR lifecycle across GitHub, GitLab, and
/// Gitea. Operations run against the bound directory (`Cwd`); the CLI infers the
/// repository from that directory's git remote. Unlike a repository, a forge has no
/// filesystem marker to detect — it's identified by the remote host — so a `Forge` is
/// constructed explicitly (`GitHub`/`GitLab`/`Gitea`), optionally guided by
/// `ForgeKind.OfRemoteUrl`. A few operations are `Unsupported` on Gitea (`tea` lacks the
/// command); the raw wrapper clients are one constructor away for anything else.
[<Sealed>]
type Forge private (cwd: string, backend: Backend) =

    // --- Construction --------------------------------------------------------

    /// Make a caller-supplied path stable before storing it on a cwd-bound handle.
    static member private NormalizePathOrThrow(parameterName: string, path: string) =
        try
            Path.GetFullPath path
        with ex ->
            // Invalid paths must be reported as caller input, never leak a platform-specific
            // Path exception from the public facade.
            invalidArg parameterName $"{parameterName} must be a valid path: {ex.Message}"

    /// A GitHub-backed handle bound to `cwd`, using the real job-backed runner (gh's
    /// ambient login).
    static member GitHub(cwd: string) =
        let absCwd = Forge.NormalizePathOrThrow("cwd", cwd)
        Forge(absCwd, githubBackend (VcsToolkit.GitHub.GitHub.Create()))

    /// A GitLab-backed handle bound to `cwd` (glab's ambient login).
    static member GitLab(cwd: string) =
        let absCwd = Forge.NormalizePathOrThrow("cwd", cwd)
        Forge(absCwd, gitlabBackend (VcsToolkit.GitLab.GitLab.Create()))

    /// A Gitea-backed handle bound to `cwd`. Gitea authenticates **only** through `tea`'s
    /// ambient login (`tea login add`) — there is no `GiteaWithToken`, because `tea` has
    /// no token-via-environment override the way `gh`/`glab` do.
    static member Gitea(cwd: string) =
        let absCwd = Forge.NormalizePathOrThrow("cwd", cwd)
        Forge(absCwd, giteaBackend (VcsToolkit.Gitea.Gitea.Create()))

    /// A GitHub-backed handle that authenticates with an explicit `token` (injected as
    /// `GH_TOKEN`) instead of gh's ambient login.
    static member GitHubWithToken(cwd: string, token: string) =
        let absCwd = Forge.NormalizePathOrThrow("cwd", cwd)
        Forge(absCwd, githubBackend (VcsToolkit.GitHub.GitHub.Create().WithToken token))

    /// A GitLab-backed handle that authenticates with an explicit `token` (injected as
    /// `GITLAB_TOKEN`) instead of glab's ambient login.
    static member GitLabWithToken(cwd: string, token: string) =
        let absCwd = Forge.NormalizePathOrThrow("cwd", cwd)
        Forge(absCwd, gitlabBackend (VcsToolkit.GitLab.GitLab.Create().WithToken token))

    /// Build a GitHub-backed handle from an explicit client — for a custom runner (e.g. a
    /// test seam) or a pre-configured `GitHub`.
    static member FromGitHub(cwd: string, client: VcsToolkit.GitHub.GitHub) =
        let absCwd = Forge.NormalizePathOrThrow("cwd", cwd)
        Forge(absCwd, githubBackend client)

    /// Build a GitLab-backed handle from an explicit `GitLab` client.
    static member FromGitLab(cwd: string, client: VcsToolkit.GitLab.GitLab) =
        let absCwd = Forge.NormalizePathOrThrow("cwd", cwd)
        Forge(absCwd, gitlabBackend client)

    /// Build a Gitea-backed handle from an explicit `Gitea` client.
    static member FromGitea(cwd: string, client: VcsToolkit.Gitea.Gitea) =
        let absCwd = Forge.NormalizePathOrThrow("cwd", cwd)
        Forge(absCwd, giteaBackend client)

    /// Build a handle for a remote that didn't classify as a known forge. The handle has
    /// no CLI client — every operation returns `Unsupported`, and `Capabilities` returns
    /// the all-`false` shape without spawning.
    static member FromUnknown(cwd: string) =
        let absCwd = Forge.NormalizePathOrThrow("cwd", cwd)
        Forge(absCwd, Backend.Unknown)

    // --- Identity / re-anchoring / capability introspection ------------------

    /// Which forge drives this handle.
    member _.Kind =
        match backend with
        | Backend.GitHub _ -> ForgeKind.GitHub
        | Backend.GitLab _ -> ForgeKind.GitLab
        | Backend.Gitea _ -> ForgeKind.Gitea
        | Backend.Unknown -> ForgeKind.Unknown

    /// The directory operations run against.
    member _.Cwd = cwd

    /// A sibling handle bound to `dir`, sharing this handle's client. `dir` is absolutised
    /// now so later operations do not inherit the process cwd.
    member _.At(dir: string) =
        let absDir = Forge.NormalizePathOrThrow("dir", dir)
        Forge(absDir, backend)

    /// The underlying `GitHub` client, or `None` when another forge backs this handle — an escape
    /// hatch to `gh`-only operations off the forge-agnostic surface (Actions runs, `prReview`/
    /// `prFeedback`, the `api` REST/GraphQL hatch). Carries the token when this handle was built
    /// via `GitHubWithToken`/`FromGitHub`. Pass `Cwd` as `dir`. (`prMerge`'s auto-merge/
    /// delete-branch options are now on the unified surface via the `PrMerge` spec.)
    /// (The `Repo.Git`/`Repo.Jj` analogue for the forge facade; named `*Client` because the bare
    /// `GitHub`/`GitLab`/`Gitea` names are the handle's constructors.)
    member _.GitHubClient =
        match backend with
        | Backend.GitHub(c, _) -> Some c
        | _ -> None

    /// The underlying `GitLab` client, or `None` when another forge backs this handle — the
    /// escape hatch to `glab`-only operations (see `GitHubClient`). Carries the token when built
    /// via `GitLabWithToken`/`FromGitLab`.
    member _.GitLabClient =
        match backend with
        | Backend.GitLab(c, _) -> Some c
        | _ -> None

    /// The underlying `Gitea`/Forgejo (`tea`) client, or `None` when another forge backs this
    /// handle — the escape hatch to `tea`-only operations (see `GitHubClient`).
    member _.GiteaClient =
        match backend with
        | Backend.Gitea(c, _) -> Some c
        | _ -> None

    /// Whether this handle's backend supports `op` — an **operation-level** gap only. The
    /// capability-varying operations (`ForgeOp`) are all present on GitHub and GitLab; Gitea
    /// (`tea`) supports **none** of them, and an `Unknown` handle (no CLI) supports nothing — so
    /// this agrees with the dispatch and `Capabilities` rather than contradicting them. Branch on
    /// this to hide an unavailable operation up front. Operations that exist everywhere but refuse
    /// a specific *variant* aren't `ForgeOp`s — see `SupportsReview`/`SupportsMergeOptions`/
    /// `SupportsCloseDeleteBranch` for those.
    member this.Supports(op: ForgeOp) =
        match this.Kind, op with
        | ForgeKind.Unknown, _ -> false
        | ForgeKind.Gitea,
          (ForgeOp.RepoView | ForgeOp.PrMarkReady | ForgeOp.PrChecks | ForgeOp.ReleaseView | ForgeOp.PrDiff | ForgeOp.IssueReopen | ForgeOp.ReleaseDelete) ->
            false
        | _ -> true

    /// Whether this handle's backend can submit a `PrReview` of `kind`. Unlike `Supports` (which
    /// answers *operation-level* gaps), `prReview` exists on every CLI but honours a different set
    /// of review kinds: `Approve` on all three, `RequestChanges` on GitHub/Gitea (not GitLab), a
    /// `Comment`-review on GitHub only — and none on an `Unknown` handle. This agrees with
    /// `PrReview`'s dispatch, which refuses an unsupported kind up front with `Unsupported` before
    /// any spawn. Branch on it to pick a supported review kind.
    member this.SupportsReview(kind: ReviewKind) = ForgeSupport.review this.Kind kind

    /// Whether this handle's backend honours `PrMerge`'s `Auto`/`DeleteBranch` options — real
    /// `gh` flags on GitHub, but unsupported on GitLab/Gitea (and an `Unknown` handle), where a
    /// spec asking for either is refused with `Unsupported` before any spawn. A plain strategy
    /// merge works everywhere regardless. Agrees with `PrMerge`'s dispatch.
    member this.SupportsMergeOptions = ForgeSupport.mergeOptions this.Kind

    /// Whether this handle's backend can delete the source branch when closing via
    /// `PrClose(number, deleteBranch = true)` — a real `gh` flag on GitHub, unsupported on
    /// GitLab/Gitea (and an `Unknown` handle), where requesting it is refused with `Unsupported`
    /// before any spawn. A branch-preserving close works everywhere. Agrees with `PrClose`'s
    /// dispatch.
    member this.SupportsCloseDeleteBranch = ForgeSupport.closeDeleteBranch this.Kind

    /// Whether this handle's backend honours `ReleaseCreate`'s `Draft`/`Prerelease` options —
    /// real `gh`/`tea` flags on GitHub and Gitea, but unsupported on GitLab (whose `glab` has no
    /// release draft/pre-release concept) and an `Unknown` handle, where a spec asking for either
    /// is refused with `Unsupported` before any spawn. A plain release works everywhere regardless.
    /// Agrees with `ReleaseCreate`'s dispatch.
    member this.SupportsReleaseOptions = ForgeSupport.releaseOptions this.Kind

    // --- Auth / repo ---------------------------------------------------------

    /// Whether the user is authenticated (GitHub/GitLab: a zero-exit `auth status`;
    /// Gitea: at least one configured login). An `Unknown` handle returns `Ok false`.
    member _.AuthStatus() =
        match backend with
        | Backend.GitHub(c, _) -> GitHubForge.authStatus c
        | Backend.GitLab(c, _) -> GitLabForge.authStatus c
        | Backend.Gitea(c, _) -> GiteaForge.authStatus c
        | Backend.Unknown -> task { return Ok false }

    /// The repository/project for the bound directory. **`Unsupported` on Gitea** (`tea`
    /// has no current-repo view).
    member _.RepoView() =
        match backend with
        | Backend.GitHub(c, _) -> GitHubForge.repoView c cwd
        | Backend.GitLab(c, _) -> GitLabForge.repoView c cwd
        | Backend.Gitea _ -> task { return Error(ForgeError.Unsupported(ForgeKind.Gitea, "repoView")) }
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "repoView")) }

    /// The forge's flat capability map — the intersection of "the CLI ships this command"
    /// and "the CLI is authenticated", plus the detected CLI version and backend kind.
    /// Spawns the auth probe, and (on an authenticated CLI) a `--version` probe — but the
    /// version probe reuses this handle's cached `versionProbe` (see `Backend`) rather than
    /// spawning `--version` independently: the installed CLI's version is reported as
    /// "current state" here just like anywhere else on the handle, and it cannot actually
    /// change mid-process, so replaying the cached result costs nothing in accuracy while
    /// still saving the spawn. The version/kind are reported independently of auth; a
    /// `--version` banner that doesn't parse degrades to `Version = None` without failing
    /// the call. The `Unknown` handle's map is the all-`false` shape (`Version = None`,
    /// `Kind = Unknown`), spawning nothing.
    member _.Capabilities() =
        task {
            match backend with
            | Backend.GitHub(c, probe) ->
                match! GitHubForge.authStatus c with
                | Error e -> return Error e
                | Ok authed ->
                    let! version = GitHubForge.detectVersion probe
                    return Ok(applyAuth staticGitHubCaps ForgeKind.GitHub version authed)
            | Backend.GitLab(c, probe) ->
                match! GitLabForge.authStatus c with
                | Error e -> return Error e
                | Ok authed ->
                    let! version = GitLabForge.detectVersion probe
                    return Ok(applyAuth staticGitLabCaps ForgeKind.GitLab version authed)
            | Backend.Gitea(c, probe) ->
                match! GiteaForge.authStatus c with
                | Error e -> return Error e
                | Ok authed ->
                    let! version = GiteaForge.detectVersion probe
                    return Ok(applyAuth staticGiteaCaps ForgeKind.Gitea version authed)
            | Backend.Unknown -> return Ok ForgeCapabilities.AllFalse
        }

    // --- PR/MR lifecycle -----------------------------------------------------

    /// Pull/merge requests for the bound directory — the facade's previous, options-less
    /// behaviour (open, up to 100). Kept as a genuine zero-argument overload (rather than
    /// folding into an `?options` optional parameter) for CLR binary compatibility: F#'s
    /// `?options` sugar still compiles to a required parameter at the metadata level, so an
    /// already-compiled caller of the pre-`PrListOptions` `PrList()` would hit
    /// `MissingMethodException` against a build that replaced it outright.
    member this.PrList() = this.PrList(PrListOptions.Default)

    /// Pull/merge requests for the bound directory, filtered and capped by `options`.
    /// **`Unsupported` on Gitea for every state** (`tea pr list --output json` does not work
    /// against the real CLI at all — K-049; see `PrListState`).
    member _.PrList(options: PrListOptions) =
        match backend with
        | Backend.GitHub(c, _) -> GitHubForge.prList c cwd options
        | Backend.GitLab(c, _) -> GitLabForge.prList c cwd options
        | Backend.Gitea(c, _) -> GiteaForge.prList c cwd options
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prList")) }

    /// PR/MRs whose source branch is `sourceBranch`, in any state, regardless of target
    /// branch — the "after pushing, find my PR" query. Returns a **list**, not a single
    /// value: a branch can have more than one PR/MR against it over its lifetime (e.g.
    /// closed then reopened, or opened against two different targets), so this mirrors
    /// `PrList`'s "list, not a singleton" shape rather than `PrView`'s. An empty list means
    /// no PR/MR currently matches `sourceBranch` — not an error.
    /// - **GitHub** — `gh pr list --head <sourceBranch> --state all` (any base branch;
    ///   the caller does not supply a target here).
    /// - **GitLab** — `glab mr list --source-branch <sourceBranch> --all`.
    /// - **`Unsupported` on Gitea** (`tea pr list --output json` does not work against the
    ///   real CLI for any state — K-049; same root cause as `PrList`).
    member _.PrForBranch(sourceBranch: string) =
        match backend with
        | Backend.GitHub(c, _) -> GitHubForge.prForBranch c cwd sourceBranch
        | Backend.GitLab(c, _) -> GitLabForge.prForBranch c cwd sourceBranch
        | Backend.Gitea(c, _) -> GiteaForge.prForBranch c cwd sourceBranch
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prForBranch")) }

    /// A single PR/MR by number (GitLab `iid`). On Gitea this lists and filters.
    member _.PrView(number: uint64) =
        match backend with
        | Backend.GitHub(c, _) -> GitHubForge.prView c cwd number
        | Backend.GitLab(c, _) -> GitLabForge.prView c cwd number
        | Backend.Gitea(c, _) -> GiteaForge.prView c cwd number
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prView")) }

    /// Open a PR/MR (see `PrCreate`), returning the CLI's success output — a URL on
    /// GitHub/GitLab; `tea` prints a textual summary (no URL). Version-gated: refused with
    /// `UnsupportedVersion` before spawning if the CLI is below the wrapper's floor.
    member _.PrCreate(spec: PrCreate) =
        gated backend "prCreate" (fun () ->
            match backend with
            | Backend.GitHub(c, _) -> GitHubForge.prCreate c cwd spec
            | Backend.GitLab(c, _) -> GitLabForge.prCreate c cwd spec
            | Backend.Gitea(c, _) -> GiteaForge.prCreate c cwd spec
            | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prCreate")) })

    /// Post a comment to an existing PR/MR. An empty (or whitespace-only) body is rejected
    /// with `InvalidInput` before any CLI spawn. Note: on Gitea the body is a positional,
    /// so a body whose first non-space character is `-` is rejected by the client.
    member _.PrComment(number: uint64, body: string) =
        task {
            if body.Trim().Length = 0 then
                return Error(ForgeError.InvalidInput "prComment: comment body must not be empty")
            else
                match backend with
                | Backend.GitHub(c, _) -> return! GitHubForge.prComment c cwd number body
                | Backend.GitLab(c, _) -> return! GitLabForge.prComment c cwd number body
                | Backend.Gitea(c, _) -> return! GiteaForge.prComment c cwd number body
                | Backend.Unknown -> return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prComment"))
        }

    /// Edit a PR/MR's title and/or body (see `PrEdit`). At least one of `Title`/`Body`
    /// must be `Some` — both-`None` is rejected before any CLI is spawned. Version-gated
    /// once the input passes: refused with `UnsupportedVersion` before spawning if the CLI
    /// is below the wrapper's floor.
    /// **`Unsupported` on Gitea** (`tea` 0.9.2 has no `pr edit` command — an unrecognised
    /// `pr edit` silently falls through to `pr list`; K-063): refused structurally before any
    /// spawn — including the version probe and the both-`None` input check, neither of which is
    /// meaningful when the command can never run — like `PrChecks`/`PrDiff`/`PrList` on Gitea.
    member _.PrEdit(number: uint64, edit: PrEdit) =
        task {
            match backend with
            | Backend.Gitea(c, _) ->
                // tea has no `pr edit`; `GiteaForge.prEdit` refuses with `Unsupported` before any
                // spawn (including the version probe below), so route straight to it.
                return! GiteaForge.prEdit c cwd number edit
            | _ ->
                if edit.Title.IsNone && edit.Body.IsNone then
                    return Error(ForgeError.InvalidInput "prEdit: at least one of title or body must be set")
                else
                    return!
                        gated backend "prEdit" (fun () ->
                            match backend with
                            | Backend.GitHub(c, _) -> GitHubForge.prEdit c cwd number edit
                            | Backend.GitLab(c, _) -> GitLabForge.prEdit c cwd number edit
                            | Backend.Gitea(c, _) -> GiteaForge.prEdit c cwd number edit
                            | Backend.Unknown ->
                                task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prEdit")) })
        }

    /// Merge a PR/MR with the given unified `PrMerge` spec. `Auto`/`DeleteBranch` map to real
    /// `gh` flags on GitHub; on GitLab and Gitea — whose CLIs expose no confirmed equivalent —
    /// a spec asking for either is refused structurally with `Unsupported` **before any spawn**
    /// (including the version probe), rather than silently dropping the option. A plain strategy
    /// merge works on all three. Version-gated: refused with `UnsupportedVersion` before
    /// spawning if the CLI is below the wrapper's floor.
    member _.PrMerge(number: uint64, merge: PrMerge) =
        // The GitLab/Gitea backends own the "auto/delete-branch is unsupported here" verdict; a
        // hit short-circuits before `gated` so nothing spawns. No catch-all: a new spec option
        // must decide its support at each backend explicitly.
        let unsupported =
            match backend with
            | Backend.GitLab _ -> ForgeSupport.unsupportedMerge ForgeKind.GitLab merge
            | Backend.Gitea _ -> ForgeSupport.unsupportedMerge ForgeKind.Gitea merge
            | Backend.GitHub _
            | Backend.Unknown -> None

        match unsupported with
        | Some e -> task { return Error e }
        | None ->
            gated backend "prMerge" (fun () ->
                match backend with
                | Backend.GitHub(c, _) -> GitHubForge.prMerge c cwd number merge
                | Backend.GitLab(c, _) -> GitLabForge.prMerge c cwd number merge.Strategy
                | Backend.Gitea(c, _) -> GiteaForge.prMerge c cwd number merge.Strategy
                | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prMerge")) })

    /// Mark a draft PR/MR as ready for review. **`Unsupported` on Gitea** (`tea` has no
    /// draft toggle — a Gitea draft is a `WIP:` title prefix, edited via the raw client).
    member _.PrMarkReady(number: uint64) =
        match backend with
        | Backend.GitHub(c, _) -> GitHubForge.prMarkReady c cwd number
        | Backend.GitLab(c, _) -> GitLabForge.prMarkReady c cwd number
        | Backend.Gitea _ -> task { return Error(ForgeError.Unsupported(ForgeKind.Gitea, "prMarkReady")) }
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prMarkReady")) }

    /// Close a PR/MR without merging. `deleteBranch` maps to the real `gh` flag on GitHub; on
    /// GitLab and Gitea — whose CLIs expose no confirmed equivalent — requesting it is refused
    /// structurally with `Unsupported` **before any spawn**, rather than silently dropping the
    /// option. Closing without deleting the branch works on all three.
    member _.PrClose(number: uint64, deleteBranch: bool) =
        // The GitLab/Gitea backends own the support verdict; a hit returns before dispatch, so
        // the unsupported request cannot reach the CLI.
        let unsupported =
            match backend with
            | Backend.GitLab _ -> ForgeSupport.unsupportedCloseDeleteBranch ForgeKind.GitLab deleteBranch
            | Backend.Gitea _ -> ForgeSupport.unsupportedCloseDeleteBranch ForgeKind.Gitea deleteBranch
            | Backend.GitHub _
            | Backend.Unknown -> None

        match unsupported with
        | Some e -> task { return Error e }
        | None ->
            match backend with
            | Backend.GitHub(c, _) -> GitHubForge.prClose c cwd number deleteBranch
            | Backend.GitLab(c, _) -> GitLabForge.prClose c cwd number
            | Backend.Gitea(c, _) -> GiteaForge.prClose c cwd number
            | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prClose")) }

    /// Check out a PR/MR's branch into the bound directory (`gh pr checkout` /
    /// `glab mr checkout` / `tea pr checkout`). Unlike the remote-only operations, this is a
    /// **local-worktree mutation**: it fetches the PR/MR's source branch and switches the
    /// working tree in `cwd` to it. Supported on all three CLIs (so it is *not* a
    /// capability-varying `ForgeOp`); only the CLI-less `Unknown` handle returns
    /// `Unsupported`, without spawning.
    member _.PrCheckout(number: uint64) =
        match backend with
        | Backend.GitHub(c, _) -> GitHubForge.prCheckout c cwd number
        | Backend.GitLab(c, _) -> GitLabForge.prCheckout c cwd number
        | Backend.Gitea(c, _) -> GiteaForge.prCheckout c cwd number
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prCheckout")) }

    /// Submit a review on a PR/MR (see `ReviewAction`) — the second half of the PR lifecycle,
    /// unified across the three forges. Support varies by review *kind*, not by operation, so an
    /// unsupported combination is refused structurally with `Unsupported` **before any spawn**
    /// (including the version probe), the same way `PrMerge`'s auto/delete-branch is:
    /// - `Approve` — all three (`gh pr review --approve` / `glab mr approve` / `tea pr approve`).
    /// - `RequestChanges` — GitHub (`--request-changes`) and Gitea (`tea pr reject`); on GitLab
    ///   `glab` has no equivalent, so it is `Unsupported` (no unsafe note+revoke composition).
    /// - `Comment`-review — GitHub only (`--comment`); on GitLab and Gitea it is `Unsupported`
    ///   (`PrComment`/`MrComment` still posts a plain comment there).
    /// Version-gated once the kind is supported: refused with `UnsupportedVersion` before
    /// spawning if the CLI is below the wrapper's floor.
    member _.PrReview(number: uint64, action: ReviewAction) =
        // The GitLab/Gitea backends own the "this review kind is unsupported here" verdict; a hit
        // short-circuits before `gated` so nothing spawns. No catch-all: a new review kind must
        // decide its support at each backend explicitly.
        let unsupported =
            match backend with
            | Backend.GitLab _ -> ForgeSupport.unsupportedReview ForgeKind.GitLab action
            | Backend.Gitea _ -> ForgeSupport.unsupportedReview ForgeKind.Gitea action
            | Backend.GitHub _
            | Backend.Unknown -> None

        match unsupported with
        | Some e -> task { return Error e }
        | None ->
            gated backend "prReview" (fun () ->
                match backend with
                | Backend.GitHub(c, _) -> GitHubForge.prReview c cwd number action
                | Backend.GitLab(c, _) -> GitLabForge.prReview c cwd number action
                | Backend.Gitea(c, _) -> GiteaForge.prReview c cwd number action
                | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prReview")) })

    /// The PR/MR's coarse CI status (see `CiStatus`). **`Unsupported` on Gitea** (`tea`
    /// has no checks command).
    member _.PrChecks(number: uint64) =
        match backend with
        | Backend.GitHub(c, _) -> GitHubForge.prChecks c cwd number
        | Backend.GitLab(c, _) -> GitLabForge.prChecks c cwd number
        | Backend.Gitea _ -> task { return Error(ForgeError.Unsupported(ForgeKind.Gitea, "prChecks")) }
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prChecks")) }

    /// The PR/MR's unified diff, parsed into per-file `FileDiff` values (`gh pr diff <n>` /
    /// `glab mr diff <n>`, through the selected backend adapter).
    /// **`Unsupported` on Gitea** (`tea` has no diff command) and on an `Unknown` handle.
    member _.PrDiff(number: uint64) =
        match backend with
        | Backend.GitHub(c, _) -> GitHubForge.prDiff c cwd number
        | Backend.GitLab(c, _) -> GitLabForge.prDiff c cwd number
        | Backend.Gitea _ -> task { return Error(ForgeError.Unsupported(ForgeKind.Gitea, "prDiff")) }
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prDiff")) }

    // --- Issues / releases ---------------------------------------------------

    /// Issues for the bound directory — the facade's previous, options-less behaviour
    /// (open, up to 100). Kept as a genuine zero-argument overload for CLR binary
    /// compatibility (see `PrList`'s doc comment for the rationale).
    member this.IssueList() =
        this.IssueList(IssueListOptions.Default)

    /// Issues for the bound directory, filtered and capped by `options`.
    /// **`Unsupported` on Gitea for every state** (`tea issues list --output json` does not
    /// work against the real CLI at all — K-049; see `IssueListState`).
    member _.IssueList(options: IssueListOptions) =
        match backend with
        | Backend.GitHub(c, _) -> GitHubForge.issueList c cwd options
        | Backend.GitLab(c, _) -> GitLabForge.issueList c cwd options
        | Backend.Gitea(c, _) -> GiteaForge.issueList c cwd options
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "issueList")) }

    /// A single issue by number (GitLab `iid`), with `Body`/`Url` filled.
    member _.IssueView(number: uint64) =
        match backend with
        | Backend.GitHub(c, _) -> GitHubForge.issueView c cwd number
        | Backend.GitLab(c, _) -> GitLabForge.issueView c cwd number
        | Backend.Gitea(c, _) -> GiteaForge.issueView c cwd number
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "issueView")) }

    /// Open an issue, returning the CLI's success output — a URL on GitHub/GitLab; `tea`
    /// prints a textual summary whose final line is the URL. Version-gated: refused with
    /// `UnsupportedVersion` before spawning if the CLI is below the wrapper's floor.
    member _.IssueCreate(title: string, body: string) =
        gated backend "issueCreate" (fun () ->
            match backend with
            | Backend.GitHub(c, _) -> GitHubForge.issueCreate c cwd title body
            | Backend.GitLab(c, _) -> GitLabForge.issueCreate c cwd title body
            | Backend.Gitea(c, _) -> GiteaForge.issueCreate c cwd title body
            | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "issueCreate")) })

    /// Close an issue (reopenable — no data is discarded). All three CLIs support it
    /// (`gh issue close` / `glab issue close` / `tea issues close`), so it is not a
    /// capability-varying `ForgeOp`; only the CLI-less `Unknown` handle is `Unsupported`,
    /// without spawning. Version-gated like the other mutations: refused with
    /// `UnsupportedVersion` before spawning if the CLI is below the wrapper's floor.
    member _.IssueClose(number: uint64) =
        gated backend "issueClose" (fun () ->
            match backend with
            | Backend.GitHub(c, _) -> GitHubForge.issueClose c cwd number
            | Backend.GitLab(c, _) -> GitLabForge.issueClose c cwd number
            | Backend.Gitea(c, _) -> GiteaForge.issueClose c cwd number
            | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "issueClose")) })

    /// Reopen a closed issue. GitHub and GitLab expose native reopen commands; `tea` 0.9.2
    /// does not, so Gitea and Unknown handles return `Unsupported` before any version probe or
    /// operation spawn. Version-gated like the other supported mutations.
    member _.IssueReopen(number: uint64) =
        match backend with
        | Backend.Gitea _ -> task { return Error(ForgeError.Unsupported(ForgeKind.Gitea, "issueReopen")) }
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "issueReopen")) }
        | _ ->
            gated backend "issueReopen" (fun () ->
                match backend with
                | Backend.GitHub(c, _) -> GitHubForge.issueReopen c cwd number
                | Backend.GitLab(c, _) -> GitLabForge.issueReopen c cwd number
                | Backend.Gitea _ -> task { return Error(ForgeError.Unsupported(ForgeKind.Gitea, "issueReopen")) }
                | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "issueReopen")) })

    /// Post a comment to an existing issue, returning the CLI's success output. An empty
    /// (or whitespace-only) body is rejected with `InvalidInput` before any CLI spawn — by
    /// the `PrComment` pattern. Note: on Gitea the body is a positional, so a body whose
    /// first non-space character is `-` is rejected by the client. Supported on all three
    /// CLIs (`gh issue comment` / `glab issue note` / `tea comment`); only the `Unknown`
    /// handle is `Unsupported`. Version-gated once the input passes.
    member _.IssueComment(number: uint64, body: string) =
        task {
            if body.Trim().Length = 0 then
                return Error(ForgeError.InvalidInput "issueComment: comment body must not be empty")
            else
                return!
                    gated backend "issueComment" (fun () ->
                        match backend with
                        | Backend.GitHub(c, _) -> GitHubForge.issueComment c cwd number body
                        | Backend.GitLab(c, _) -> GitLabForge.issueComment c cwd number body
                        | Backend.Gitea(c, _) -> GiteaForge.issueComment c cwd number body
                        | Backend.Unknown ->
                            task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "issueComment")) })
        }

    /// Releases for the bound directory, newest first (up to 100).
    member _.ReleaseList() =
        match backend with
        | Backend.GitHub(c, _) -> GitHubForge.releaseList c cwd
        | Backend.GitLab(c, _) -> GitLabForge.releaseList c cwd
        | Backend.Gitea(c, _) -> GiteaForge.releaseList c cwd
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "releaseList")) }

    /// A single release by tag. **`Unsupported` on Gitea** (`tea releases` always lists —
    /// filter `ReleaseList` instead).
    member _.ReleaseView(tag: string) =
        match backend with
        | Backend.GitHub(c, _) -> GitHubForge.releaseView c cwd tag
        | Backend.GitLab(c, _) -> GitLabForge.releaseView c cwd tag
        | Backend.Gitea _ -> task { return Error(ForgeError.Unsupported(ForgeKind.Gitea, "releaseView")) }
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "releaseView")) }

    /// Create a release for `tag`, returning the CLI's success output — the release URL on
    /// GitHub/GitLab; `tea` prints a textual summary (no URL). `Draft`/`Prerelease` map to real
    /// `gh`/`tea` flags on GitHub and Gitea; on GitLab — whose `glab` has no release
    /// draft/pre-release concept — a spec asking for either is refused structurally with
    /// `Unsupported` **before any spawn** (including the version probe), rather than silently
    /// dropping the option. A plain release (tag + optional title/notes) works on all three.
    /// Version-gated once the options pass: refused with `UnsupportedVersion` before spawning if
    /// the CLI is below the wrapper's floor.
    member _.ReleaseCreate(spec: ReleaseCreate) =
        // The GitLab backend owns the "draft/prerelease is unsupported here" verdict; a hit
        // short-circuits before `gated` so nothing spawns. No catch-all: a new spec option must
        // decide its support at each backend explicitly.
        let unsupported =
            match backend with
            | Backend.GitLab _ -> ForgeSupport.unsupportedReleaseCreate ForgeKind.GitLab spec
            | Backend.GitHub _
            | Backend.Gitea _
            | Backend.Unknown -> None

        match unsupported with
        | Some e -> task { return Error e }
        | None ->
            gated backend "releaseCreate" (fun () ->
                match backend with
                | Backend.GitHub(c, _) -> GitHubForge.releaseCreate c cwd spec
                | Backend.GitLab(c, _) -> GitLabForge.releaseCreate c cwd spec
                | Backend.Gitea(c, _) -> GiteaForge.releaseCreate c cwd spec
                | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "releaseCreate")) })

    /// Delete a release by tag. GitHub and GitLab expose native delete commands; `tea` 0.9.2
    /// does not, so Gitea and Unknown handles return `Unsupported` before any version probe or
    /// operation spawn. The wrapper always supplies the confirmation flag on supported CLIs.
    member _.ReleaseDelete(tag: string) =
        match backend with
        | Backend.Gitea _ -> task { return Error(ForgeError.Unsupported(ForgeKind.Gitea, "releaseDelete")) }
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "releaseDelete")) }
        | _ ->
            gated backend "releaseDelete" (fun () ->
                match backend with
                | Backend.GitHub(c, _) -> GitHubForge.releaseDelete c cwd tag
                | Backend.GitLab(c, _) -> GitLabForge.releaseDelete c cwd tag
                | Backend.Gitea _ -> task { return Error(ForgeError.Unsupported(ForgeKind.Gitea, "releaseDelete")) }
                | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "releaseDelete")) })
