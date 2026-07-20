namespace VcsToolkit.GitLab

open System
open ProcessKit
open VcsToolkit.CliSupport
open VcsToolkit.Diff

/// glab-specific command shaping shared by the client's methods.
[<AutoOpen>]
module private GitLabHelpers =

    /// Refuse a body/description value equal to exactly `-` before spawning: glab
    /// treats a bare `-` as its own sentinel meaning "read from stdin / open
    /// $EDITOR", not the literal string, and a headless call would hang waiting
    /// for input that never arrives (glab has no timeout on this prompt). A value
    /// that merely contains a dash (`"-x"`, `"a-b"`) or is empty passes through
    /// byte-for-byte.
    let rejectDashSentinel (what: string) (value: string) : Result<unit, ProcessError> =
        if value = "-" then
            Error(
                ProcessError.Spawn(
                    BINARY,
                    sprintf
                        "%s is \"-\", which glab treats as its own stdin/editor sentinel — refusing to pass it as a literal value"
                        what
                )
            )
        else
            Ok()

/// The real GitLab client: typed async methods that run the real `glab`, ask it for
/// `--output json`, and deserialize the result. `GitLab.Create()` uses the job-backed
/// runner; `GitLab.WithRunner` injects a fake one for tests. Wraps a `ManagedClient`.
///
/// By default it authenticates through `glab`'s own ambient login; attach a credential
/// provider with `WithCredentials`/`WithToken`/`WithEnvToken` to supply a token per
/// operation — it is injected as `GITLAB_TOKEN` on every `glab` invocation (never in argv).
///
/// Injection safety: the methods that place a caller value in a bare positional slot
/// (`api` endpoint, release `tag`) reject an empty or `-`-leading value before
/// spawning. Flag-value slots (`--description`, `--source-branch`, …) are consumed verbatim.
[<Sealed>]
type GitLab private (core: ManagedClient) =

    /// Create a client driving the real job-backed runner.
    static member Create() =
        GitLab(ManagedClient.Create(BINARY).WithTokenEnv(CredentialService.GitLab, "GITLAB_TOKEN"))

    /// Create a client driving `runner` — inject a fake in tests.
    static member WithRunner(runner: IProcessRunner) =
        GitLab(ManagedClient.WithRunner(BINARY, runner).WithTokenEnv(CredentialService.GitLab, "GITLAB_TOKEN"))

    // --- Configuration (chainable; each returns a new client) ----------------

    /// Apply a default timeout to every command this client builds.
    member _.DefaultTimeout(timeout: TimeSpan) = GitLab(core.DefaultTimeout timeout)

    /// Set an environment variable on every command this client builds.
    member _.DefaultEnv(key: string, value: string) = GitLab(core.DefaultEnv(key, value))

    /// Remove an inherited environment variable on every command this client builds.
    member _.DefaultEnvRemove(key: string) = GitLab(core.DefaultEnvRemove key)

    /// Cancel every command this client builds when `token` fires.
    member _.DefaultCancelOn(token: Threading.CancellationToken) = GitLab(core.DefaultCancelOn token)

    /// Retry lock-contention failures per `policy` (opt-in, off by default).
    member _.WithRetry(policy: RetryPolicy) = GitLab(core.WithRetry policy)

    /// Attach a diagnostic observer notified as each `glab` command starts and finishes
    /// (opt-in, off by default). See `ICommandObserver`.
    member _.WithObserver(observer: ICommandObserver) = GitLab(core.WithObserver observer)

    /// Supply credentials per operation via a provider — opt-in, off by default
    /// (ambient `glab` auth). The resolved token is injected as `GITLAB_TOKEN`.
    member _.WithCredentials(provider: ICredentialProvider) = GitLab(core.WithCredentials provider)

    /// Authenticate with a single static `token`, injected as `GITLAB_TOKEN`.
    member this.WithToken(token: string) =
        this.WithCredentials(StaticCredential.Token token :> ICredentialProvider)

    /// Read the token from environment variable `var` at request time (injected as
    /// `GITLAB_TOKEN`); if unset/empty, fall back to ambient auth.
    member this.WithEnvToken(var: string) =
        this.WithCredentials(EnvToken var :> ICredentialProvider)

    // --- Escape hatches / version / auth -------------------------------------

    /// Run `glab <args>` in the process's current directory, returning trimmed stdout. Unguarded
    /// — never forward untrusted argv (glab aliases and `glab api` can reach code execution). For
    /// an ad-hoc command scoped to a repository, use the `dir`-taking overload (`Run(dir, args)`)
    /// or a bound view's `at(dir).Run(args)`.
    member _.Run(args: string seq) = core.Run(core.Command args)

    /// Run `glab <args>` in `dir`, returning trimmed stdout — the `dir`-bound counterpart of
    /// `Run(args)` (which runs in the process cwd). Backs `GitLabAt.Run`. Equally unguarded.
    member _.Run(dir: string, args: string seq) = core.Run(core.CommandIn(dir, args))

    /// Like `Run` but never errors on a non-zero exit — returns the captured result.
    member _.RunRaw(args: string seq) = core.Output(core.Command args)

    /// Like `Run(dir, args)` but never errors on a non-zero exit — returns the captured
    /// result. Backs `GitLabAt.RunRaw`.
    member _.RunRaw(dir: string, args: string seq) = core.Output(core.CommandIn(dir, args))

    /// Installed GitLab CLI version (`glab --version`).
    member _.Version() = core.Run(core.Command [ "--version" ])

    /// The installed binary's parsed version, as `GitLabCapabilities`. An unrecognisable
    /// `glab --version` banner is a `Parse` error (never a throw) — the predictable
    /// degradation for a non-standard version string.
    member this.Capabilities() =
        task {
            match! this.Version() with
            | Error e -> return Error e
            | Ok raw ->
                match GitLabParse.parseVersion raw with
                | Some v -> return Ok { Version = v }
                | None ->
                    return
                        Error(ProcessError.Parse(BINARY, sprintf "unrecognisable `glab --version` output: \"%s\"" raw))
        }

    /// Whether the user is authenticated (`glab auth status` exits zero). Any non-zero
    /// exit reads as `false`; only a spawn failure or timeout errors.
    ///
    /// Caveat: a long-standing glab bug (gitlab-org/cli#911) can make `glab auth status`
    /// exit `0` even when *not* authenticated, so a `true` here is a best-effort signal,
    /// not a guarantee — a subsequent API call is the real test. A `false`, a spawn
    /// failure, or a timeout are still reported faithfully.
    member _.AuthStatus() =
        task {
            match! core.ExitCode(core.Command [ "auth"; "status" ]) with
            | Error e -> return Error e
            | Ok code -> return Ok(code = 0)
        }

    /// Raw GitLab REST/GraphQL response body (`glab api <endpoint>`), run in the bound repo
    /// `dir` so a relative endpoint resolves against *that* project rather than whatever repo
    /// the process cwd happens to be in.
    member _.Api(dir: string, endpoint: string) =
        task {
            match checkFlags BINARY [ "endpoint", endpoint ] with
            | Error e -> return Error e
            | Ok() -> return! core.Run(core.CommandIn(dir, [ "api"; endpoint ]))
        }

    // --- Project / lists -----------------------------------------------------

    /// The project for `dir` (`glab repo view --output json`).
    member _.RepoView(dir: string) =
        core.TryParse(core.CommandIn(dir, [ "repo"; "view"; "--output"; "json" ]), GitLabParse.parseRepoView)

    /// Open merge requests for `dir` (`glab mr list [--closed|--merged|--all] --per-page
    /// <limit> --output json`). `options` (defaulting to `MrListOptions.Default` — open, up
    /// to 100, the GitLab API per-page max) selects the state filter and result cap;
    /// omitting it reproduces the previous behaviour exactly.
    member _.MrList(dir: string, ?options: MrListOptions) =
        let opts = defaultArg options MrListOptions.Default

        let args =
            [ "mr"; "list" ]
            @ opts.State.Flags
            @ [ "--per-page"; string opts.Limit; "--output"; "json" ]

        core.TryParse(core.CommandIn(dir, args), GitLabParse.parseMrList)

    /// A single merge request by its project-scoped number — GitLab's `iid`
    /// (`glab mr view <number> --output json`).
    member _.MrView(dir: string, number: uint64) =
        core.TryParse(core.CommandIn(dir, [ "mr"; "view"; string number; "--output"; "json" ]), GitLabParse.parseMr)

    /// Open a merge request, returning the command's output (the MR URL on success)
    /// (`glab mr create`). See `MrCreate`. `--yes` skips glab's submission prompt.
    member _.MrCreate(dir: string, spec: MrCreate) =
        task {
            match rejectDashSentinel "body" spec.Body with
            | Error e -> return Error e
            | Ok() ->
                let args =
                    [ "mr"; "create"; "--title"; spec.Title; "--description"; spec.Body; "--yes" ]
                    @ (match spec.Source with
                       | Some s -> [ "--source-branch"; s ]
                       | None -> [])
                    @ (match spec.Target with
                       | Some t -> [ "--target-branch"; t ]
                       | None -> [])

                return! core.Run(core.CommandIn(dir, args))
        }

    // --- MR lifecycle --------------------------------------------------------

    /// Merge a merge request **immediately** (`glab mr merge <id> --yes --auto-merge=false
    /// [--squash|--rebase]`). `--auto-merge=false` overrides glab's default of enabling
    /// merge-when-pipeline-succeeds. See `MergeStrategy`.
    member _.MrMerge(dir: string, number: uint64, strategy: MergeStrategy) =
        let args =
            [ "mr"; "merge"; string number; "--yes"; "--auto-merge=false" ]
            @ (match strategy.Flag with
               | Some flag -> [ flag ]
               | None -> [])

        core.RunUnit(core.CommandIn(dir, args))

    /// Mark a draft merge request as ready (`glab mr update <id> --ready`).
    member _.MrMarkReady(dir: string, number: uint64) =
        core.RunUnit(core.CommandIn(dir, [ "mr"; "update"; string number; "--ready" ]))

    /// Close a merge request without merging (`glab mr close <id>`).
    member _.MrClose(dir: string, number: uint64) =
        core.RunUnit(core.CommandIn(dir, [ "mr"; "close"; string number ]))

    /// Check out a merge request's branch locally in `dir` (`glab mr checkout <id>`): fetch
    /// the MR's source branch and switch the working tree to it. A local-worktree mutation
    /// (it changes `dir`'s checked-out branch), so it returns unit like the other lifecycle
    /// mutations. The number is a positional but is always digits (`uint64`), so no
    /// injection guard is needed.
    member _.MrCheckout(dir: string, number: uint64) =
        core.RunUnit(core.CommandIn(dir, [ "mr"; "checkout"; string number ]))

    /// Approve a merge request (`glab mr approve <id>`): record the current user's approval.
    /// `glab`'s approve verb takes no comment, so this is the number-only form; use `MrComment`
    /// for a note. The number is a positional but is always digits (`uint64`), so no injection
    /// guard is needed.
    member _.MrApprove(dir: string, number: uint64) =
        core.RunUnit(core.CommandIn(dir, [ "mr"; "approve"; string number ]))

    /// Revoke a previously granted approval on a merge request (`glab mr revoke <id>`) — the
    /// inverse of `MrApprove`. The number is a positional but is always digits, so no injection
    /// guard is needed.
    member _.MrRevoke(dir: string, number: uint64) =
        core.RunUnit(core.CommandIn(dir, [ "mr"; "revoke"; string number ]))

    /// Add a comment to a merge request, returning the command's output
    /// (`glab mr note <id> -m <message>`).
    member _.MrComment(dir: string, number: uint64, body: string) =
        task {
            match rejectDashSentinel "body" body with
            | Error e -> return Error e
            | Ok() -> return! core.Run(core.CommandIn(dir, [ "mr"; "note"; string number; "-m"; body ]))
        }

    /// Edit a merge request's title and/or description (`glab mr update <id>
    /// [--title …] [--description …] --yes`). At least one of `Title`/`Body` must be
    /// `Some` — both-`None` is refused before spawning. An empty string clears the field.
    member _.MrEdit(dir: string, number: uint64, edit: MrEdit) =
        task {
            match edit.Title, edit.Body with
            | None, None ->
                return Error(ProcessError.Spawn(BINARY, "mr update requires at least a title or a body to change"))
            | _ ->
                let dashCheck =
                    match edit.Body with
                    | Some b -> rejectDashSentinel "body" b
                    | None -> Ok()

                match dashCheck with
                | Error e -> return Error e
                | Ok() ->
                    let args =
                        [ "mr"; "update"; string number ]
                        @ (match edit.Title with
                           | Some t -> [ "--title"; t ]
                           | None -> [])
                        @ (match edit.Body with
                           | Some b -> [ "--description"; b ]
                           | None -> [])
                        @ [ "--yes" ]

                    return! core.RunUnit(core.CommandIn(dir, args))
        }

    /// The MR's pipeline status, bucketed (`glab mr view <id> --output json`, reading
    /// `head_pipeline.status`). `CiStatus.None` when no pipeline ran.
    member _.MrChecks(dir: string, number: uint64) =
        core.TryParse(
            core.CommandIn(dir, [ "mr"; "view"; string number; "--output"; "json" ]),
            GitLabParse.parseCiStatus
        )

    /// The merge request's unified diff, parsed into per-file `FileDiff` values
    /// (`glab mr diff <n>`, then `VcsToolkit.Diff.parseDiff`). The number is a positional
    /// but is always digits (`uint64`), so no injection guard is needed.
    member _.MrDiff(dir: string, number: uint64) =
        task {
            match! runUntrimmed core (core.CommandIn(dir, [ "mr"; "diff"; string number ])) with
            | Error e -> return Error e
            | Ok raw -> return Ok(parseDiff raw)
        }

    // --- Issues / releases ---------------------------------------------------

    /// Open issues for `dir` (`glab issue list [--closed|--all] --per-page <limit> --output
    /// json`). `options` (defaulting to `IssueListOptions.Default` — open, up to 100, the
    /// GitLab API per-page max) selects the state filter and result cap; omitting it
    /// reproduces the previous behaviour exactly.
    member _.IssueList(dir: string, ?options: IssueListOptions) =
        let opts = defaultArg options IssueListOptions.Default

        let args =
            [ "issue"; "list" ]
            @ opts.State.Flags
            @ [ "--per-page"; string opts.Limit; "--output"; "json" ]

        core.TryParse(core.CommandIn(dir, args), GitLabParse.parseIssueList)

    /// A single issue by its project-scoped id (`iid`)
    /// (`glab issue view <number> --output json`).
    member _.IssueView(dir: string, number: uint64) =
        core.TryParse(
            core.CommandIn(dir, [ "issue"; "view"; string number; "--output"; "json" ]),
            GitLabParse.parseIssue
        )

    /// Open an issue, returning the command's output (the issue URL on success)
    /// (`glab issue create --title <t> --description <d> --yes`).
    member _.IssueCreate(dir: string, title: string, body: string) =
        task {
            match rejectDashSentinel "body" body with
            | Error e -> return Error e
            | Ok() ->
                return!
                    core.Run(
                        core.CommandIn(dir, [ "issue"; "create"; "--title"; title; "--description"; body; "--yes" ])
                    )
        }

    /// Close an issue without merging (`glab issue close <id>`).
    member _.IssueClose(dir: string, number: uint64) =
        core.RunUnit(core.CommandIn(dir, [ "issue"; "close"; string number ]))

    /// Add a comment (note) to an issue, returning the command's output
    /// (`glab issue note <id> -m <message>`) — the issue counterpart of `MrComment`, with
    /// the same `-`-sentinel body check the other mutating methods use.
    member _.IssueComment(dir: string, number: uint64, body: string) =
        task {
            match rejectDashSentinel "body" body with
            | Error e -> return Error e
            | Ok() -> return! core.Run(core.CommandIn(dir, [ "issue"; "note"; string number; "-m"; body ]))
        }

    /// Releases for `dir` (`glab release list --per-page 100 --output json`).
    /// Up to 100 (the GitLab API per-page max).
    member _.ReleaseList(dir: string) =
        core.TryParse(
            core.CommandIn(dir, [ "release"; "list"; "--per-page"; "100"; "--output"; "json" ]),
            GitLabParse.parseReleaseList
        )

    /// A single release by its tag (`glab release view <tag> --output json`).
    member _.ReleaseView(dir: string, tag: string) =
        task {
            match checkFlags BINARY [ "tag", tag ] with
            | Error e -> return Error e
            | Ok() ->
                return!
                    core.TryParse(
                        core.CommandIn(dir, [ "release"; "view"; tag; "--output"; "json" ]),
                        GitLabParse.parseRelease
                    )
        }

    /// A view of this client bound to repository `dir`: modelled methods drop their leading
    /// `dir` argument, and the raw `Run`/`RunRaw` hatches run in the bound `dir` too.
    member this.At(dir: string) : GitLabAt = GitLabAt(this, dir)

