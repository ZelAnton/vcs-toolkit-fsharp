namespace VcsToolkit.Gitea

open System
open ProcessKit
open VcsToolkit.CliSupport

/// tea-specific command shaping shared by the client's methods.
[<AutoOpen>]
module private GiteaHelpers =

    /// Apply the argv injection guard to each (what, value) pair, short-circuiting on
    /// the first refusal.
    let checkFlags (checks: (string * string) list) : Result<unit, ProcessError> =
        let bad =
            checks
            |> List.tryPick (fun (what, value) ->
                match rejectFlagLike BINARY what value with
                | Error e -> Some e
                | Ok() -> None)

        match bad with
        | Some e -> Error e
        | None -> Ok()

    /// Map a parser's `Result<_, string>` error message into a `ProcessError.Parse`.
    let mapParse (r: Result<'T, string>) : Result<'T, ProcessError> =
        match r with
        | Ok v -> Ok v
        | Error m -> Error(ProcessError.Parse(BINARY, m))

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

    // --- Escape hatches / version / auth -------------------------------------

    /// Run `tea <args>` in the current directory, returning trimmed stdout. Unguarded
    /// — never forward untrusted argv (tea aliases can reach code execution).
    member _.Run(args: string seq) = core.Run(core.Command args)

    /// Like `Run` but never errors on a non-zero exit — returns the captured result.
    member _.RunRaw(args: string seq) = core.Output(core.Command args)

    /// Installed Gitea CLI version (`tea --version`).
    member _.Version() = core.Run(core.Command [ "--version" ])

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
                        return mapParse (GiteaParse.parseHasLogins json)
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

    /// Add a comment to a pull request, returning the command's output
    /// (`tea comment <index> <body>`). Gitea PRs and issues share the `index` space.
    /// The `body` is a bare positional, so it is rejected if empty or `-`-leading.
    member _.PrComment(dir: string, number: uint64, body: string) =
        task {
            match checkFlags [ "body", body ] with
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

    /// Releases for `dir` (`tea releases list --limit 100 --output json`). Up to 100.
    /// There is intentionally no `ReleaseView`: `tea releases` takes no positional and
    /// always lists, so a single-release-by-tag view does not exist in `tea`.
    member _.ReleaseList(dir: string) =
        core.TryParse(
            core.CommandIn(dir, [ "releases"; "list"; "--limit"; "100"; "--output"; "json" ]),
            GiteaParse.parseReleaseList
        )

    /// A view of this client bound to repository `dir`: modelled methods drop their leading
    /// `dir` argument. The raw `Run`/`RunRaw` hatches stay process-cwd.
    member this.At(dir: string) : GiteaAt = GiteaAt(this, dir)

/// A view of a `Gitea` client bound to a repository `dir`. Every modelled method drops the
/// leading `dir` argument and injects the bound one, so `at.PrList()` is `gitea.PrList dir`.
/// The raw `Run`/`RunRaw` escape hatches are deliberately NOT rebound: like the client's, they
/// run in the process's current working directory, not the bound `dir`. `tea` has no `api`
/// escape hatch, so this view has none either.
and [<Sealed>] GiteaAt internal (gitea: Gitea, dir: string) =

    // --- Escape hatches / version / auth (process-cwd, NOT the bound dir) -----

    /// Run `tea <args>` in the process's current directory (NOT the bound `dir`). Unguarded.
    member _.Run(args: string seq) = gitea.Run args

    /// Like `Run` but never errors on a non-zero exit. Process-cwd, NOT the bound `dir`.
    member _.RunRaw(args: string seq) = gitea.RunRaw args

    /// Installed Gitea CLI version (`tea --version`).
    member _.Version() = gitea.Version()

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

    /// Releases for the bound `dir` (`tea releases list …`).
    member _.ReleaseList() = gitea.ReleaseList dir
