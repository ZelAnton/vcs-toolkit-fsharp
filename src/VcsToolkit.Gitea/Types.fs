namespace VcsToolkit.Gitea

open ProcessKit
open VcsToolkit.Diff

/// Toolkit-wide constants for the Gitea wrapper.
[<AutoOpen>]
module internal Constants =

    /// Name of the underlying CLI binary this crate drives (also drives Forgejo).
    ///
    /// Injection-safety note: most of the lean surface keeps caller values out of bare
    /// positional slots — PR numbers are `uint64`, and title/body/branch arguments ride
    /// in flag-value positions. The one exception is `PrComment`'s body: `tea comment
    /// <n> <body>` takes it as a bare positional, so it is guarded with `rejectFlagLike`.
    [<Literal>]
    let BINARY = "tea"

    /// The oldest `tea` this wrapper's typed `--output json`/`--fields` surface is written
    /// against (the 0.9.x line where the print-table JSON output stabilised). Version-gated
    /// operations refuse a CLI below this floor up front rather than driving it into a raw
    /// failure.
    let MIN_SUPPORTED_VERSION: Version =
        { Major = 0UL
          Minor = 9UL
          Patch = 0UL }

    /// `--fields` column set for `tea pr list` (every value comes back as a JSON string).
    [<Literal>]
    let PR_FIELDS = "index,title,state,head,base,url"

    /// `--fields` column set for `tea issues list`.
    [<Literal>]
    let ISSUE_FIELDS = "index,title,state,body,url"

    /// `tea` has no single-PR view, so `PrView` lists all states and pages through, filtering
    /// by number. The Gitea server caps each API page at `MAX_RESPONSE_ITEMS` (default 50) and
    /// `tea` makes one call per page, so a single large `--limit` is silently clamped — hence
    /// paging. `PR_VIEW_PAGE_SIZE` is the requested per-page size (an empty page ends the walk
    /// regardless of the server's actual clamp); `PR_VIEW_MAX_PAGES` bounds the walk.
    [<Literal>]
    let PR_VIEW_PAGE_SIZE = 50

    /// Safety bound on the `PrView` page walk (see `PR_VIEW_PAGE_SIZE`).
    [<Literal>]
    let PR_VIEW_MAX_PAGES = 200

/// How `prMerge` merges the PR — maps to `tea pr merge --style` (Gitea's default is a
/// merge commit).
[<RequireQualifiedAccess>]
type MergeStrategy =
    /// A merge commit (`--style merge`).
    | Merge
    /// Squash the commits into one (`--style squash`).
    | Squash
    /// Rebase the source onto the target (`--style rebase`).
    | Rebase

    /// The `tea pr merge --style` value for this strategy.
    member internal this.Style =
        match this with
        | MergeStrategy.Merge -> "merge"
        | MergeStrategy.Squash -> "squash"
        | MergeStrategy.Rebase -> "rebase"

/// Which PR states `prList` returns (`tea pr list --state`). `tea`'s `--state` filter takes
/// `open`/`closed`/`all`; whether its `closed` bucket reliably includes a merged PR is
/// unconfirmed against the real CLI (`PrView`, above, deliberately does not rely on it —
/// it walks `--state all` instead), so `Closed` here is a literal `--state closed` pass-through
/// for a caller who wants exactly that CLI behaviour, not a claim about merged-PR coverage.
[<RequireQualifiedAccess>]
type PrListState =
    /// Open PRs (`--state open`, tea's default).
    | Open
    /// `--state closed` verbatim — see the type doc comment for the merged-PR caveat.
    | Closed
    /// Every PR regardless of state (`--state all`).
    | All

    /// The `--state` value this case emits.
    member internal this.Flag =
        match this with
        | PrListState.Open -> "open"
        | PrListState.Closed -> "closed"
        | PrListState.All -> "all"

/// Options for `prList` (`tea pr list --state <state> --limit <limit>`). Defaults reproduce
/// this wrapper's previous, options-less behaviour: open PRs, up to 100.
type PrListOptions =
    {
        /// Which states to include (see `PrListState`).
        State: PrListState
        /// `--limit` — the maximum number of results.
        Limit: int
    }

    /// Open PRs, up to 100 — this wrapper's previous behaviour before `PrListOptions` existed.
    static member Default =
        { State = PrListState.Open
          Limit = 100 }

    /// Filter by `state` instead of the default `Open`.
    member this.WithState(state: PrListState) = { this with State = state }

    /// Cap the result count at `limit` instead of the default 100.
    member this.WithLimit(limit: int) = { this with Limit = limit }

