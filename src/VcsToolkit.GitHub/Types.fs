namespace VcsToolkit.GitHub

open ProcessKit
open VcsToolkit.CliSupport
open VcsToolkit.Diff

/// Toolkit-wide constants for the GitHub wrapper.
[<AutoOpen>]
module internal Constants =

    /// Name of the underlying CLI binary this crate drives.
    [<Literal>]
    let BINARY = "gh"

    /// The oldest `gh` this wrapper's typed `--json`/flag surface is written against
    /// (`--json` field selection is a gh 2.0 feature). Version-gated operations refuse a
    /// CLI below this floor up front rather than driving it into a raw failure.
    let MIN_SUPPORTED_VERSION: Version =
        { Major = 2UL
          Minor = 0UL
          Patch = 0UL }

    /// `--json` field set for a pull request (`pr list`/`pr view`).
    [<Literal>]
    let PR_FIELDS = "number,title,state,headRefName,baseRefName,url"

    /// `--json` field set for `repo view`.
    [<Literal>]
    let REPO_FIELDS = "name,owner,description,url,isPrivate,defaultBranchRef"

    /// `--json` field set for `issue list`.
    [<Literal>]
    let ISSUE_LIST_FIELDS = "number,title,state,body,url"

    /// `--json` field set for `issue view`.
    [<Literal>]
    let ISSUE_VIEW_FIELDS = "number,title,state,body,url"

    /// `--json` field set for a workflow run (`run list`/`run view`).
    [<Literal>]
    let RUN_FIELDS =
        "databaseId,name,displayTitle,status,conclusion,workflowName,headBranch,event,url,createdAt"

    /// `--json` field set for `pr checks`.
    [<Literal>]
    let CHECK_FIELDS = "name,state,bucket,workflow,link,startedAt,completedAt"

    /// `--json` field set for `release list`.
    [<Literal>]
    let RELEASE_LIST_FIELDS = "tagName,name,isLatest,isDraft,isPrerelease,publishedAt"

    /// `--json` field set for `release view`.
    [<Literal>]
    let RELEASE_VIEW_FIELDS = "tagName,name,body,url,publishedAt,isDraft,isPrerelease"

/// How `prMerge` merges the PR — exactly one of gh's mutually exclusive strategy flags.
[<RequireQualifiedAccess>]
type MergeStrategy =
    /// A merge commit (`--merge`).
    | Merge
    /// Squash into one commit (`--squash`).
    | Squash
    /// Rebase the commits onto the base (`--rebase`).
    | Rebase

    /// The gh flag this strategy emits.
    member internal this.Flag =
        match this with
        | MergeStrategy.Merge -> "--merge"
        | MergeStrategy.Squash -> "--squash"
        | MergeStrategy.Rebase -> "--rebase"

/// Options for `prMerge` (`gh pr merge`). Build it through the strategy
/// constructors `PrMerge.Merge`/`Squash`/`Rebase`, then `WithAuto`/`WithDeleteBranch`.
type PrMerge =
    {
        /// The merge strategy (exactly one of gh's `--merge`/`--squash`/`--rebase`).
        Strategy: MergeStrategy
        /// Enable auto-merge: merge once requirements are met (`--auto`).
        Auto: bool
        /// Delete the head branch after the merge (`--delete-branch`).
        DeleteBranch: bool
    }

    /// Merge with a merge commit (`gh pr merge --merge`).
    static member Merge =
        { Strategy = MergeStrategy.Merge
          Auto = false
          DeleteBranch = false }

    /// Squash-merge (`gh pr merge --squash`).
    static member Squash =
        { Strategy = MergeStrategy.Squash
          Auto = false
          DeleteBranch = false }

    /// Rebase-merge (`gh pr merge --rebase`).
    static member Rebase =
        { Strategy = MergeStrategy.Rebase
          Auto = false
          DeleteBranch = false }

    /// Merge automatically once requirements are met (`--auto`).
    member this.WithAuto() = { this with Auto = true }

    /// Delete the head branch after merging (`--delete-branch`).
    member this.WithDeleteBranch() = { this with DeleteBranch = true }

