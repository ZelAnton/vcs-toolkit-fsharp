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
    let PR_FIELDS = "number,title,state,headRefName,baseRefName,url,labels,assignees"

    /// `--json` field set for `repo view`.
    [<Literal>]
    let REPO_FIELDS = "name,owner,description,url,isPrivate,defaultBranchRef"

    /// `--json` field set for `issue list`.
    [<Literal>]
    let ISSUE_LIST_FIELDS = "number,title,state,body,url,labels,assignees"

    /// `--json` field set for `issue view`.
    [<Literal>]
    let ISSUE_VIEW_FIELDS = "number,title,state,body,url,labels,assignees"

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

/// Host-classification helpers for `GitHubHost`. Kept local to this crate (the Forge
/// facade sits *above* GitHub in the dependency stack, so its `OfRemoteUrl` classifier
/// can't be reused here); this is the gh-specific port of the Rust `host_from_remote_url`
/// / `validate_host` pair, which is stricter than Forge's SaaS-only classifier (any
/// valid dotted host is a GHES host, and an ambiguous remote is an error, not `None`).
[<AutoOpen>]
module private HostClassify =

    /// A DNS-host character: ASCII letter/digit, `.`, or `-` (matching Rust
    /// `is_ascii_alphanumeric() || '.' || '-'`).
    let inline private isHostChar (c: char) =
        (c >= '0' && c <= '9')
        || (c >= 'a' && c <= 'z')
        || (c >= 'A' && c <= 'Z')
        || c = '.'
        || c = '-'

    /// The `Spawn` error the crate raises for a rejected host value — the same shape
    /// `rejectFlagLike` uses for a refused positional, naming the bad host and why.
    let invalidHostError (value: string) (reason: string) : ProcessError =
        ProcessError.Spawn(BINARY, sprintf "GitHub host \"%s\": %s" value reason)

    /// Validate a bare gh hostname, returning it **lower-cased** (its canonical form —
    /// hostnames are case-insensitive and `gh` stores them lower-cased). A host must be
    /// a non-empty DNS-style name (ASCII letters/digits/`.`/`-`), not start with `-`/`.`
    /// nor end with `.`, and carry no scheme, path, port, userinfo, or whitespace.
    /// Anything else is refused — `gh` would misread it, or it is not a host at all.
    let validateHost (host: string) : Result<string, ProcessError> =
        let trimmed = host.Trim()

        let wellFormed =
            trimmed <> ""
            && not (trimmed.StartsWith('-'))
            && not (trimmed.StartsWith('.'))
            && not (trimmed.EndsWith('.'))
            && trimmed |> Seq.forall isHostChar

        if wellFormed then
            Ok(asciiLower trimmed)
        else
            Error(invalidHostError host "not a valid GitHub hostname")

    /// Extract the hostname from a repository remote URL (HTTPS / SSH / scp-like),
    /// dropping any userinfo and port. Returns `None` when no unambiguous host is present
    /// — an IPv6-literal authority (`[::1]`) and a bare single-label scp authority
    /// (indistinguishable from a Windows drive path) included — so `GitHubHost.OfRemoteUrl`
    /// surfaces a diagnosable error rather than defaulting to github.com. A GitHub host is
    /// a dotted DNS name.
    let hostFromRemoteUrl (url: string) : string option =
        let url = url.Trim()

        if url = "" then
            None
        else
            match url.IndexOf("://", System.StringComparison.Ordinal) with
            | i when i >= 0 ->
                // scheme://[user@]host[:port]/…  — the authority ends at the first
                // `/`/`?`/`#`; drop any `user:pass@` userinfo, then a trailing `:port`.
                let rest = url.Substring(i + 3)
                let authority = rest.Split([| '/'; '?'; '#' |]).[0]

                let hostPort =
                    match authority.LastIndexOf('@') with
                    | j when j >= 0 -> authority.Substring(j + 1)
                    | _ -> authority

                // Refuse an IPv6-literal authority (`[::1]`): a GitHub host is never a
                // bracketed literal, and gh names hosts without a port.
                if hostPort = "" || hostPort.StartsWith('[') then
                    None
                else
                    match hostPort.Split(':').[0] with
                    | "" -> None
                    | h -> Some h
            | _ ->
                // scp-like SSH `[user@]host:path` (no scheme): the host ends at the first `:`.
                match url.IndexOf(':') with
                | j when j >= 0 ->
                    let authority = url.Substring(0, j)

                    let host =
                        match authority.LastIndexOf('@') with
                        | k when k >= 0 -> authority.Substring(k + 1)
                        | _ -> authority

                    // Require a dotted host so a Windows drive path (`C:\…`) or a bare
                    // single-label authority isn't misread as a remote host — those are
                    // ambiguous, and the caller gets a diagnosable error, not a guess.
                    if host.Contains('.') && not (host.Contains('/')) && not (host.Contains('\\')) then
                        Some host
                    else
                        None
                | _ -> None

