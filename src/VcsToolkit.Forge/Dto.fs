namespace VcsToolkit.Forge

open System
open System.Net
open System.Net.Sockets
open VcsToolkit.CliSupport

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
    /// The authority split, userinfo drop, and port strip are the shared `RemoteUrl`
    /// mechanics from CliSupport; the anti-spoofing IPv6-bracket policy below is Forge's own.
    /// For a scheme URL the host is bracket-aware: an IPv6 authority `[::1]:443` yields
    /// `::1`, but only when the bracket content is a *genuine* IPv6 literal — a bracketed
    /// name like `[gitlab.com]` or a colon-bearing fake `[a:b.gitlab.com]` must not be
    /// unwrapped (it could otherwise spoof a trusted host), so it yields `None`.
    let hostOf (url: string) : string option =
        match RemoteUrl.afterScheme url with
        | Some after ->
            // A scheme URL: shared mechanics take the authority to the next `/`/`?`/`#` and
            // drop userinfo, leaving `host[:port]` or an IPv6 `[...]` authority.
            let hostPort = RemoteUrl.authority after

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
                match RemoteUrl.stripPort hostPort with
                | h when h.Length = 0 -> None
                | h -> Some h
        | None ->
            // No scheme: scp-like `user@host:path` or bare `host:path` / `host/path`.
            let afterUser = RemoteUrl.dropUserinfo url

            match afterUser.Split([| ':'; '/' |]).[0] with
            | h when h.Length = 0 -> None
            | h -> Some h

/// Which forge backs a `Forge` handle.
///
/// Treat this as potentially extensible (the Rust model is `#[non_exhaustive]`) — even with the
/// `Unknown` catch-all, add a `| _ ->` arm when pattern-matching so a future forge doesn't break
/// your code.
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
            // ASCII-only lowercase (matches Rust `to_ascii_lowercase`): a full-Unicode fold
            // (`ToLowerInvariant`) could map a non-ASCII character onto an ASCII letter and help
            // complete a spoof of a trusted host, so fold only `A`–`Z` in this security check.
            let h = asciiLower host

            if hostIs h "github.com" then
                Some ForgeKind.GitHub
            elif hostIs h "gitlab.com" then
                Some ForgeKind.GitLab
            elif hostIs h "gitea.com" || hostIs h "codeberg.org" then
                Some ForgeKind.Gitea
            else
                None

/// A facade operation that is *entirely absent* on some backend's CLI — one that can return
/// `Unsupported` no matter its arguments. Pass it to `Forge.Supports` to branch *before*
/// calling. This covers **operation-level** gaps only (chiefly Gitea, whose `tea` lacks these
/// whole commands). A handful of operations exist on every CLI yet refuse a specific *variant*
/// — `prReview` by review kind, `prMerge`'s `Auto`/`DeleteBranch` options, and `prClose`'s
/// delete-branch — and those finer-grained refusals are **not** in `ForgeOp`; query them
/// through `Forge.SupportsReview`/`SupportsMergeOptions`/`SupportsCloseDeleteBranch` instead.
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
    /// `prDiff` — a PR/MR's unified diff. **`Unsupported` on Gitea** (`tea` has no diff
    /// command); supported on GitHub/GitLab.
    | PrDiff
    /// `issueReopen` — reopen a closed issue. **`Unsupported` on Gitea** (`tea` 0.9.2 has no
    /// issue reopen command).
    | IssueReopen
    /// `releaseDelete` — delete a release by tag. **`Unsupported` on Gitea** (`tea` 0.9.2 has
    /// no release delete command).
    | ReleaseDelete

    /// Every capability-varying operation — iterate it to build a full support matrix.
    static member All =
        [ ForgeOp.RepoView
          ForgeOp.PrMarkReady
          ForgeOp.PrChecks
          ForgeOp.ReleaseView
          ForgeOp.PrDiff
          ForgeOp.IssueReopen
          ForgeOp.ReleaseDelete ]

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

