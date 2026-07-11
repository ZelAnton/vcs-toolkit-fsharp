namespace VcsToolkit.Forge

open System.Threading.Tasks

/// The per-CLI client behind a `Forge`. `Unknown` carries no client — the remote URL
/// didn't classify as a known forge, so no CLI can be picked; the handle exists only to
/// surface the all-`false` capability map. Clients are reference types, so a sibling
/// handle from `Forge.At` shares the same client instance.
[<RequireQualifiedAccess>]
type internal Backend =
    | GitHub of VcsToolkit.GitHub.GitHub
    | GitLab of VcsToolkit.GitLab.GitLab
    | Gitea of VcsToolkit.Gitea.Gitea
    | Unknown

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
          Authed = false
          Version = None
          Kind = ForgeKind.Unknown }

    /// GitLab ships the same command set as GitHub on the lean surface.
    let staticGitLabCaps: ForgeCapabilities = staticGitHubCaps

    /// Gitea's `tea` has no checks command, so `PrChecks` is `false`.
    let staticGiteaCaps: ForgeCapabilities =
        { staticGitHubCaps with
            PrChecks = false }

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
                | Backend.GitHub c -> GitHubForge.ensureVersion c op
                | Backend.GitLab c -> GitLabForge.ensureVersion c op
                | Backend.Gitea c -> GiteaForge.ensureVersion c op
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

    /// A GitHub-backed handle bound to `cwd`, using the real job-backed runner (gh's
    /// ambient login).
    static member GitHub(cwd: string) =
        Forge(cwd, Backend.GitHub(VcsToolkit.GitHub.GitHub.Create()))

    /// A GitLab-backed handle bound to `cwd` (glab's ambient login).
    static member GitLab(cwd: string) =
        Forge(cwd, Backend.GitLab(VcsToolkit.GitLab.GitLab.Create()))

    /// A Gitea-backed handle bound to `cwd`. Gitea authenticates **only** through `tea`'s
    /// ambient login (`tea login add`) — there is no `GiteaWithToken`, because `tea` has
    /// no token-via-environment override the way `gh`/`glab` do.
    static member Gitea(cwd: string) =
        Forge(cwd, Backend.Gitea(VcsToolkit.Gitea.Gitea.Create()))

    /// A GitHub-backed handle that authenticates with an explicit `token` (injected as
    /// `GH_TOKEN`) instead of gh's ambient login.
    static member GitHubWithToken(cwd: string, token: string) =
        Forge(cwd, Backend.GitHub(VcsToolkit.GitHub.GitHub.Create().WithToken token))

    /// A GitLab-backed handle that authenticates with an explicit `token` (injected as
    /// `GITLAB_TOKEN`) instead of glab's ambient login.
    static member GitLabWithToken(cwd: string, token: string) =
        Forge(cwd, Backend.GitLab(VcsToolkit.GitLab.GitLab.Create().WithToken token))

    /// Build a GitHub-backed handle from an explicit client — for a custom runner (e.g. a
    /// test seam) or a pre-configured `GitHub`.
    static member FromGitHub(cwd: string, client: VcsToolkit.GitHub.GitHub) = Forge(cwd, Backend.GitHub client)

    /// Build a GitLab-backed handle from an explicit `GitLab` client.
    static member FromGitLab(cwd: string, client: VcsToolkit.GitLab.GitLab) = Forge(cwd, Backend.GitLab client)

    /// Build a Gitea-backed handle from an explicit `Gitea` client.
    static member FromGitea(cwd: string, client: VcsToolkit.Gitea.Gitea) = Forge(cwd, Backend.Gitea client)

    /// Build a handle for a remote that didn't classify as a known forge. The handle has
    /// no CLI client — every operation returns `Unsupported`, and `Capabilities` returns
    /// the all-`false` shape without spawning.
    static member FromUnknown(cwd: string) = Forge(cwd, Backend.Unknown)

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

    /// A sibling handle bound to `dir`, sharing this handle's client.
    member _.At(dir: string) = Forge(dir, backend)

    /// The underlying `GitHub` client, or `None` when another forge backs this handle — an escape
    /// hatch to `gh`-only operations off the forge-agnostic surface (Actions runs, `prReview`/
    /// `prFeedback`, the `api` REST/GraphQL hatch). Carries the token when this handle was built
    /// via `GitHubWithToken`/`FromGitHub`. Pass `Cwd` as `dir`. (`prMerge`'s auto-merge/
    /// delete-branch options are now on the unified surface via the `PrMerge` spec.)
    /// (The `Repo.Git`/`Repo.Jj` analogue for the forge facade; named `*Client` because the bare
    /// `GitHub`/`GitLab`/`Gitea` names are the handle's constructors.)
    member _.GitHubClient =
        match backend with
        | Backend.GitHub c -> Some c
        | _ -> None

    /// The underlying `GitLab` client, or `None` when another forge backs this handle — the
    /// escape hatch to `glab`-only operations (see `GitHubClient`). Carries the token when built
    /// via `GitLabWithToken`/`FromGitLab`.
    member _.GitLabClient =
        match backend with
        | Backend.GitLab c -> Some c
        | _ -> None

    /// The underlying `Gitea`/Forgejo (`tea`) client, or `None` when another forge backs this
    /// handle — the escape hatch to `tea`-only operations (see `GitHubClient`).
    member _.GiteaClient =
        match backend with
        | Backend.Gitea c -> Some c
        | _ -> None

    /// Whether this handle's backend supports `op`. The capability-varying operations
    /// (`ForgeOp`) are all present on GitHub and GitLab; Gitea (`tea`) supports **none**
    /// of them, and an `Unknown` handle (no CLI) supports nothing — so this agrees with
    /// the dispatch and `Capabilities` rather than contradicting them. Branch on this to
    /// hide an unavailable operation up front.
    member this.Supports(op: ForgeOp) =
        match this.Kind, op with
        | ForgeKind.Unknown, _ -> false
        | ForgeKind.Gitea, (ForgeOp.RepoView | ForgeOp.PrMarkReady | ForgeOp.PrChecks | ForgeOp.ReleaseView) -> false
        | _ -> true

    // --- Auth / repo ---------------------------------------------------------

    /// Whether the user is authenticated (GitHub/GitLab: a zero-exit `auth status`;
    /// Gitea: at least one configured login). An `Unknown` handle returns `Ok false`.
    member _.AuthStatus() =
        match backend with
        | Backend.GitHub c -> GitHubForge.authStatus c
        | Backend.GitLab c -> GitLabForge.authStatus c
        | Backend.Gitea c -> GiteaForge.authStatus c
        | Backend.Unknown -> task { return Ok false }

    /// The repository/project for the bound directory. **`Unsupported` on Gitea** (`tea`
    /// has no current-repo view).
    member _.RepoView() =
        match backend with
        | Backend.GitHub c -> GitHubForge.repoView c cwd
        | Backend.GitLab c -> GitLabForge.repoView c cwd
        | Backend.Gitea _ -> task { return Error(ForgeError.Unsupported(ForgeKind.Gitea, "repoView")) }
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "repoView")) }

    /// The forge's flat capability map — the intersection of "the CLI ships this command"
    /// and "the CLI is authenticated", plus the detected CLI version and backend kind.
    /// Spawns the auth probe, and (on an authenticated CLI) a `--version` probe. The
    /// version/kind are reported independently of auth; a `--version` banner that doesn't
    /// parse degrades to `Version = None` without failing the call. The `Unknown` handle's
    /// map is the all-`false` shape (`Version = None`, `Kind = Unknown`), spawning nothing.
    member _.Capabilities() =
        task {
            match backend with
            | Backend.GitHub c ->
                match! GitHubForge.authStatus c with
                | Error e -> return Error e
                | Ok authed ->
                    let! version = GitHubForge.detectVersion c
                    return Ok(applyAuth staticGitHubCaps ForgeKind.GitHub version authed)
            | Backend.GitLab c ->
                match! GitLabForge.authStatus c with
                | Error e -> return Error e
                | Ok authed ->
                    let! version = GitLabForge.detectVersion c
                    return Ok(applyAuth staticGitLabCaps ForgeKind.GitLab version authed)
            | Backend.Gitea c ->
                match! GiteaForge.authStatus c with
                | Error e -> return Error e
                | Ok authed ->
                    let! version = GiteaForge.detectVersion c
                    return Ok(applyAuth staticGiteaCaps ForgeKind.Gitea version authed)
            | Backend.Unknown -> return Ok ForgeCapabilities.AllFalse
        }

    // --- PR/MR lifecycle -----------------------------------------------------

    /// Open pull/merge requests for the bound directory.
    member _.PrList() =
        match backend with
        | Backend.GitHub c -> GitHubForge.prList c cwd
        | Backend.GitLab c -> GitLabForge.prList c cwd
        | Backend.Gitea c -> GiteaForge.prList c cwd
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prList")) }

    /// A single PR/MR by number (GitLab `iid`). On Gitea this lists and filters.
    member _.PrView(number: uint64) =
        match backend with
        | Backend.GitHub c -> GitHubForge.prView c cwd number
        | Backend.GitLab c -> GitLabForge.prView c cwd number
        | Backend.Gitea c -> GiteaForge.prView c cwd number
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prView")) }

    /// Open a PR/MR (see `PrCreate`), returning the CLI's success output — a URL on
    /// GitHub/GitLab; `tea` prints a textual summary (no URL). Version-gated: refused with
    /// `UnsupportedVersion` before spawning if the CLI is below the wrapper's floor.
    member _.PrCreate(spec: PrCreate) =
        gated backend "prCreate" (fun () ->
            match backend with
            | Backend.GitHub c -> GitHubForge.prCreate c cwd spec
            | Backend.GitLab c -> GitLabForge.prCreate c cwd spec
            | Backend.Gitea c -> GiteaForge.prCreate c cwd spec
            | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prCreate")) })

    /// Post a comment to an existing PR/MR. An empty (or whitespace-only) body is rejected
    /// with `InvalidInput` before any CLI spawn. Note: on Gitea the body is a positional,
    /// so a body whose first non-space character is `-` is rejected by the client.
    member _.PrComment(number: uint64, body: string) =
        task {
            if body.Trim() = "" then
                return Error(ForgeError.InvalidInput "prComment: comment body must not be empty")
            else
                match backend with
                | Backend.GitHub c -> return! GitHubForge.prComment c cwd number body
                | Backend.GitLab c -> return! GitLabForge.prComment c cwd number body
                | Backend.Gitea c -> return! GiteaForge.prComment c cwd number body
                | Backend.Unknown -> return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prComment"))
        }

    /// Edit a PR/MR's title and/or body (see `PrEdit`). At least one of `Title`/`Body`
    /// must be `Some` — both-`None` is rejected before any CLI is spawned. Version-gated
    /// once the input passes: refused with `UnsupportedVersion` before spawning if the CLI
    /// is below the wrapper's floor.
    member _.PrEdit(number: uint64, edit: PrEdit) =
        task {
            if edit.Title.IsNone && edit.Body.IsNone then
                return Error(ForgeError.InvalidInput "prEdit: at least one of title or body must be set")
            else
                return!
                    gated backend "prEdit" (fun () ->
                        match backend with
                        | Backend.GitHub c -> GitHubForge.prEdit c cwd number edit
                        | Backend.GitLab c -> GitLabForge.prEdit c cwd number edit
                        | Backend.Gitea c -> GiteaForge.prEdit c cwd number edit
                        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prEdit")) })
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
            | Backend.GitLab _ -> GitLabForge.unsupportedMerge merge
            | Backend.Gitea _ -> GiteaForge.unsupportedMerge merge
            | Backend.GitHub _
            | Backend.Unknown -> None

        match unsupported with
        | Some e -> task { return Error e }
        | None ->
            gated backend "prMerge" (fun () ->
                match backend with
                | Backend.GitHub c -> GitHubForge.prMerge c cwd number merge
                | Backend.GitLab c -> GitLabForge.prMerge c cwd number merge.Strategy
                | Backend.Gitea c -> GiteaForge.prMerge c cwd number merge.Strategy
                | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prMerge")) })

    /// Mark a draft PR/MR as ready for review. **`Unsupported` on Gitea** (`tea` has no
    /// draft toggle — a Gitea draft is a `WIP:` title prefix, edited via the raw client).
    member _.PrMarkReady(number: uint64) =
        match backend with
        | Backend.GitHub c -> GitHubForge.prMarkReady c cwd number
        | Backend.GitLab c -> GitLabForge.prMarkReady c cwd number
        | Backend.Gitea _ -> task { return Error(ForgeError.Unsupported(ForgeKind.Gitea, "prMarkReady")) }
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prMarkReady")) }

    /// Close a PR/MR without merging. `deleteBranch` applies to GitHub only; GitLab and
    /// Gitea ignore it.
    member _.PrClose(number: uint64, deleteBranch: bool) =
        match backend with
        | Backend.GitHub c -> GitHubForge.prClose c cwd number deleteBranch
        | Backend.GitLab c -> GitLabForge.prClose c cwd number
        | Backend.Gitea c -> GiteaForge.prClose c cwd number
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prClose")) }

    /// Check out a PR/MR's branch into the bound directory (`gh pr checkout` /
    /// `glab mr checkout` / `tea pr checkout`). Unlike the remote-only operations, this is a
    /// **local-worktree mutation**: it fetches the PR/MR's source branch and switches the
    /// working tree in `cwd` to it. Supported on all three CLIs (so it is *not* a
    /// capability-varying `ForgeOp`); only the CLI-less `Unknown` handle returns
    /// `Unsupported`, without spawning.
    member _.PrCheckout(number: uint64) =
        match backend with
        | Backend.GitHub c -> GitHubForge.prCheckout c cwd number
        | Backend.GitLab c -> GitLabForge.prCheckout c cwd number
        | Backend.Gitea c -> GiteaForge.prCheckout c cwd number
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prCheckout")) }

    /// The PR/MR's coarse CI status (see `CiStatus`). **`Unsupported` on Gitea** (`tea`
    /// has no checks command).
    member _.PrChecks(number: uint64) =
        match backend with
        | Backend.GitHub c -> GitHubForge.prChecks c cwd number
        | Backend.GitLab c -> GitLabForge.prChecks c cwd number
        | Backend.Gitea _ -> task { return Error(ForgeError.Unsupported(ForgeKind.Gitea, "prChecks")) }
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prChecks")) }

    // --- Issues / releases ---------------------------------------------------

    /// Open issues for the bound directory (up to 100; drop to the underlying client for more).
    member _.IssueList() =
        match backend with
        | Backend.GitHub c -> GitHubForge.issueList c cwd
        | Backend.GitLab c -> GitLabForge.issueList c cwd
        | Backend.Gitea c -> GiteaForge.issueList c cwd
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "issueList")) }

    /// A single issue by number (GitLab `iid`), with `Body`/`Url` filled.
    member _.IssueView(number: uint64) =
        match backend with
        | Backend.GitHub c -> GitHubForge.issueView c cwd number
        | Backend.GitLab c -> GitLabForge.issueView c cwd number
        | Backend.Gitea c -> GiteaForge.issueView c cwd number
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "issueView")) }

    /// Open an issue, returning the CLI's success output — a URL on GitHub/GitLab; `tea`
    /// prints a textual summary whose final line is the URL. Version-gated: refused with
    /// `UnsupportedVersion` before spawning if the CLI is below the wrapper's floor.
    member _.IssueCreate(title: string, body: string) =
        gated backend "issueCreate" (fun () ->
            match backend with
            | Backend.GitHub c -> GitHubForge.issueCreate c cwd title body
            | Backend.GitLab c -> GitLabForge.issueCreate c cwd title body
            | Backend.Gitea c -> GiteaForge.issueCreate c cwd title body
            | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "issueCreate")) })

    /// Releases for the bound directory, newest first (up to 100).
    member _.ReleaseList() =
        match backend with
        | Backend.GitHub c -> GitHubForge.releaseList c cwd
        | Backend.GitLab c -> GitLabForge.releaseList c cwd
        | Backend.Gitea c -> GiteaForge.releaseList c cwd
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "releaseList")) }

    /// A single release by tag. **`Unsupported` on Gitea** (`tea releases` always lists —
    /// filter `ReleaseList` instead).
    member _.ReleaseView(tag: string) =
        match backend with
        | Backend.GitHub c -> GitHubForge.releaseView c cwd tag
        | Backend.GitLab c -> GitLabForge.releaseView c cwd tag
        | Backend.Gitea _ -> task { return Error(ForgeError.Unsupported(ForgeKind.Gitea, "releaseView")) }
        | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "releaseView")) }
