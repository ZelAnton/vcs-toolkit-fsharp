namespace VcsToolkit.Gitea

open System
open ProcessKit
open VcsToolkit.CliSupport

/// The real Gitea (and Forgejo) client: typed async methods that run the real `tea`,
/// ask it for `--output json`, and deserialize the result. `Gitea.Create()` uses the
/// job-backed runner; `Gitea.WithRunner` injects a fake one for tests. Wraps a `ManagedClient`.
///
/// **Authentication is ambient.** Unlike the GitHub/GitLab wrappers, `tea` has no
/// non-interactive per-invocation token mechanism — it authenticates only from the
/// logins stored by `tea login add`. So this client offers no credential injection;
/// configure `tea`'s logins out of band.
///
/// **Deliberately leaner than the GitHub/GitLab wrappers**, because `tea` lacks the
/// commands: there is no current-repo view, no single-release-by-tag view, no
/// PR-checks command, no draft toggle (so no `prMarkReady`), and no `api` escape hatch.
/// `prView` is synthesized by listing with `--state all` and filtering by number.
///
/// Injection safety: `PrComment`'s body lands in a bare positional slot and is rejected
/// if empty or `-`-leading before spawning. Flag-value slots (`--title`, `--description`,
/// `--head`, …) are consumed verbatim.
[<Sealed>]
type Gitea private (core: ManagedClient) =

    /// Create a client driving the real job-backed runner.
    static member Create() = Gitea(ManagedClient.Create BINARY)

    /// Create a client driving `runner` — inject a fake in tests.
    static member WithRunner(runner: IProcessRunner) =
        Gitea(ManagedClient.WithRunner(BINARY, runner))

    // --- Configuration (chainable; each returns a new client) ----------------

    /// Apply a default timeout to every command this client builds.
    member _.DefaultTimeout(timeout: TimeSpan) = Gitea(core.DefaultTimeout timeout)

    /// Set an environment variable on every command this client builds.
    member _.DefaultEnv(key: string, value: string) = Gitea(core.DefaultEnv(key, value))

    /// Remove an inherited environment variable on every command this client builds.
    member _.DefaultEnvRemove(key: string) = Gitea(core.DefaultEnvRemove key)

    /// Cancel every command this client builds when `token` fires.
    member _.DefaultCancelOn(token: Threading.CancellationToken) = Gitea(core.DefaultCancelOn token)

    /// Retry lock-contention failures per `policy` (opt-in, off by default).
    member _.WithRetry(policy: RetryPolicy) = Gitea(core.WithRetry policy)

    /// Attach a diagnostic observer notified as each `tea` command starts and finishes
    /// (opt-in, off by default). See `ICommandObserver`.
    member _.WithObserver(observer: ICommandObserver) = Gitea(core.WithObserver observer)

    // --- Escape hatches / version / auth -------------------------------------

    /// Run `tea <args>` in the process's current directory, returning trimmed stdout. Unguarded
    /// — never forward untrusted argv (tea aliases can reach code execution). For an ad-hoc
    /// command scoped to a repository, use the `dir`-taking overload (`Run(dir, args)`) or a
    /// bound view's `at(dir).Run(args)`.
    member _.Run(args: string seq) = core.Run(core.Command args)

    /// Run `tea <args>` in `dir`, returning trimmed stdout — the `dir`-bound counterpart of
    /// `Run(args)` (which runs in the process cwd). Backs `GiteaAt.Run`. Equally unguarded.
    member _.Run(dir: string, args: string seq) = core.Run(core.CommandIn(dir, args))

    /// Like `Run` but never errors on a non-zero exit — returns the captured result.
    member _.RunRaw(args: string seq) = core.Output(core.Command args)

    /// Like `Run(dir, args)` but never errors on a non-zero exit — returns the captured
    /// result. Backs `GiteaAt.RunRaw`.
    member _.RunRaw(dir: string, args: string seq) = core.Output(core.CommandIn(dir, args))

    /// Installed Gitea CLI version (`tea --version`).
    member _.Version() = core.Run(core.Command [ "--version" ])

    /// The installed binary's parsed version, as `GiteaCapabilities`. An unrecognisable
    /// `tea --version` banner is a `Parse` error (never a throw) — the predictable
    /// degradation for a non-standard version string.
    member this.Capabilities() =
        task {
            match! this.Version() with
            | Error e -> return Error e
            | Ok raw ->
                match GiteaParse.parseVersion raw with
                | Some v -> return Ok { Version = v }
                | None ->
                    return
                        Error(ProcessError.Parse(BINARY, sprintf "unrecognisable `tea --version` output: \"%s\"" raw))
        }

    /// Whether at least one login is configured (`tea login list --output json` is a
    /// non-empty array). `tea` has no per-instance `auth status`, so this is the closest
    /// "are we logged in" signal. A non-zero exit (e.g. no config file yet) reads as
    /// `false`, the same as an empty array; only a spawn failure or timeout errors.
    member _.AuthStatus() =
        task {
            match! core.Output(core.Command [ "login"; "list"; "--output"; "json" ]) with
            | Error e -> return Error e
            | Ok res ->
                match res.Code with
                | Some 0 ->
                    // Some tea builds print nothing (not `[]`) when none are configured;
                    // treat empty output as "no logins" rather than a parse error.
                    let json = res.Stdout.Trim()

                    if json = "" then
                        return Ok false
                    else
                        return mapParse BINARY (GiteaParse.parseHasLogins json)
                | Some _ ->
                    // A plain non-zero exit just means "no logins" → false.
                    return Ok false
                | None ->
                    // No exit code (a timeout / signal-kill) is a genuine failure;
                    // `ensureSuccess` surfaces it as `Timeout`/IO rather than "false".
                    match ProcessResult.ensureSuccess res with
                    | Error e -> return Error e
                    | Ok _ -> return Ok false // unreachable: a None code always errors above.
        }

    // --- PR lifecycle --------------------------------------------------------

    /// Open pull requests for `dir` (`tea pr list --limit 100 --fields … --output json`).
    /// Up to 100 open PRs. `--fields` selects the columns the parser reads.
    member _.PrList(dir: string) =
        core.TryParse(
            core.CommandIn(dir, [ "pr"; "list"; "--limit"; "100"; "--fields"; PR_FIELDS; "--output"; "json" ]),
            GiteaParse.parsePrList
        )

    /// A single pull request by number. `tea` has no single-PR view, so this **lists** all
    /// states and **pages** (`tea pr list --state all --limit 50 --page N …`) until #number is
    /// found or a page returns empty (past the end). The Gitea server caps each page at ~50 and
    /// `tea` makes one call per page, so a single large `--limit` would silently clamp and
    /// page-miss a higher-numbered PR — paging avoids that false "not found".
    member _.PrView(dir: string, number: uint64) =
        task {
            let limit = string PR_VIEW_PAGE_SIZE
            let mutable found: Result<PullRequest, ProcessError> option = None
            let mutable page = 1

            while Option.isNone found && page <= PR_VIEW_MAX_PAGES do
                let cmd =
                    core.CommandIn(
                        dir,
                        [ "pr"
                          "list"
                          "--state"
                          "all"
                          "--limit"
                          limit
                          "--page"
                          string page
                          "--fields"
                          PR_FIELDS
                          "--output"
                          "json" ]
                    )

                match! core.TryParse(cmd, GiteaParse.parsePrList) with
                | Error e -> found <- Some(Error e)
                | Ok prs ->
                    match prs |> List.tryFind (fun pr -> pr.Number = number) with
                    | Some pr -> found <- Some(Ok pr)
                    | None when List.isEmpty prs ->
                        // An empty page means we walked past the last PR — a genuine absence.
                        found <-
                            Some(
                                Error(ProcessError.Parse(BINARY, sprintf "no pull request #%d in `tea pr list`" number))
                            )
                    | None -> page <- page + 1

            match found with
            | Some r -> return r
            | None ->
                // Hit the page safety bound without finding it — an extremely large repo.
                // Report honestly rather than a confident false "not found".
                return
                    Error(
                        ProcessError.Parse(
                            BINARY,
                            sprintf
                                "pull request #%d not found in the first %d of `tea pr list` (stopped at the %d-page safety bound; query `tea`/the Gitea API directly for a repository this large)"
                                number
                                (PR_VIEW_MAX_PAGES * PR_VIEW_PAGE_SIZE)
                                PR_VIEW_MAX_PAGES
                        )
                    )
        }

    /// Open a pull request, returning the command's textual output (`tea pr create`).
    /// Unlike gh/glab, `tea` prints a summary, **not** the new PR's URL — do not parse
    /// this as a URL. See `PrCreate`.
    member _.PrCreate(dir: string, spec: PrCreate) =
        let args =
            [ "pr"; "create"; "--title"; spec.Title; "--description"; spec.Body ]
            @ (match spec.Head with
               | Some h -> [ "--head"; h ]
               | None -> [])
            @ (match spec.Base with
               | Some b -> [ "--base"; b ]
               | None -> [])

        core.Run(core.CommandIn(dir, args))

    /// Merge a pull request (`tea pr merge <number> --style merge|rebase|squash`).
    /// See `MergeStrategy`.
    member _.PrMerge(dir: string, number: uint64, strategy: MergeStrategy) =
        core.RunUnit(core.CommandIn(dir, [ "pr"; "merge"; string number; "--style"; strategy.Style ]))

    /// Close a pull request without merging (`tea pr close <number>`).
    member _.PrClose(dir: string, number: uint64) =
        core.RunUnit(core.CommandIn(dir, [ "pr"; "close"; string number ]))

    /// Check out a pull request's branch locally in `dir` (`tea pr checkout <index>`): fetch
    /// the PR's head branch and switch the working tree to it. A local-worktree mutation (it
    /// changes `dir`'s checked-out branch), so it returns unit like the other lifecycle
    /// mutations. The number is a positional but is always digits (`uint64`), so no
    /// injection guard is needed.
    member _.PrCheckout(dir: string, number: uint64) =
        core.RunUnit(core.CommandIn(dir, [ "pr"; "checkout"; string number ]))

    /// Approve a pull request (`tea pr approve <index> [<comment>]`). `comment` is optional
    /// — when `Some`, it lands in a bare positional slot after the index, so it is rejected
    /// if empty or `-`-leading (like `PrComment`'s body); when `None`, the bare approve is
    /// sent. The number is always digits, so it needs no guard.
    member _.PrApprove(dir: string, number: uint64, comment: string option) =
        task {
            match comment with
            | Some c ->
                match checkFlags BINARY [ "comment", c ] with
                | Error e -> return Error e
                | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "pr"; "approve"; string number; c ]))
            | None -> return! core.RunUnit(core.CommandIn(dir, [ "pr"; "approve"; string number ]))
        }

    /// Request changes on a pull request (`tea pr reject <index> <reason>`) — Gitea's
    /// request-changes review. `tea` requires the reason, and it is a bare positional, so it
    /// is rejected if empty or `-`-leading (like `PrComment`'s body).
    member _.PrReject(dir: string, number: uint64, reason: string) =
        task {
            match checkFlags BINARY [ "reason", reason ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "pr"; "reject"; string number; reason ]))
        }

    /// Add a comment to a pull request, returning the command's output
    /// (`tea comment <index> <body>`). Gitea PRs and issues share the `index` space.
    /// The `body` is a bare positional, so it is rejected if empty or `-`-leading.
    member _.PrComment(dir: string, number: uint64, body: string) =
        task {
            match checkFlags BINARY [ "body", body ] with
            | Error e -> return Error e
            | Ok() -> return! core.Run(core.CommandIn(dir, [ "comment"; string number; body ]))
        }

    /// Edit a pull request's title and/or description (`tea pr edit <index>
    /// [--title …] [--description …]`). At least one of `Title`/`Body` must be `Some`
    /// — both-`None` is refused before spawning. An empty string clears the field.
    member _.PrEdit(dir: string, number: uint64, edit: PrEdit) =
        task {
            match edit.Title, edit.Body with
            | None, None ->
                return Error(ProcessError.Spawn(BINARY, "pr edit requires at least a title or a body to change"))
            | _ ->
                let args =
                    [ "pr"; "edit"; string number ]
                    @ (match edit.Title with
                       | Some t -> [ "--title"; t ]
                       | None -> [])
                    @ (match edit.Body with
                       | Some b -> [ "--description"; b ]
                       | None -> [])

                return! core.RunUnit(core.CommandIn(dir, args))
        }

    // --- Issues / releases ---------------------------------------------------

    /// Open issues for `dir` (`tea issues list --limit 100 --fields … --output json`).
    /// Up to 100 open issues.
    member _.IssueList(dir: string) =
        core.TryParse(
            core.CommandIn(
                dir,
                [ "issues"
                  "list"
                  "--limit"
                  "100"
                  "--fields"
                  ISSUE_FIELDS
                  "--output"
                  "json" ]
            ),
            GiteaParse.parseIssueList
        )

    /// A single issue by number — the bare-index view (`tea issues <number> --output
    /// json`), deserialising one typed object.
    member _.IssueView(dir: string, number: uint64) =
        core.TryParse(core.CommandIn(dir, [ "issues"; string number; "--output"; "json" ]), GiteaParse.parseIssue)

    /// Open an issue, returning the command's textual output (`tea issues create
    /// --title <t> --description <d>`). Like `PrCreate`, `tea` prints a summary (with
    /// the new issue's URL on the final line), not a bare URL — returned verbatim.
    member _.IssueCreate(dir: string, title: string, body: string) =
        core.Run(core.CommandIn(dir, [ "issues"; "create"; "--title"; title; "--description"; body ]))

    /// Close an issue without further comment (`tea issues close <number>`).
    member _.IssueClose(dir: string, number: uint64) =
        core.RunUnit(core.CommandIn(dir, [ "issues"; "close"; string number ]))

    /// Add a comment to an issue, returning the command's output (`tea comment <index>
    /// <body>`). Gitea PRs and issues share the `index` space, so this is the same
    /// `tea comment` command `PrComment` uses. The `body` is a bare positional, so it is
    /// rejected if empty or `-`-leading.
    member _.IssueComment(dir: string, number: uint64, body: string) =
        task {
            match checkFlags BINARY [ "body", body ] with
            | Error e -> return Error e
            | Ok() -> return! core.Run(core.CommandIn(dir, [ "comment"; string number; body ]))
        }

    /// Releases for `dir` (`tea releases list --limit 100 --output json`). Up to 100.
    /// There is intentionally no `ReleaseView`: `tea releases` takes no positional and
    /// always lists, so a single-release-by-tag view does not exist in `tea`.
    member _.ReleaseList(dir: string) =
        core.TryParse(
            core.CommandIn(dir, [ "releases"; "list"; "--limit"; "100"; "--output"; "json" ]),
            GiteaParse.parseReleaseList
        )

    /// A view of this client bound to repository `dir`: modelled methods drop their leading
    /// `dir` argument, and the raw `Run`/`RunRaw` hatches run in the bound `dir` too.
    member this.At(dir: string) : GiteaAt = GiteaAt(this, dir)