/// The GitHub host an operation targets: SaaS `github.com` or a **GitHub Enterprise
/// Server** (GHES) host. `gh` reads a supplied credential from a different environment
/// variable per host — `GH_TOKEN` for github.com, `GH_ENTERPRISE_TOKEN` for a GHES host
/// — and its `auth status` can be scoped to a single host, so this type carries the
/// target host: the client injects a credential into the variable `gh` actually reads
/// for it (`GitHub.WithHost`) and can probe auth for exactly that host
/// (`GitHub.AuthStatusFor`).
///
/// Build it for github.com (`GitHubHost.GitHubCom`), from a bare hostname
/// (`GitHubHost.New`), or from a repository's remote URL (`GitHubHost.OfRemoteUrl`). A
/// host that cannot be determined is an **error**, never a silent fall back to
/// github.com — so an ambiguous or unknown host is a diagnosable result at the call site
/// rather than a quiet authentication against the wrong host with the github.com token.
[<Sealed>]
type GitHubHost private (host: string, enterprise: bool) =

    /// The SaaS GitHub hostname (`github.com`).
    static member SaasHost = "github.com"

    /// The SaaS github.com host — a supplied credential is injected as `GH_TOKEN`.
    static member GitHubCom = GitHubHost(GitHubHost.SaasHost, false)

    /// Classify a bare `host`: `github.com` (case-insensitive) is SaaS; any other valid
    /// hostname is a GitHub Enterprise Server host (its credential goes to
    /// `GH_ENTERPRISE_TOKEN`). An empty, flag-like, or malformed host (a scheme, path,
    /// port, userinfo, or whitespace) is an error rather than a github.com guess.
    static member New(host: string) : Result<GitHubHost, ProcessError> =
        match validateHost host with
        | Error e -> Error e
        | Ok canonical -> Ok(GitHubHost(canonical, canonical <> GitHubHost.SaasHost))

    /// Derive the host from a repository **remote URL** and classify it. Handles
    /// `scheme://[user@]host[:port]/…` (HTTPS/SSH/…) and the scp-like `[user@]host:path`
    /// SSH form; any userinfo and port are dropped. A remote whose host can't be
    /// determined (unparseable, hostless, or ambiguous — an IPv6 literal, a bare
    /// single-label scp authority, a local path) is an **error**, not a silent github.com
    /// fallback, so the caller can surface an ambiguous remote as a diagnosable result.
    static member OfRemoteUrl(url: string) : Result<GitHubHost, ProcessError> =
        match hostFromRemoteUrl url with
        | Some h -> GitHubHost.New h
        | None -> Error(invalidHostError url "no GitHub host could be determined from the remote URL")

    /// The canonical (lower-cased) hostname (`github.com`, `ghe.example.com`).
    member _.Host = host

    /// Whether this is a GitHub Enterprise Server host (anything but github.com).
    member _.IsEnterprise = enterprise

    /// Whether this is SaaS github.com.
    member _.IsGitHubCom = not enterprise

    /// The environment variable `gh` reads for a credential on this host — `GH_TOKEN`
    /// for github.com, `GH_ENTERPRISE_TOKEN` for a GHES host. Seeds the client's
    /// token-env binding in `GitHub.WithHost`.
    member internal _.TokenEnvVar =
        if enterprise then "GH_ENTERPRISE_TOKEN" else "GH_TOKEN"

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
