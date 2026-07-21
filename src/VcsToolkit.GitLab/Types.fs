namespace VcsToolkit.GitLab

open ProcessKit
open VcsToolkit.Diff

/// Toolkit-wide constants for the GitLab wrapper.
[<AutoOpen>]
module internal Constants =

    /// Name of the underlying CLI binary this crate drives.
    ///
    /// Injection-safety note: most of the surface keeps caller values out of bare
    /// positional slots — MR/issue ids are `uint64`, and title/body/branch arguments
    /// ride in flag-value positions (`--title <t>`, `--source-branch <b>`) where glab
    /// consumes the next token verbatim. The one exception is `ReleaseView`'s bare
    /// `<tag>`, guarded with `rejectFlagLike`; guard any future bare positional likewise.
    [<Literal>]
    let BINARY = "glab"

    /// The oldest `glab` this wrapper's typed `--output json`/flag surface is written
    /// against (the modern glab 1.x line). Version-gated operations refuse a CLI below
    /// this floor up front rather than driving it into a raw failure.
    let MIN_SUPPORTED_VERSION: Version =
        { Major = 1UL
          Minor = 0UL
          Patch = 0UL }

/// How `mrMerge` merges the MR. GitLab's default is a merge commit; `Squash`/`Rebase`
/// add the corresponding flag.
[<RequireQualifiedAccess>]
type MergeStrategy =
    /// A merge commit (glab's default — no extra flag).
    | Merge
    /// Squash the commits into one (`--squash`).
    | Squash
    /// Rebase the source onto the target (`--rebase`).
    | Rebase

    /// The glab flag this strategy emits, or `None` for the default merge commit.
    member internal this.Flag =
        match this with
        | MergeStrategy.Merge -> None
        | MergeStrategy.Squash -> Some "--squash"
        | MergeStrategy.Rebase -> Some "--rebase"

/// Which MR states `mrList` returns. `glab` has no `--state` flag — it selects state through
/// mutually exclusive boolean flags, open being the implicit default when none are passed.
[<RequireQualifiedAccess>]
type MrListState =
    /// Open MRs (glab's default — no extra flag).
    | Open
    /// Closed (not merged) MRs (`--closed`).
    | Closed
    /// Merged MRs (`--merged`).
    | Merged
    /// Every MR regardless of state (`--all`).
    | All

    /// The glab flag this case emits, or `[]` for the default (open).
    member internal this.Flags =
        match this with
        | MrListState.Open -> []
        | MrListState.Closed -> [ "--closed" ]
        | MrListState.Merged -> [ "--merged" ]
        | MrListState.All -> [ "--all" ]

/// Options for `mrList` (`glab mr list [--closed|--merged|--all] --per-page <limit>`).
/// Defaults reproduce this wrapper's previous, options-less behaviour: open MRs, up to 100.
type MrListOptions =
    {
        /// Which states to include (see `MrListState`).
        State: MrListState
        /// `--per-page` — the maximum number of results.
        Limit: int
    }

    /// Open MRs, up to 100 — this wrapper's previous behaviour before `MrListOptions` existed.
    static member Default =
        { State = MrListState.Open
          Limit = 100 }

    /// Filter by `state` instead of the default `Open`.
    member this.WithState(state: MrListState) = { this with State = state }

    /// Cap the result count at `limit` instead of the default 100.
    member this.WithLimit(limit: int) = { this with Limit = limit }

/// Which issue states `issueList` returns. Issues have no "merged" state, and `glab` again
/// selects state through boolean flags rather than `--state` (see `MrListState`).
[<RequireQualifiedAccess>]
type IssueListState =
    /// Open issues (glab's default — no extra flag).
    | Open
    /// Closed issues (`--closed`).
    | Closed
    /// Every issue regardless of state (`--all`).
    | All

    /// The glab flag this case emits, or `[]` for the default (open).
    member internal this.Flags =
        match this with
        | IssueListState.Open -> []
        | IssueListState.Closed -> [ "--closed" ]
        | IssueListState.All -> [ "--all" ]

/// Options for `issueList` (`glab issue list [--closed|--all] --per-page <limit>`). Defaults
/// reproduce this wrapper's previous, options-less behaviour: open issues, up to 100.
type IssueListOptions =
    {
        /// Which states to include (see `IssueListState`).
        State: IssueListState
        /// `--per-page` — the maximum number of results.
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

/// Options for `mrCreate` (`glab mr create`). Build it through `MrCreate.Create`
/// (title + body) and the chained `WithSource`/`WithTarget` setters.
type MrCreate =
    {
        /// The MR title (`--title`).
        Title: string
        /// The MR description (`--description`).
        Body: string
        /// The source branch (`--source-branch`); `None` = the current branch.
        Source: string option
        /// The target branch (`--target-branch`); `None` = the project default.
        Target: string option
    }

    /// An MR with `title` and `body`, source/target left to glab's defaults
    /// (current branch → project default).
    static member Create(title: string, body: string) =
        { Title = title
          Body = body
          Source = None
          Target = None }

    /// Set the source branch (`--source-branch`) instead of the current branch.
    member this.WithSource(source: string) = { this with Source = Some source }

    /// Set the target branch (`--target-branch`) instead of the project default.
    member this.WithTarget(target: string) = { this with Target = Some target }

/// Options for `mrEdit` (`glab mr update`). At least one of `Title`/`Body` must be
/// `Some` — `mrEdit` rejects both-`None` before spawning (an explicit error, not a
/// silent no-op). An empty string is a real value (glab clears the field on
/// `--title ""`), not a `None`.
type MrEdit =
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

/// Options for `releaseCreate` (`glab release create`). Build it through
/// `ReleaseCreate.Create` (the tag) and the chained setters. `glab` has no release
/// draft/pre-release concept, so — unlike the GitHub/Gitea specs — this carries no such
/// options (the `Forge` facade refuses a draft/pre-release request on GitLab before any
/// spawn).
type ReleaseCreate =
    {
        /// The Git tag the release is attached to — a bare positional, rejected if empty
        /// or `-`-leading before spawning.
        Tag: string
        /// The release title (`--name`); `None` lets glab default it.
        Title: string option
        /// The release notes / description (`--notes`); `None` omits it.
        Notes: string option
    }

    /// A release on `tag`, name/notes left to glab's defaults.
    static member Create(tag: string) =
        { Tag = tag
          Title = None
          Notes = None }

    /// Set the release title (`--name`).
    member this.WithTitle(title: string) = { this with Title = Some title }

    /// Set the release notes / description (`--notes`).
    member this.WithNotes(notes: string) = { this with Notes = Some notes }

/// What the installed `glab` binary supports, probed via `GitLab.Capabilities`.
type GitLabCapabilities =
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
                    sprintf "VcsToolkit.GitLab requires glab >= %O, found %O" MIN_SUPPORTED_VERSION this.Version
                )
            )

    /// The minimum `glab` version this wrapper supports.
    static member MinimumSupported: Version = MIN_SUPPORTED_VERSION