/// Which PR/MR states `prList` returns — the unified filter, mapped to each CLI's own state
/// flag(s) in the corresponding backend adapter: `gh pr list --state` and `glab mr list
/// [--closed|--merged|--all]` both support every value directly. **On Gitea every state is
/// `Unsupported`**: `tea`'s `pr list --output json` does not work against the real CLI at
/// all (K-049 — the `--output json` flag itself is rejected, regardless of `--state`), so
/// there is no working listing path to filter in the first place (see `GiteaForge.prList`).
[<RequireQualifiedAccess>]
type PrListState =
    /// Open / awaiting review (the default).
    | Open
    /// Closed without merging.
    | Closed
    /// Merged.
    | Merged
    /// Every state.
    | All

/// Options for `prList` — the unified state + result-count filter, mapped to each CLI's own
/// flags (see `PrListState`). Build it through the state constructors
/// (`PrListOptions.Open`/`Closed`/`Merged`/`All`), then optionally `WithLimit`.
type PrListOptions =
    {
        /// Which states to include (see `PrListState`).
        State: PrListState
        /// Maximum number of results.
        Limit: int
    }

    /// Open PRs/MRs, up to 100 — the facade's behaviour before `PrListOptions` existed
    /// (`Forge.PrList()` with no argument still defaults to this).
    static member Default =
        { State = PrListState.Open
          Limit = 100 }

    /// Open PRs/MRs, up to 100.
    static member Open =
        { PrListOptions.Default with
            State = PrListState.Open }

    /// Closed (not merged) PRs/MRs, up to 100.
    static member Closed =
        { PrListOptions.Default with
            State = PrListState.Closed }

    /// Merged PRs/MRs, up to 100.
    static member Merged =
        { PrListOptions.Default with
            State = PrListState.Merged }

    /// Every PR/MR regardless of state, up to 100.
    static member All =
        { PrListOptions.Default with
            State = PrListState.All }

    /// Filter by `state` instead of the default `Open`.
    member this.WithState(state: PrListState) = { this with State = state }

    /// Cap the result count at `limit` instead of the default 100.
    member this.WithLimit(limit: int) = { this with Limit = limit }

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
        /// Whether the PR/MR is a draft, or `None` when the backend doesn't report it on
        /// its lean surface. Only GitLab carries `draft` there (`Some`); GitHub and Gitea
        /// don't expose it, so it is `None` — never conflated with a confirmed `Some false`.
        Draft: bool option
        /// Labels attached to the PR/MR, or `None` when the backend can't report them.
        /// GitHub and GitLab report `Some` — an empty `Some []` is a *confirmed* "no
        /// labels"; Gitea is always `None` (`tea`'s PR list/view has no labels column, so
        /// an empty list there would be a false "no labels" rather than the honest
        /// "unknown"). Mirrors the `None`/`Some []` contract of `Draft`.
        Labels: string list option
        /// Usernames/logins of assigned users, or `None` when the backend can't report
        /// them. GitHub (`gh --json assignees` → `login`) and GitLab (`assignees` →
        /// `username`) report `Some` — an empty `Some []` is a *confirmed* "unassigned";
        /// Gitea is always `None` (`tea`'s PR list/view has no assignees column).
        Assignees: string list option
        /// Author login/username, or `None` when the backend can't report it. GitHub
        /// (`author.login`) and GitLab (`author.username`) report `Some` — including `Some ""`
        /// for a *confirmed* deleted/anonymised account, which is a fact, never conflated with
        /// `None`. Gitea is always `None` (`tea`'s csv PR surface has no author column — K-049).
        Author: string option
        /// Creation timestamp (RFC 3339), or `None` when the backend can't report it.
        /// GitHub/GitLab report `Some`; Gitea is always `None` (no timestamp column).
        CreatedAt: string option
        /// Last-update timestamp (RFC 3339), or `None` when the backend can't report it.
        /// GitHub/GitLab report `Some`; Gitea is always `None` (no timestamp column).
        UpdatedAt: string option
        /// Milestone title, or `None` when there is no milestone or the backend can't report
        /// one. GitHub/GitLab map an unset milestone (a `null`) to `None` and a set one to
        /// `Some title`; Gitea is always `None` (no milestone column).
        Milestone: string option
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
        /// Whether the repository is private/non-public, or `None` when the backend
        /// doesn't report visibility. `Some true`/`Some false` is a confirmed verdict; an
        /// absent visibility is `None` (unknown) — never conflated with a public repo.
        Private: bool option
    }

