namespace VcsToolkit.Forge

open System
open System.Net
open System.Net.Sockets

// Forge-agnostic data types the facade returns, generalising the per-CLI shapes of
// VcsToolkit.GitHub / VcsToolkit.GitLab / VcsToolkit.Gitea into one set a consumer can
// use without knowing which forge is in play.

/// Host-classification helpers for `ForgeKind.OfRemoteUrl`.
[<AutoOpen>]
module private HostClassify =

    /// Whether `host` is exactly `domain` or a **proper subdomain** of it (`*.domain`)
    /// — an anchored match. A lookalike such as `gitlab.com.attacker.net` does NOT match
    /// `gitlab.com`, and `notgithub.com` does NOT match `github.com`.
    let hostIs (host: string) (domain: string) : bool =
        host = domain
        || (host.EndsWith(domain, StringComparison.Ordinal)
            && host.Length > domain.Length
            && host.[host.Length - domain.Length - 1] = '.')

    /// Extract the host from a git remote URL — scheme URLs (`https://host/…`,
    /// `ssh://git@host:22/…`, `https://[::1]:443/…`) and scp-like (`git@host:owner/repo`).
    /// For a scheme URL the host is bracket-aware: an IPv6 authority `[::1]:443` yields
    /// `::1`, but only when the bracket content is a *genuine* IPv6 literal — a bracketed
    /// name like `[gitlab.com]` or a colon-bearing fake `[a:b.gitlab.com]` must not be
    /// unwrapped (it could otherwise spoof a trusted host), so it yields `None`.
    let hostOf (url: string) : string option =
        match url.IndexOf("://", StringComparison.Ordinal) with
        | i when i >= 0 ->
            // A scheme URL: take the authority up to the next `/`/`?`/`#`, drop userinfo.
            let after = url.Substring(i + 3)
            let authority = after.Split([| '/'; '?'; '#' |]).[0]

            let hostPort =
                match authority.LastIndexOf('@') with
                | j when j >= 0 -> authority.Substring(j + 1)
                | _ -> authority

            if hostPort.StartsWith("[", StringComparison.Ordinal) then
                // Unwrap brackets ONLY when the content parses as a real IPv6 literal.
                let inner = hostPort.Substring(1).Split(']').[0]

                // Reject a zone/scope id (`%…`, incl. the `%25`-encoded form): .NET's
                // parser accepts an *arbitrary* scope string (`fe80::1%evil.gitlab.com`)
                // that Rust's `Ipv6Addr::parse` rejects — and returning the raw scope text
                // could let it spoof a trusted-domain suffix (`…%x.gitlab.com` → GitLab). A
                // genuine IPv6 literal in a URL bracket never carries a raw zone id.
                if inner.Contains('%') then
                    None
                else
                    match IPAddress.TryParse inner with
                    | true, addr ->
                        match addr with
                        | null -> None
                        | a when a.AddressFamily = AddressFamily.InterNetworkV6 -> Some inner
                        | _ -> None
                    | _ -> None
            else
                // Otherwise strip an optional `:port`.
                match hostPort.Split(':').[0] with
                | "" -> None
                | h -> Some h
        | _ ->
            // No scheme: scp-like `user@host:path` or bare `host:path` / `host/path`.
            let afterUser =
                match url.LastIndexOf('@') with
                | j when j >= 0 -> url.Substring(j + 1)
                | _ -> url

            match afterUser.Split([| ':'; '/' |]).[0] with
            | "" -> None
            | h -> Some h

