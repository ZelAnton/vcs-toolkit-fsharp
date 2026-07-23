namespace VcsToolkit.Gitea

open System
open ProcessKit
open VcsToolkit.CliSupport

/// The real Gitea (and Forgejo) client: typed async methods that run the real `tea`, ask it
/// for its supported `--output csv` (tea 0.9.2 does not support `--output json`; see K-049 /
/// `GiteaParse`), and parse the result. `Gitea.Create()` uses the job-backed runner;
/// `Gitea.WithRunner` injects a fake one for tests. Wraps a `ManagedClient`.
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

    /// Whether at least one login is configured (`tea login list --output csv` prints a data
    /// row after its header). `tea` has no per-instance `auth status`, so this is the closest
    /// "are we logged in" signal. A non-zero exit (e.g. no config file yet) reads as `false`,
    /// the same as an empty table; only a spawn failure or timeout errors.
    member _.AuthStatus() =
        task {
            match! core.Output(core.Command [ "login"; "list"; "--output"; "csv" ]) with
            | Error e -> return Error e
            | Ok res ->
                match res.Code with
                | Some 0 ->
                    // Some tea builds print nothing (not even a header) when none are
                    // configured; treat empty output as "no logins" rather than a parse error.
                    let csv = res.Stdout.Trim()

                    if csv.Length = 0 then
                        return Ok false
                    else
                        return mapParse BINARY (GiteaParse.parseHasLogins csv)
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

    /// Open pull requests for `dir` — the previous, options-less behaviour (open, up to
    /// 100). Kept as a genuine `(dir)`-only overload (rather than folding into `dir` plus an
    /// `?options` optional parameter) for CLR binary compatibility: F#'s `?options` sugar
    /// still compiles to a required parameter at the metadata level, so an already-compiled
    /// caller of the pre-`PrListOptions` `PrList(dir)` would hit `MissingMethodException`
    /// against a build that replaced it outright.
    member this.PrList(dir: string) = this.PrList(dir, PrListOptions.Default)

    /// Pull requests for `dir` (`tea pr list --state <state> --limit <limit> --fields …
    /// --output csv`). `--fields` pins the exact columns the csv parser reads positionally.
    /// tea 0.9.2 does not support `--output json` on `pr list` (K-049), so this drives its
    /// supported `--output csv` (`outputdsv`) format instead.
    member _.PrList(dir: string, options: PrListOptions) =
        core.TryParse(
            core.CommandIn(
                dir,
                [ "pr"
                  "list"
                  "--state"
                  options.State.Flag
                  "--limit"
                  string options.Limit
                  "--fields"
                  PR_FIELDS
                  "--output"
                  "csv" ]
            ),
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
                          "csv" ]
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

    /// Merge a pull request (`tea pr merge --style merge|rebase|squash <number>`). See
    /// `MergeStrategy`.
    ///
    /// **Flags MUST precede the positional index** (confirmed live against tea 0.9.2; K-061).
    /// `tea pulls merge`'s own usage line reads `tea pulls merge [command options] <pull
    /// index>`, and its `Action` requires `ctx.Args().Len() == 1` before treating the sole
    /// remaining bare argument as the index (`cmd/pulls/merge.go`). `tea`'s argv parser
    /// (`urfave/cli` v2, itself layered over Go's `flag` package) stops recognising `--flag`
    /// tokens as soon as it hits the *first* bare positional — so `<number> --style <style>`
    /// (the original, broken argv here) leaves `--style`/`<style>` as two more bare
    /// positionals, `ctx.Args().Len()` becomes 3, and `tea` fails immediately with `Error:
    /// Must specify a PR index` (exactly the live-CI failure this fixes) without ever
    /// reaching the network. Putting `--style` before the index avoids the trap.
    member _.PrMerge(dir: string, number: uint64, strategy: MergeStrategy) =
        core.RunUnit(core.CommandIn(dir, [ "pr"; "merge"; "--style"; strategy.Style; string number ]))

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

    /// Editing a pull request's title/description is **not supported by `tea` 0.9.2**: there is
    /// no `pr edit` subcommand at all. `tea`'s `pulls` command registers only
    /// `list`/`checkout`/`clean`/`create`/`close`/`reopen`/`review`/`approve`/`reject`/`merge`
    /// (`cmd/pulls.go`); the `editPullState` helper in `cmd/pulls/edit.go` is a plain function
    /// wired up only by `close`/`reopen` (to flip `State`), never exposed standalone with
    /// `--title`/`--description` flags. Because `urfave/cli` v2 falls through an unrecognised
    /// subcommand to the parent's default action (`runPulls`), `tea pr edit …` silently runs a
    /// plain `pr list` (or a PR-detail view) instead of erring — it does the wrong thing
    /// quietly. Verified against the real tea 0.9.2 binary (`tea pulls --help` lists no `edit`;
    /// `tea pr edit …` falls through to the `pulls` default action) and its Go source (K-063).
    ///
    /// This member therefore **refuses structurally, before any spawn**, rather than silently
    /// mis-editing. Use `PrClose` (or tea's `reopen` subcommand) for state changes, or the Gitea
    /// REST API for a genuine title/body edit. Kept for signature parity with the GitHub/GitLab
    /// clients.
    member _.PrEdit(dir: string, number: uint64, edit: PrEdit) =
        task {
            // tea 0.9.2 exposes no `pr edit` command, so there is no argv to build: refuse up
            // front rather than let an unrecognised `pr edit` silently fall through to `pr list`.
            // `dir`/`edit` are unused for the same reason — there is no command to pass them to.
            ignore (dir, edit)

            // Annotate the success type: with only an `Error` branch, F# would otherwise
            // generalise the result's `Ok` type, turning this into a generic member.
            let refusal: Result<unit, ProcessError> =
                Error(
                    ProcessError.Spawn(
                        BINARY,
                        sprintf
                            "tea 0.9.2 has no `pr edit` command — cannot edit PR #%d's title/description (an unrecognised `pr edit` silently falls through to `pr list`); use close/reopen for state changes or the Gitea REST API for a title/body edit"
                            number
                    )
                )

            return refusal
        }

    // --- Issues / releases ---------------------------------------------------

    /// Open issues for `dir` — the previous, options-less behaviour (open, up to 100). Kept
    /// as a genuine `(dir)`-only overload for CLR binary compatibility (see `PrList`'s doc
    /// comment for the rationale).
    member this.IssueList(dir: string) =
        this.IssueList(dir, IssueListOptions.Default)

    /// Issues for `dir` (`tea issues list --state <state> --limit <limit> --fields …
    /// --output csv`). `--fields` pins the exact columns the csv parser reads positionally.
    /// tea 0.9.2 does not support `--output json` on `issues list` (K-049), so this drives
    /// its supported `--output csv` (`outputdsv`) format instead.
    member _.IssueList(dir: string, options: IssueListOptions) =
        core.TryParse(
            core.CommandIn(
                dir,
                [ "issues"
                  "list"
                  "--state"
                  options.State.Flag
                  "--limit"
                  string options.Limit
                  "--fields"
                  ISSUE_FIELDS
                  "--output"
                  "csv" ]
            ),
            GiteaParse.parseIssueList
        )

    /// A single issue by number. `tea` 0.9.2's bare-index view (`tea issues <number>`) renders
    /// a human-readable Markdown page and ignores `--output`, so there is no structured detail
    /// read. This synthesizes one exactly like `PrView`: it **lists** all states and **pages**
    /// (`tea issues list --state all --limit 50 --page N …`) until #number is found or a page
    /// returns empty (past the end).
    member _.IssueView(dir: string, number: uint64) =
        task {
            let limit = string ISSUE_VIEW_PAGE_SIZE
            let mutable found: Result<Issue, ProcessError> option = None
            let mutable page = 1

            while Option.isNone found && page <= ISSUE_VIEW_MAX_PAGES do
                let cmd =
                    core.CommandIn(
                        dir,
                        [ "issues"
                          "list"
                          "--state"
                          "all"
                          "--limit"
                          limit
                          "--page"
                          string page
                          "--fields"
                          ISSUE_FIELDS
                          "--output"
                          "csv" ]
                    )

                match! core.TryParse(cmd, GiteaParse.parseIssueList) with
                | Error e -> found <- Some(Error e)
                | Ok issues ->
                    match issues |> List.tryFind (fun issue -> issue.Number = number) with
                    | Some issue -> found <- Some(Ok issue)
                    | None when List.isEmpty issues ->
                        // An empty page means we walked past the last issue — a genuine absence.
                        found <-
                            Some(Error(ProcessError.Parse(BINARY, sprintf "no issue #%d in `tea issues list`" number)))
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
                                "issue #%d not found in the first %d of `tea issues list` (stopped at the %d-page safety bound; query `tea`/the Gitea API directly for a repository this large)"
                                number
                                (ISSUE_VIEW_MAX_PAGES * ISSUE_VIEW_PAGE_SIZE)
                                ISSUE_VIEW_MAX_PAGES
                        )
                    )
        }

    /// Open an issue, returning the command's textual output (`tea issues create
    /// --title <t> --description <d>`). Like `PrCreate`, `tea` prints a summary (with
    /// the new issue's URL on the final line), not a bare URL — returned verbatim.
    member _.IssueCreate(dir: string, title: string, body: string) =
        core.Run(core.CommandIn(dir, [ "issues"; "create"; "--title"; title; "--description"; body ]))

    /// Close an issue without further comment (`tea issues close <number>`).
    member _.IssueClose(dir: string, number: uint64) =
        core.RunUnit(core.CommandIn(dir, [ "issues"; "close"; string number ]))

    /// Reopening issues is unsupported by `tea` 0.9.2, which exposes only issue list/create
    /// subcommands; refuses structurally before any spawn.
    member _.IssueReopen(_dir: string, _number: uint64) =
        task {
            let refusal: Result<unit, ProcessError> =
                Error(ProcessError.Spawn(BINARY, "tea 0.9.2 has no `issues reopen` command"))

            return refusal
        }

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

    /// Releases for `dir` (`tea releases list --limit 100 --output csv`). Up to 100. tea 0.9.2
    /// does not support `--output json` on `releases list` (K-049), so this drives its
    /// supported `--output csv` (`outputdsv`) format, parsed positionally by tea's fixed
    /// release columns. There is intentionally no `ReleaseView`: `tea releases` takes no
    /// positional and always lists, so a single-release-by-tag view does not exist in `tea`.
    member _.ReleaseList(dir: string) =
        core.TryParse(
            core.CommandIn(dir, [ "releases"; "list"; "--limit"; "100"; "--output"; "csv" ]),
            GiteaParse.parseReleaseList
        )

    /// Create a release, returning the command's textual output (`tea release create --tag
    /// <tag> [--title …] [--note …] [--draft] [--prerelease]`). Tag/title/note are all flag
    /// values, consumed verbatim. Unlike gh/glab, `tea` prints a summary, not a bare URL. See
    /// `ReleaseCreate`.
    member _.ReleaseCreate(dir: string, spec: ReleaseCreate) =
        let args =
            [ "release"; "create"; "--tag"; spec.Tag ]
            @ (match spec.Title with
               | Some t -> [ "--title"; t ]
               | None -> [])
            @ (match spec.Notes with
               | Some n -> [ "--note"; n ]
               | None -> [])
            @ (if spec.Draft then [ "--draft" ] else [])
            @ (if spec.Prerelease then [ "--prerelease" ] else [])

        core.Run(core.CommandIn(dir, args))

    /// Deleting releases is unsupported by `tea` 0.9.2, which exposes only release creation;
    /// refuses structurally before any spawn.
    member _.ReleaseDelete(_dir: string, _tag: string) =
        task {
            let refusal: Result<unit, ProcessError> =
                Error(ProcessError.Spawn(BINARY, "tea 0.9.2 has no `release delete` command"))

            return refusal
        }

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

    /// Whether at least one login is configured (`tea login list --output csv`).
    member _.AuthStatus() = gitea.AuthStatus()

    // --- Modelled methods (dir injected as the first argument) ----------------

    /// Open pull requests for the bound `dir` (`tea pr list …`) — the previous,
    /// options-less behaviour. Kept as a genuine zero-argument overload for CLR binary
    /// compatibility (see `Gitea.PrList`'s doc comment for the rationale).
    member _.PrList() = gitea.PrList dir

    /// Open pull requests for the bound `dir`, filtered and capped by `options`.
    member _.PrList(options: PrListOptions) = gitea.PrList(dir, options)

    /// A single pull request by number (synthesized via `tea pr list --state all …`).
    member _.PrView(number: uint64) = gitea.PrView(dir, number)

    /// Open a pull request (`tea pr create`).
    member _.PrCreate(spec: PrCreate) = gitea.PrCreate(dir, spec)

    /// Merge a pull request (`tea pr merge --style … <n>`; see `Gitea.PrMerge` for why the
    /// flag must precede the index).
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

    /// Editing a PR's title/description is **unsupported on `tea` 0.9.2** (no `pr edit`
    /// command); refuses structurally before any spawn — see the dir-bound `PrEdit` (K-063).
    member _.PrEdit(number: uint64, edit: PrEdit) = gitea.PrEdit(dir, number, edit)

    /// Open issues for the bound `dir` (`tea issues list …`) — the previous, options-less
    /// behaviour. Kept as a genuine zero-argument overload for CLR binary compatibility
    /// (see `Gitea.PrList`'s doc comment for the rationale).
    member _.IssueList() = gitea.IssueList dir

    /// Open issues for the bound `dir`, filtered and capped by `options`.
    member _.IssueList(options: IssueListOptions) = gitea.IssueList(dir, options)

    /// A single issue by number (synthesized via `tea issues list --state all …`).
    member _.IssueView(number: uint64) = gitea.IssueView(dir, number)

    /// Open an issue (`tea issues create …`).
    member _.IssueCreate(title: string, body: string) = gitea.IssueCreate(dir, title, body)

    /// Close an issue (`tea issues close <index>`).
    member _.IssueClose(number: uint64) = gitea.IssueClose(dir, number)

    /// Reopening issues is unsupported by `tea` 0.9.2.
    member _.IssueReopen(number: uint64) = gitea.IssueReopen(dir, number)

    /// Add a comment to an issue (`tea comment <index> <body>`).
    member _.IssueComment(number: uint64, body: string) = gitea.IssueComment(dir, number, body)

    /// Releases for the bound `dir` (`tea releases list …`).
    member _.ReleaseList() = gitea.ReleaseList dir

    /// Create a release (`tea release create --tag <tag> …`).
    member _.ReleaseCreate(spec: ReleaseCreate) = gitea.ReleaseCreate(dir, spec)

    /// Deleting releases is unsupported by `tea` 0.9.2.
    member _.ReleaseDelete(tag: string) = gitea.ReleaseDelete(dir, tag)