/// The normalised state of a `ForgeIssue`. An unknown state reads as `Open` — a state we
/// don't model is treated as live, never silently as resolved.
[<RequireQualifiedAccess>]
type ForgeIssueState =
    /// Open / unresolved.
    | Open
    /// Closed.
    | Closed

/// Which issue states `issueList` returns — the unified filter (see `PrListState`, the
/// PR/MR counterpart). Issues have no "merged" state, so only three values. **On Gitea every
/// state is `Unsupported`**, for the identical K-049 reason as `PrListState` (see
/// `GiteaForge.issueList`).
[<RequireQualifiedAccess>]
type IssueListState =
    /// Open / unresolved (the default).
    | Open
    /// Closed.
    | Closed
    /// Every state.
    | All

/// Options for `issueList` — the unified state + result-count filter (see `PrListOptions`,
/// the PR/MR counterpart). Build it through the state constructors
/// (`IssueListOptions.Open`/`Closed`/`All`), then optionally `WithLimit`.
type IssueListOptions =
    {
        /// Which states to include (see `IssueListState`).
        State: IssueListState
        /// Maximum number of results.
        Limit: int
    }

    /// Open issues, up to 100 — the facade's behaviour before `IssueListOptions` existed
    /// (`Forge.IssueList()` with no argument still defaults to this).
    static member Default =
        { State = IssueListState.Open
          Limit = 100 }

    /// Open issues, up to 100.
    static member Open =
        { IssueListOptions.Default with
            State = IssueListState.Open }

    /// Closed issues, up to 100.
    static member Closed =
        { IssueListOptions.Default with
            State = IssueListState.Closed }

    /// Every issue regardless of state, up to 100.
    static member All =
        { IssueListOptions.Default with
            State = IssueListState.All }

    /// Filter by `state` instead of the default `Open`.
    member this.WithState(state: IssueListState) = { this with State = state }

    /// Cap the result count at `limit` instead of the default 100.
    member this.WithLimit(limit: int) = { this with Limit = limit }

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
        /// Labels attached to the issue, or `None` when the backend can't report them.
        /// GitHub and GitLab report `Some` (an empty `Some []` is a *confirmed* "no
        /// labels"); Gitea is always `None` — `tea`'s issue list/view has no labels column,
        /// so an empty list there would be a false "no labels" rather than "unknown".
        Labels: string list option
        /// Usernames/logins of assigned users, or `None` when the backend can't report
        /// them. GitHub and GitLab report `Some` (an empty `Some []` is a *confirmed*
        /// "unassigned"); Gitea is always `None` — `tea` has no assignees column.
        Assignees: string list option
        /// Author login/username, or `None` when the backend can't report it. GitHub
        /// (`author.login`) and GitLab (`author.username`) report `Some` — including `Some ""`
        /// for a *confirmed* deleted/anonymised account, which is a fact, never conflated with
        /// `None`. Gitea is always `None` (`tea`'s csv issue surface has no author column — K-049).
        Author: string option
        /// Creation timestamp (RFC 3339), or `None` when the backend can't report it.
        /// GitHub/GitLab report `Some`; Gitea is always `None` (no timestamp column).
        CreatedAt: string option
        /// Last-update timestamp (RFC 3339), or `None` when the backend can't report it.
        /// GitHub/GitLab report `Some`; Gitea is always `None` (no timestamp column).
        UpdatedAt: string option
        /// Milestone title, or `None` when there is no milestone or the backend can't report
        /// one. GitHub/GitLab map an unset milestone (a `null`) to `None` and a set one to
        /// `Some title`; Gitea is always `None` (no milestone column).
        Milestone: string option
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
        /// Whether this is an unpublished draft, or `None` when the backend has no draft
        /// concept. GitHub/Gitea report it (`Some`); GitLab has no release draft, so `None`.
        Draft: bool option
        /// Whether this is a pre-release, or `None` when the backend has no pre-release
        /// concept. GitHub/Gitea report it (`Some`); GitLab has none, so `None`.
        Prerelease: bool option
        /// Release author login/username, or `None` when the backend can't report it.
        /// **Best-effort on GitHub:** `None` from the lean `releaseList` (which doesn't fetch
        /// the author, like `Url`/`Body`), `Some` from `releaseView`. GitLab carries the author
        /// on both list and view (`Some`). Gitea is always `None` — `tea`'s release csv has no
        /// author column (K-049).
        Author: string option
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

