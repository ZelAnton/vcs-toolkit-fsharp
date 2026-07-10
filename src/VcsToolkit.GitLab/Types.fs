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