/// Which forge backs a `Forge` handle.
[<RequireQualifiedAccess>]
type ForgeKind =
    /// GitHub (the `gh` CLI).
    | GitHub
    /// GitLab (the `glab` CLI).
    | GitLab
    /// Gitea / Forgejo (the `tea` CLI).
    | Gitea
    /// The remote URL doesn't classify as a known forge (self-hosted, lookalike, or no
    /// remote). Distinct from a forge the CLI is just not authenticated against.
    | Unknown

    /// The forge's short name (`"github"` / `"gitlab"` / `"gitea"` / `"unknown"`).
    member this.AsString =
        match this with
        | ForgeKind.GitHub -> "github"
        | ForgeKind.GitLab -> "gitlab"
        | ForgeKind.Gitea -> "gitea"
        | ForgeKind.Unknown -> "unknown"

    /// Best-effort guess of the forge from a git remote URL's host, for the **public
    /// SaaS** hosts: `github.com` → `GitHub`, `gitlab.com` → `GitLab`, and
    /// `gitea.com`/`codeberg.org` → `Gitea` — each matching the exact host or a proper
    /// subdomain (`*.gitlab.com`), never a lookalike (`gitlab.com.evil.example` → `None`).
    /// Returns `None` for everything else: a **self-hosted** GitLab/Gitea lives on an
    /// arbitrary domain that can't be distinguished, so pick the kind explicitly there.
    static member OfRemoteUrl(url: string) : ForgeKind option =
        match hostOf url with
        | None -> None
        | Some host ->
            let h = host.ToLowerInvariant()

            if hostIs h "github.com" then
                Some ForgeKind.GitHub
            elif hostIs h "gitlab.com" then
                Some ForgeKind.GitLab
            elif hostIs h "gitea.com" || hostIs h "codeberg.org" then
                Some ForgeKind.Gitea
            else
                None

/// A facade operation whose availability varies by backend — i.e. one that can return
/// `Unsupported`. Pass it to `Forge.Supports` to branch *before* calling. Every other
/// facade operation is supported on all three forges.
[<RequireQualifiedAccess>]
type ForgeOp =
    /// `repoView` — current repo/project metadata.
    | RepoView
    /// `prMarkReady` — flip a draft PR to ready.
    | PrMarkReady
    /// `prChecks` — coarse CI status for a PR.
    | PrChecks
    /// `releaseView` — a single release by tag.
    | ReleaseView

    /// Every capability-varying operation — iterate it to build a full support matrix.
    static member All =
        [ ForgeOp.RepoView; ForgeOp.PrMarkReady; ForgeOp.PrChecks; ForgeOp.ReleaseView ]

/// The normalised state of a `ForgePr`, unifying GitHub's `OPEN`/`CLOSED`/`MERGED`,
/// GitLab's `opened`/`closed`/`locked`/`merged`, and Gitea's `open`/`closed`.
[<RequireQualifiedAccess>]
type ForgePrState =
    /// Open / awaiting review.
    | Open
    /// Closed without merging (GitLab's `locked` folds in here too).
    | Closed
    /// Merged.
    | Merged

/// A pull request (GitHub/Gitea) / merge request (GitLab), unified across the three forges.
type ForgePr =
    {
        /// The PR/MR number a caller passes to the other operations (GitHub/Gitea
        /// `number`, GitLab `iid`).
        Number: uint64
        /// Title.
        Title: string
        /// Normalised state (see `ForgePrState`).
        State: ForgePrState
        /// Source (head) branch name.
        SourceBranch: string
        /// Target (base) branch name.
        TargetBranch: string
        /// Web URL.
        Url: string
        /// Whether the PR/MR is a draft. **Best-effort**: only GitLab reports it on the
        /// lean surface; GitHub and Gitea report `false` here.
        Draft: bool
    }

/// A repository (GitHub) / project (GitLab), unified. (Gitea's `tea` has no current-repo
/// view, so `repoView` is `Unsupported` there.)
type ForgeRepo =
    {
        /// Repository / project name.
        Name: string
        /// Owner / namespace (GitHub owner login; GitLab the namespace path).
        Owner: string
        /// Default branch name (empty for an empty repo).
        DefaultBranch: string
        /// Web URL.
        Url: string
        /// Whether the repository is private/non-public. **Conservative when unknown:** an
        /// absent visibility maps to `false` (public) — never told private without proof.
        Private: bool
    }

/// The normalised state of a `ForgeIssue`. An unknown state reads as `Open` — a state we
/// don't model is treated as live, never silently as resolved.
[<RequireQualifiedAccess>]
type ForgeIssueState =
    /// Open / unresolved.
    | Open
    /// Closed.
    | Closed

/// An issue, unified across the three forges.
type ForgeIssue =
    {
        /// The issue number a caller passes to the other operations (GitHub/Gitea
        /// `number`, GitLab `iid`).
        Number: uint64
        /// Title.
        Title: string
        /// Normalised state (see `ForgeIssueState`).
        State: ForgeIssueState
        /// Issue body (markdown).
        Body: string
        /// Web URL.
        Url: string
    }