/// Options for `prMerge` — the unified merge-a-PR/MR spec, mapped to each CLI's own merge
/// flags. Build it through the strategy constructors `PrMerge.Merge`/`Squash`/`Rebase`, then
/// the chained `WithAuto`/`WithDeleteBranch` setters (modelled on GitHub's own `PrMerge`).
/// `Auto`/`DeleteBranch` map to real `gh` flags on GitHub; GitLab and Gitea expose no
/// confirmed equivalent, so a spec that asks for either is refused with `Unsupported` before
/// any spawn there rather than silently dropping the option.
type PrMerge =
    {
        /// The merge strategy (see `MergeStrategy`).
        Strategy: MergeStrategy
        /// Enable auto-merge — merge once requirements are met (GitHub `--auto`).
        /// **`Unsupported` on GitLab/Gitea.**
        Auto: bool
        /// Delete the source branch after the merge (GitHub `--delete-branch`).
        /// **`Unsupported` on GitLab/Gitea.**
        DeleteBranch: bool
    }

    /// Merge with a merge commit.
    static member Merge =
        { Strategy = MergeStrategy.Merge
          Auto = false
          DeleteBranch = false }

    /// Squash the commits into one.
    static member Squash =
        { Strategy = MergeStrategy.Squash
          Auto = false
          DeleteBranch = false }

    /// Rebase the source onto the target.
    static member Rebase =
        { Strategy = MergeStrategy.Rebase
          Auto = false
          DeleteBranch = false }

    /// Merge automatically once requirements are met (GitHub `--auto`).
    member this.WithAuto() = { this with Auto = true }

    /// Delete the source branch after merging (GitHub `--delete-branch`).
    member this.WithDeleteBranch() = { this with DeleteBranch = true }

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

/// Options for `releaseCreate` — the unified create-a-release spec, mapped to each CLI's own
/// flags. Build it through `ReleaseCreate.Create` (the tag) and the chained setters.
/// `Draft`/`Prerelease` map to real flags on GitHub (`gh --draft`/`--prerelease`) and Gitea
/// (`tea --draft`/`--prerelease`); GitLab's `glab` has no release draft/pre-release concept
/// (mirroring `ForgeRelease.Draft`/`Prerelease` being `None` on GitLab), so a spec asking for
/// either is refused with `Unsupported` before any spawn there rather than silently dropping
/// the option. A plain release (tag + optional title/notes) works on all three.
type ReleaseCreate =
    {
        /// The Git tag the release is attached to (a bare positional on `gh`/`glab`; a
        /// `--tag` flag value on `tea`).
        Tag: string
        /// Release title; `None` lets the forge default it (commonly to the tag).
        Title: string option
        /// Release notes / description (markdown); `None` = no notes.
        Notes: string option
        /// Create as an unpublished draft (GitHub/Gitea). **`Unsupported` on GitLab.**
        Draft: bool
        /// Mark as a pre-release (GitHub/Gitea). **`Unsupported` on GitLab.**
        Prerelease: bool
    }

    /// A published release on `tag`, titled and annotated by the forge's defaults.
    static member Create(tag: string) =
        { Tag = tag
          Title = None
          Notes = None
          Draft = false
          Prerelease = false }

    /// Set the release title instead of the forge's default.
    member this.WithTitle(title: string) = { this with Title = Some title }

    /// Set the release notes / description.
    member this.WithNotes(notes: string) = { this with Notes = Some notes }

    /// Create as an unpublished draft (GitHub/Gitea). **`Unsupported` on GitLab.**
    member this.WithDraft() = { this with Draft = true }

    /// Mark as a pre-release (GitHub/Gitea). **`Unsupported` on GitLab.**
    member this.WithPrerelease() = { this with Prerelease = true }