/// Options for `prCreate` (`gh pr create`). Build it through `PrCreate.Create`
/// (title + body) and the chained `WithHead`/`WithBase` setters.
type PrCreate =
    {
        /// The PR title (`--title`).
        Title: string
        /// The PR body (`--body`).
        Body: string
        /// The source branch (`--head`); `None` = the current branch.
        Head: string option
        /// The target branch (`--base`); `None` = the repo default.
        Base: string option
    }

    /// A PR with the given title and body, opened from the current branch into the
    /// repo default (`gh pr create --title <title> --body <body>`).
    static member Create(title: string, body: string) =
        { Title = title
          Body = body
          Head = None
          Base = None }

    /// Set the source branch (`--head`).
    member this.WithHead(head: string) = { this with Head = Some head }

    /// Set the target branch (`--base`).
    member this.WithBase(baseBranch: string) = { this with Base = Some baseBranch }

/// Options for `prEdit` (`gh pr edit`). At least one of `Title`/`Body` must be
/// `Some` — `prEdit` rejects both-`None` before spawning (an explicit error, not a
/// silent no-op). An empty string is a real value (gh clears the field on
/// `--title ""`), not a `None`.
type PrEdit =
    {
        /// The new title (`--title`); `None` leaves the title alone.
        Title: string option
        /// The new body (`--body`); `None` leaves the body alone.
        Body: string option
    }

    /// An edit that leaves both fields alone (rejected before spawning). Start with
    /// this and add what to change via `WithTitle`/`WithBody`.
    static member Create() = { Title = None; Body = None }

    /// Set the new title (`--title`).
    member this.WithTitle(title: string) = { this with Title = Some title }

    /// Set the new body (`--body`).
    member this.WithBody(body: string) = { this with Body = Some body }

/// Which kind of review `prReview` submits.
[<RequireQualifiedAccess>]
type ReviewKind =
    /// Approve (`--approve`).
    | Approve
    /// Request changes (`--request-changes`).
    | RequestChanges
    /// A comment-only review (`--comment`).
    | Comment

/// What `prReview` submits (`gh pr review`). The constructor is private so the
/// invariant holds by construction: gh *requires* a body for request-changes /
/// comment reviews, so those are only reachable through `RequestChanges` / `Comment`
/// (which both take the body) — an empty-body request-changes is unrepresentable.
/// Approve's body is optional (`Approve` starts with none; attach one with `WithBody`).
[<Sealed>]
type ReviewAction private (kind: ReviewKind, body: string option) =
    /// Which kind of review this is.
    member _.Kind = kind
    /// The review body, if any.
    member _.Body = body

    /// Approve, with no body (`--approve`). Attach one with `WithBody`.
    static member Approve = ReviewAction(ReviewKind.Approve, None)

    /// Request changes; gh requires the body (`--request-changes --body <body>`).
    static member RequestChanges(body: string) =
        ReviewAction(ReviewKind.RequestChanges, Some body)

    /// A comment-only review; gh requires the body (`--comment --body <body>`).
    static member Comment(body: string) =
        ReviewAction(ReviewKind.Comment, Some body)

    /// Attach or replace the body — mainly to give an `Approve` a message.
    member _.WithBody(body: string) = ReviewAction(kind, Some body)

/// What the installed `gh` binary supports, probed via `GitHub.Capabilities`.
type GitHubCapabilities =
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
                    sprintf "VcsToolkit.GitHub requires gh >= %O, found %O" MIN_SUPPORTED_VERSION this.Version
                )
            )

    /// The minimum `gh` version this wrapper supports.
    static member MinimumSupported: Version = MIN_SUPPORTED_VERSION