/// A view of a `Gitea` client bound to a repository `dir`. Every modelled method drops the
/// leading `dir` argument and injects the bound one, so `at.PrList()` is `gitea.PrList dir`.
/// The raw `Run`/`RunRaw` escape hatches also run in the bound `dir` (forwarding to
/// `gitea.Run(dir, …)`/`gitea.RunRaw(dir, …)`); for a raw command that must run in the process's
/// current directory instead, call `Run`/`RunRaw` on the unbound `Gitea` client. `tea` has no
/// `api` escape hatch, so this view has none either.
and [<Sealed>] GiteaAt internal (gitea: Gitea, dir: string) =

    // --- Escape hatches / version / auth (Run/RunRaw bound to `dir`) ----------

    /// Run `tea <args>` in the bound `dir`. Unguarded.
    member _.Run(args: string seq) = gitea.Run(dir, args)

    /// Like `Run` but never errors on a non-zero exit — returns the captured result.
    member _.RunRaw(args: string seq) = gitea.RunRaw(dir, args)

    /// Installed Gitea CLI version (`tea --version`).
    member _.Version() = gitea.Version()

    /// The installed binary's parsed version, as `GiteaCapabilities`.
    member _.Capabilities() = gitea.Capabilities()

    /// Whether at least one login is configured (`tea login list --output json`).
    member _.AuthStatus() = gitea.AuthStatus()

    // --- Modelled methods (dir injected as the first argument) ----------------

    /// Open pull requests for the bound `dir` (`tea pr list …`).
    member _.PrList() = gitea.PrList dir

    /// A single pull request by number (synthesized via `tea pr list --state all …`).
    member _.PrView(number: uint64) = gitea.PrView(dir, number)

    /// Open a pull request (`tea pr create`).
    member _.PrCreate(spec: PrCreate) = gitea.PrCreate(dir, spec)

    /// Merge a pull request (`tea pr merge <n> --style …`).
    member _.PrMerge(number: uint64, strategy: MergeStrategy) = gitea.PrMerge(dir, number, strategy)

    /// Close a pull request without merging (`tea pr close <n>`).
    member _.PrClose(number: uint64) = gitea.PrClose(dir, number)

    /// Check out a pull request's branch locally (`tea pr checkout <index>`).
    member _.PrCheckout(number: uint64) = gitea.PrCheckout(dir, number)

    /// Approve a pull request (`tea pr approve <index> [<comment>]`).
    member _.PrApprove(number: uint64, comment: string option) = gitea.PrApprove(dir, number, comment)

    /// Request changes on a pull request (`tea pr reject <index> <reason>`).
    member _.PrReject(number: uint64, reason: string) = gitea.PrReject(dir, number, reason)

    /// Add a comment to a pull request (`tea comment <index> <body>`).
    member _.PrComment(number: uint64, body: string) = gitea.PrComment(dir, number, body)

    /// Edit a pull request's title and/or description (`tea pr edit <index> …`).
    member _.PrEdit(number: uint64, edit: PrEdit) = gitea.PrEdit(dir, number, edit)

    /// Open issues for the bound `dir` (`tea issues list …`).
    member _.IssueList() = gitea.IssueList dir

    /// A single issue by number (`tea issues <n> --output json`).
    member _.IssueView(number: uint64) = gitea.IssueView(dir, number)

    /// Open an issue (`tea issues create …`).
    member _.IssueCreate(title: string, body: string) = gitea.IssueCreate(dir, title, body)

    /// Close an issue (`tea issues close <index>`).
    member _.IssueClose(number: uint64) = gitea.IssueClose(dir, number)

    /// Add a comment to an issue (`tea comment <index> <body>`).
    member _.IssueComment(number: uint64, body: string) = gitea.IssueComment(dir, number, body)

    /// Releases for the bound `dir` (`tea releases list …`).
    member _.ReleaseList() = gitea.ReleaseList dir