/// Which kind of review `prReview` submits — the unified counterpart of each CLI's own
/// review verb. Support varies by kind, not by operation: `Approve` maps to a real verb on
/// all three forges; `RequestChanges` only on GitHub/Gitea; `Comment` only on GitHub (see
/// `Forge.PrReview`).
///
/// A per-level type (like `MergeStrategy`), converted in each adapter, rather than one type
/// shared across the layers — mirroring `VcsToolkit.GitHub.ReviewKind`.
[<RequireQualifiedAccess>]
type ReviewKind =
    /// Approve the PR/MR.
    | Approve
    /// Request changes.
    | RequestChanges
    /// A comment-only review.
    | Comment

/// What `prReview` submits (see `Forge.PrReview`), unified across the three forges. The
/// constructor is private so the invariant holds by construction — request-changes / comment
/// reviews *require* a body, so they are only reachable through `RequestChanges` / `Comment`
/// (which both take it); an empty-body request-changes is unrepresentable. Approve's body is
/// optional (`Approve` starts with none; attach one with `WithBody`). Mirrors the invariant
/// of `VcsToolkit.GitHub.ReviewAction`.
[<Sealed>]
type ReviewAction private (kind: ReviewKind, body: string option) =
    /// Which kind of review this is.
    member _.Kind = kind
    /// The review body, if any.
    member _.Body = body

    /// Approve, with no body. Attach one with `WithBody`.
    static member Approve = ReviewAction(ReviewKind.Approve, None)

    /// Request changes; the body is required.
    static member RequestChanges(body: string) =
        ReviewAction(ReviewKind.RequestChanges, Some body)

    /// A comment-only review; the body is required.
    static member Comment(body: string) =
        ReviewAction(ReviewKind.Comment, Some body)

    /// Attach or replace the body — mainly to give an `Approve` a message.
    member _.WithBody(body: string) = ReviewAction(kind, Some body)

/// The flat capability map for a configured forge — what its CLI is honest about doing,
/// intersected with whether the CLI is authenticated. Each `bool` is `true` iff the
/// operation is available on this forge's CLI **and** the CLI reports an authenticated
/// session.
///
/// Deliberately not a 1:1 mirror of `ForgeOp`: this map predates `RepoView`/`PrMarkReady`/
/// `ReleaseView`/`PrDiff` joining `ForgeOp`, and those (all read-only, capability-varying
/// only on Gitea) are queried through `Forge.Supports` instead — adding a flag here for
/// each would duplicate that support matrix without a behavioural difference. `PrChecks`
/// keeps its flag here for backward compatibility with existing consumers.
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
        /// The CLI can reopen a closed issue.
        IssueReopen: bool
        /// The CLI can delete a release by tag.
        ReleaseDelete: bool
        /// The CLI reports an authenticated session. The other six flags are all `false`
        /// when this is `false`. **Best-effort for GitLab:** `glab auth status` can exit
        /// `0` while unauthenticated (gitlab-org/cli#911), so a `true` means "probably".
        Authed: bool
        /// The detected forge CLI version, or `None` when it wasn't probed (the `Unknown`
        /// handle) or the `--version` banner didn't parse. Reported independently of
        /// `Authed` — it describes the installed binary, not the session.
        Version: VcsToolkit.Diff.Version option
        /// Which forge CLI this map describes (`Unknown` for the CLI-less handle). Mirrors
        /// the handle's `Forge.Kind`, so a `ForgeCapabilities` value is self-describing.
        Kind: ForgeKind
    }

    /// The all-`false` shape, for the `Unknown` case (no CLI, no version).
    static member AllFalse =
        { PrCreate = false
          PrComment = false
          PrEdit = false
          PrChecks = false
          PrMerge = false
          IssueCreate = false
          IssueReopen = false
          ReleaseDelete = false
          Authed = false
          Version = None
          Kind = ForgeKind.Unknown }
