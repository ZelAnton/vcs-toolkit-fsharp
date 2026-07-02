namespace VcsToolkit.Gitea

/// Toolkit-wide constants for the Gitea wrapper.
[<AutoOpen>]
module Constants =

    /// Name of the underlying CLI binary this crate drives (also drives Forgejo).
    ///
    /// Injection-safety note: most of the lean surface keeps caller values out of bare
    /// positional slots — PR numbers are `uint64`, and title/body/branch arguments ride
    /// in flag-value positions. The one exception is `PrComment`'s body: `tea comment
    /// <n> <body>` takes it as a bare positional, so it is guarded with `rejectFlagLike`.
    [<Literal>]
    let BINARY = "tea"

    /// `--fields` column set for `tea pr list` (every value comes back as a JSON string).
    [<Literal>]
    let PR_FIELDS = "index,title,state,head,base,url"

    /// `--fields` column set for `tea issues list`.
    [<Literal>]
    let ISSUE_FIELDS = "index,title,state,body,url"

    /// `tea` has no single-PR view, so `PrView` lists all states and filters by number.
    /// This caps the page; a repo with more PRs than this would page-miss a
    /// high-numbered PR (reported as a *possible* truncation in the not-found error).
    [<Literal>]
    let PR_VIEW_LIMIT = "999"

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
    member this.Style =
        match this with
        | MergeStrategy.Merge -> "merge"
        | MergeStrategy.Squash -> "squash"
        | MergeStrategy.Rebase -> "rebase"

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