/// Which issue states `issueList` returns (`tea issues list --state`).
[<RequireQualifiedAccess>]
type IssueListState =
    /// Open issues (`--state open`, tea's default).
    | Open
    /// Closed issues (`--state closed`).
    | Closed
    /// Every issue regardless of state (`--state all`).
    | All

    /// The `--state` value this case emits.
    member internal this.Flag =
        match this with
        | IssueListState.Open -> "open"
        | IssueListState.Closed -> "closed"
        | IssueListState.All -> "all"

/// Options for `issueList` (`tea issues list --state <state> --limit <limit>`). Defaults
/// reproduce this wrapper's previous, options-less behaviour: open issues, up to 100.
type IssueListOptions =
    {
        /// Which states to include (see `IssueListState`).
        State: IssueListState
        /// `--limit` — the maximum number of results.
        Limit: int
    }

    /// Open issues, up to 100 — this wrapper's previous behaviour before `IssueListOptions`
    /// existed.
    static member Default =
        { State = IssueListState.Open
          Limit = 100 }

    /// Filter by `state` instead of the default `Open`.
    member this.WithState(state: IssueListState) = { this with State = state }

    /// Cap the result count at `limit` instead of the default 100.
    member this.WithLimit(limit: int) = { this with Limit = limit }

/// Options for `prCreate` (`tea pr create`). Build it through `PrCreate.Create`
/// (title + body) and the chained `WithHead`/`WithBase` setters.
type PrCreate =
    {
        /// The PR title (`--title`).
        Title: string
        /// The PR description (`--description`).
        Body: string
        /// The source branch (`--head`); `None` = the current branch.
        Head: string option
        /// The target branch (`--base`); `None` = the repo default.
        Base: string option
    }

    /// A PR with `title` and `body`, source/target left to tea's defaults
    /// (current branch → repo default).
    static member Create(title: string, body: string) =
        { Title = title
          Body = body
          Head = None
          Base = None }

    /// Set the source branch (`--head`) instead of the current branch.
    member this.WithHead(head: string) = { this with Head = Some head }

    /// Set the target branch (`--base`) instead of the repo default.
    member this.WithBase(baseBranch: string) = { this with Base = Some baseBranch }

/// Options for `prEdit` (`tea pr edit`). At least one of `Title`/`Body` must be `Some`
/// — `prEdit` rejects both-`None` before spawning (an explicit error, not a silent
/// no-op). An empty string is a real value (tea clears the field), not a `None`.
type PrEdit =
    {
        /// The new title (`--title`); `None` leaves the title alone.
        Title: string option
        /// The new description (`--description`); `None` leaves the description alone.
        Body: string option
    }

    /// An edit that leaves both fields alone (rejected before spawning). Start with
    /// this and add what to change via `WithTitle`/`WithBody`.
    static member Create() = { Title = None; Body = None }

    /// Set the new title (`--title`).
    member this.WithTitle(title: string) = { this with Title = Some title }

    /// Set the new description (`--description`).
    member this.WithBody(body: string) = { this with Body = Some body }

/// What the installed `tea` binary supports, probed via `Gitea.Capabilities`.
type GiteaCapabilities =
    {
        /// The binary's parsed version.
        Version: Version
    }

    /// Whether the binary meets the supported floor (see `MIN_SUPPORTED_VERSION`).
    member this.IsSupported = this.Version >= MIN_SUPPORTED_VERSION

    /// Error unless `IsSupported` — a structural refusal carrying the found-vs-required
    /// versions, not a raw CLI failure.
    member this.EnsureSupported() : Result<unit, ProcessError> =
        if this.IsSupported then
            Ok()
        else
            Error(
                ProcessError.Spawn(
                    BINARY,
                    sprintf "VcsToolkit.Gitea requires tea >= %O, found %O" MIN_SUPPORTED_VERSION this.Version
                )
            )

    /// The minimum `tea` version this wrapper supports.
    static member MinimumSupported: Version = MIN_SUPPORTED_VERSION