/// A release, unified across the three forges. (Gitea's `tea` always lists, so
/// `releaseView` is `Unsupported` there.)
type ForgeRelease =
    {
        /// The Git tag the release is attached to (what `releaseView` takes).
        Tag: string
        /// Release title (may be empty — forges commonly default it to the tag).
        Title: string
        /// Web URL. **Best-effort:** empty from GitHub's lean `releaseList`.
        Url: string
        /// Publication timestamp (ISO 8601); `None` for an unpublished draft or when the
        /// backend doesn't report one.
        PublishedAt: string option
        /// Release notes (markdown). `None` when the backend doesn't carry them — always
        /// on Gitea, and on GitHub's lean `releaseList`.
        Body: string option
        /// Whether this is an unpublished draft. **Best-effort:** GitHub/Gitea report it;
        /// GitLab has no draft concept, so it is always `false` there.
        Draft: bool
        /// Whether this is a pre-release. **Best-effort:** GitHub/Gitea report it; GitLab
        /// has no pre-release concept, so it is always `false` there.
        Prerelease: bool
    }

/// The coarse CI status for a PR/MR, bucketed into the four states a caller acts on.
/// (Gitea's `tea` has no checks command, so `prChecks` is `Unsupported` there.)
[<RequireQualifiedAccess>]
type CiStatus =
    /// Everything that ran passed.
    | Passing
    /// At least one check failed or was canceled.
    | Failing
    /// At least one check is still running, and none failed.
    | Pending
    /// No checks/pipeline ran.
    | None

/// Options for `prCreate` — the unified open-a-PR/MR spec, mapped to each CLI's own
/// flags. Build it through `PrCreate.Create` and the chained setters.
type PrCreate =
    {
        /// Title.
        Title: string
        /// Body / description.
        Body: string
        /// Source (head) branch; `None` = the current branch.
        Source: string option
        /// Target (base) branch; `None` = the repository default.
        Target: string option
    }

    /// A PR/MR from the current branch into the repository's default branch.
    static member Create(title: string, body: string) =
        { Title = title
          Body = body
          Source = None
          Target = None }

    /// Open from this source (head) branch instead of the current one.
    member this.WithSource(branch: string) = { this with Source = Some branch }

    /// Open against this target (base) branch instead of the repo default.
    member this.WithTarget(branch: string) = { this with Target = Some branch }

/// How `prMerge` merges — mapped to each CLI's own merge-strategy flag.
[<RequireQualifiedAccess>]
type MergeStrategy =
    /// A merge commit.
    | Merge
    /// Squash the commits into one.
    | Squash
    /// Rebase the source onto the target.
    | Rebase

/// Options for `prEdit` — the unified edit-a-PR/MR spec. At least one of `Title`/`Body`
/// must be `Some` — both-`None` is rejected by the facade before spawning. An empty
/// string is a real value (clears the field), not a `None`.
type PrEdit =
    {
        /// The new title; `None` leaves the title alone.
        Title: string option
        /// The new body / description; `None` leaves the body alone.
        Body: string option
    }

    /// An edit that leaves both fields alone (rejected before spawning). Start with this
    /// and add what to change via `WithTitle`/`WithBody`.
    static member Create() = { Title = None; Body = None }

    /// Set the new title.
    member this.WithTitle(title: string) = { this with Title = Some title }

    /// Set the new body / description.
    member this.WithBody(body: string) = { this with Body = Some body }

/// The flat capability map for a configured forge — what its CLI is honest about doing,
/// intersected with whether the CLI is authenticated. Each `bool` is `true` iff the
/// operation is available on this forge's CLI **and** the CLI reports an authenticated
/// session.
type ForgeCapabilities =
    {
        /// The CLI can open a PR/MR.
        PrCreate: bool
        /// The CLI can post a comment to an existing PR/MR.
        PrComment: bool
        /// The CLI can edit a PR/MR's title and/or body.
        PrEdit: bool
        /// The CLI can report a PR/MR's CI status.
        PrChecks: bool
        /// The CLI can merge a PR/MR.
        PrMerge: bool
        /// The CLI can open an issue.
        IssueCreate: bool
        /// The CLI reports an authenticated session. The other six flags are all `false`
        /// when this is `false`. **Best-effort for GitLab:** `glab auth status` can exit
        /// `0` while unauthenticated (gitlab-org/cli#911), so a `true` means "probably".
        Authed: bool
    }

    /// The all-`false` shape, for the `Unknown` case.
    static member AllFalse =
        { PrCreate = false
          PrComment = false
          PrEdit = false
          PrChecks = false
          PrMerge = false
          IssueCreate = false
          Authed = false }