/// A view of a `GitLab` client bound to a repository `dir`. Every modelled method drops the
/// leading `dir` argument and injects the bound one, so `at.MrList()` is `gitlab.MrList dir`
/// and `at.Api(endpoint)` is `gitlab.Api(dir, endpoint)`. The raw `Run`/`RunRaw` escape hatches
/// also run in the bound `dir` (forwarding to `gitlab.Run(dir, …)`/`gitlab.RunRaw(dir, …)`); for
/// a raw command that must run in the process's current directory instead, call `Run`/`RunRaw`
/// on the unbound `GitLab` client.
and [<Sealed>] GitLabAt internal (gitlab: GitLab, dir: string) =

    // --- Escape hatches / version / auth (Run/RunRaw bound to `dir`) ----------

    /// Run `glab <args>` in the bound `dir`. Unguarded.
    member _.Run(args: string seq) = gitlab.Run(dir, args)

    /// Like `Run` but never errors on a non-zero exit — returns the captured result.
    member _.RunRaw(args: string seq) = gitlab.RunRaw(dir, args)

    /// Installed GitLab CLI version (`glab --version`).
    member _.Version() = gitlab.Version()

    /// The installed binary's parsed version, as `GitLabCapabilities`.
    member _.Capabilities() = gitlab.Capabilities()

    /// Whether the user is authenticated (`glab auth status` exits zero).
    member _.AuthStatus() = gitlab.AuthStatus()

    // --- Modelled methods (dir injected as the first argument) ----------------

    /// Raw GitLab REST/GraphQL response body for the bound `dir` (`glab api <endpoint>`).
    member _.Api(endpoint: string) = gitlab.Api(dir, endpoint)

    /// The project for the bound `dir` (`glab repo view --output json`).
    member _.RepoView() = gitlab.RepoView dir

    /// Open merge requests for the bound `dir` (`glab mr list …`).
    member _.MrList(?options: MrListOptions) = gitlab.MrList(dir, ?options = options)

    /// A single merge request by its project-scoped number (`glab mr view <n> …`).
    member _.MrView(number: uint64) = gitlab.MrView(dir, number)

    /// Open a merge request (`glab mr create`).
    member _.MrCreate(spec: MrCreate) = gitlab.MrCreate(dir, spec)

    /// Merge a merge request immediately (`glab mr merge <id> …`).
    member _.MrMerge(number: uint64, strategy: MergeStrategy) = gitlab.MrMerge(dir, number, strategy)

    /// Mark a draft merge request as ready (`glab mr update <id> --ready`).
    member _.MrMarkReady(number: uint64) = gitlab.MrMarkReady(dir, number)

    /// Close a merge request without merging (`glab mr close <id>`).
    member _.MrClose(number: uint64) = gitlab.MrClose(dir, number)

    /// Check out a merge request's branch locally (`glab mr checkout <id>`).
    member _.MrCheckout(number: uint64) = gitlab.MrCheckout(dir, number)

    /// Approve a merge request (`glab mr approve <id>`).
    member _.MrApprove(number: uint64) = gitlab.MrApprove(dir, number)

    /// Revoke an approval on a merge request (`glab mr revoke <id>`).
    member _.MrRevoke(number: uint64) = gitlab.MrRevoke(dir, number)

    /// Add a comment to a merge request (`glab mr note <id> -m …`).
    member _.MrComment(number: uint64, body: string) = gitlab.MrComment(dir, number, body)

    /// Edit a merge request's title and/or description (`glab mr update <id> …`).
    member _.MrEdit(number: uint64, edit: MrEdit) = gitlab.MrEdit(dir, number, edit)

    /// The MR's pipeline status, bucketed (`glab mr view <id> --output json`).
    member _.MrChecks(number: uint64) = gitlab.MrChecks(dir, number)

    /// The merge request's unified diff, parsed into per-file `FileDiff` values
    /// (`glab mr diff <n>`).
    member _.MrDiff(number: uint64) = gitlab.MrDiff(dir, number)

    /// Open issues for the bound `dir` (`glab issue list …`).
    member _.IssueList(?options: IssueListOptions) =
        gitlab.IssueList(dir, ?options = options)

    /// A single issue by its project-scoped id (`glab issue view <n> …`).
    member _.IssueView(number: uint64) = gitlab.IssueView(dir, number)

    /// Open an issue (`glab issue create …`).
    member _.IssueCreate(title: string, body: string) = gitlab.IssueCreate(dir, title, body)

    /// Close an issue (`glab issue close <id>`).
    member _.IssueClose(number: uint64) = gitlab.IssueClose(dir, number)

    /// Add a comment (note) to an issue (`glab issue note <id> -m …`).
    member _.IssueComment(number: uint64, body: string) = gitlab.IssueComment(dir, number, body)

    /// Releases for the bound `dir` (`glab release list …`).
    member _.ReleaseList() = gitlab.ReleaseList dir

    /// A single release by its tag (`glab release view <tag> …`).
    member _.ReleaseView(tag: string) = gitlab.ReleaseView(dir, tag)
