namespace VcsToolkit.GitHub

open System
open ProcessKit
open VcsToolkit.CliSupport

/// gh-specific command shaping shared by the client's methods.
[<AutoOpen>]
module private GitHubHelpers =

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

/// The real GitHub client: typed async methods that run the real `gh`, ask it for
/// `--json`, and deserialize the result. `GitHub.Create()` uses the job-backed
/// runner; `GitHub.WithRunner` injects a fake one for tests. Wraps a `ManagedClient`.
///
/// By default it authenticates through `gh`'s own ambient login; attach a credential
/// provider with `WithCredentials`/`WithToken`/`WithEnvToken` to supply a token per
/// operation — it is injected as `GH_TOKEN` on every `gh` invocation (never in argv).
///
/// Injection safety: the methods that place a caller value in a bare positional slot
/// (`api` endpoint, release `tag`) reject an empty or `-`-leading value before
/// spawning. Flag-value slots (`--body`, `--branch`, …) are consumed verbatim.
[<Sealed>]
type GitHub private (core: ManagedClient) =

    /// Create a client driving the real job-backed runner.
    static member Create() =
        GitHub(ManagedClient.Create(BINARY).WithTokenEnv(CredentialService.GitHub, "GH_TOKEN"))

    /// Create a client driving `runner` — inject a fake in tests.
    static member WithRunner(runner: IProcessRunner) =
        GitHub(ManagedClient.WithRunner(BINARY, runner).WithTokenEnv(CredentialService.GitHub, "GH_TOKEN"))

    // --- Configuration (chainable; each returns a new client) ----------------

    /// Apply a default timeout to every command this client builds.
    member _.DefaultTimeout(timeout: TimeSpan) = GitHub(core.DefaultTimeout timeout)

    /// Set an environment variable on every command this client builds.
    member _.DefaultEnv(key: string, value: string) = GitHub(core.DefaultEnv(key, value))

    /// Remove an inherited environment variable on every command this client builds.
    member _.DefaultEnvRemove(key: string) = GitHub(core.DefaultEnvRemove key)

    /// Cancel every command this client builds when `token` fires.
    member _.DefaultCancelOn(token: Threading.CancellationToken) = GitHub(core.DefaultCancelOn token)

    /// Retry lock-contention failures per `policy` (opt-in, off by default).
    member _.WithRetry(policy: RetryPolicy) = GitHub(core.WithRetry policy)

    /// Supply credentials per operation via a provider — opt-in, off by default
    /// (ambient `gh` auth). The resolved token is injected as `GH_TOKEN`.
    member _.WithCredentials(provider: ICredentialProvider) = GitHub(core.WithCredentials provider)

    /// Authenticate with a single static `token`, injected as `GH_TOKEN`.
    member this.WithToken(token: string) =
        this.WithCredentials(StaticCredential.Token token :> ICredentialProvider)

    /// Read the token from environment variable `var` at request time (injected as
    /// `GH_TOKEN`); if unset/empty, fall back to ambient auth.
    member this.WithEnvToken(var: string) =
        this.WithCredentials(EnvToken var :> ICredentialProvider)

    // Bind this client to a directory with `At(dir)` (a `GitHubAt` view whose modelled methods
    // drop the leading `dir` argument); see the `GitHubAt` type below.

    // --- Escape hatches / version / auth -------------------------------------

    /// Run `gh <args>` in the current directory, returning trimmed stdout. Unguarded
    /// — never forward untrusted argv (gh aliases/extensions and `gh api` can reach
    /// code execution).
    member _.Run(args: string seq) = core.Run(core.Command args)

    /// Like `Run` but never errors on a non-zero exit — returns the captured result.
    member _.RunRaw(args: string seq) = core.Output(core.Command args)

    /// Installed GitHub CLI version (`gh --version`).
    member _.Version() = core.Run(core.Command [ "--version" ])

    /// The installed binary's parsed version, as `GitHubCapabilities`. An unrecognisable
    /// `gh --version` banner is a `Parse` error (never a throw) — the predictable
    /// degradation for a non-standard version string.
    member this.Capabilities() =
        task {
            match! this.Version() with
            | Error e -> return Error e
            | Ok raw ->
                match GitHubParse.parseVersion raw with
                | Some v -> return Ok { Version = v }
                | None ->
                    return Error(ProcessError.Parse(BINARY, sprintf "unrecognisable `gh --version` output: \"%s\"" raw))
        }

    /// Whether the user is authenticated (`gh auth status` exits zero). Any non-zero
    /// exit reads as `false`; only a spawn failure or timeout errors.
    member _.AuthStatus() =
        task {
            match! core.ExitCode(core.Command [ "auth"; "status" ]) with
            | Error e -> return Error e
            | Ok code -> return Ok(code = 0)
        }

    /// Raw GitHub REST/GraphQL response body (`gh api <endpoint>`), run in the bound repo
    /// `dir` so a relative endpoint's `{owner}/{repo}` placeholder resolves against *that*
    /// repository's remote rather than whatever repo the process cwd happens to be in.
    member _.Api(dir: string, endpoint: string) =
        task {
            match checkFlags [ "endpoint", endpoint ] with
            | Error e -> return Error e
            | Ok() -> return! core.Run(core.CommandIn(dir, [ "api"; endpoint ]))
        }

    // --- Repo / lists --------------------------------------------------------

    /// The repository for `dir` (`gh repo view --json …`).
    member _.RepoView(dir: string) =
        core.TryParse(core.CommandIn(dir, [ "repo"; "view"; "--json"; REPO_FIELDS ]), GitHubParse.parseRepo)

    /// Pull requests for `dir` (`gh pr list --limit 100 --json …`). Up to 100 open PRs.
    member _.PrList(dir: string) =
        core.TryParse(
            core.CommandIn(dir, [ "pr"; "list"; "--limit"; "100"; "--json"; PR_FIELDS ]),
            GitHubParse.parsePrList
        )

    /// Pull requests that merge `head` into `base`, in any state (`--state all`).
    member _.PrListForBranch(dir: string, head: string, baseBranch: string) =
        core.TryParse(
            core.CommandIn(
                dir,
                [ "pr"
                  "list"
                  "--head"
                  head
                  "--base"
                  baseBranch
                  "--state"
                  "all"
                  "--limit"
                  "100"
                  "--json"
                  PR_FIELDS ]
            ),
            GitHubParse.parsePrList
        )

    /// A single pull request by number (`gh pr view <n> --json …`).
    member _.PrView(dir: string, number: uint64) =
        core.TryParse(core.CommandIn(dir, [ "pr"; "view"; string number; "--json"; PR_FIELDS ]), GitHubParse.parsePr)

    /// Issues for `dir` (`gh issue list --limit 100 --json …`). Up to 100 open issues.
    member _.IssueList(dir: string) =
        core.TryParse(
            core.CommandIn(dir, [ "issue"; "list"; "--limit"; "100"; "--json"; ISSUE_LIST_FIELDS ]),
            GitHubParse.parseIssueList
        )

    /// Open a pull request, returning its URL (`gh pr create`). See `PrCreate`.
    member _.PrCreate(dir: string, spec: PrCreate) =
        let args =
            [ "pr"; "create"; "--title"; spec.Title; "--body"; spec.Body ]
            @ (match spec.Head with
               | Some h -> [ "--head"; h ]
               | None -> [])
            @ (match spec.Base with
               | Some b -> [ "--base"; b ]
               | None -> [])

        core.Run(core.CommandIn(dir, args))

    // --- PR lifecycle --------------------------------------------------------

    /// Merge a pull request (`gh pr merge <n> --merge|--squash|--rebase …`). See `PrMerge`.
    member _.PrMerge(dir: string, number: uint64, merge: PrMerge) =
        let args =
            [ "pr"; "merge"; string number; merge.Strategy.Flag ]
            @ (if merge.Auto then [ "--auto" ] else [])
            @ (if merge.DeleteBranch then [ "--delete-branch" ] else [])

        core.RunUnit(core.CommandIn(dir, args))

    /// Mark a draft pull request as ready for review (`gh pr ready <n>`).
    member _.PrMarkReady(dir: string, number: uint64) =
        core.RunUnit(core.CommandIn(dir, [ "pr"; "ready"; string number ]))

    /// Close a pull request without merging (`gh pr close <n> [--delete-branch]`).
    member _.PrClose(dir: string, number: uint64, deleteBranch: bool) =
        let args =
            [ "pr"; "close"; string number ]
            @ (if deleteBranch then [ "--delete-branch" ] else [])

        core.RunUnit(core.CommandIn(dir, args))

    /// Check out a pull request's branch locally in `dir` (`gh pr checkout <n>`): fetch the
    /// PR's head branch and switch the working tree to it. A local-worktree mutation (it
    /// changes `dir`'s checked-out branch), so it returns unit like the other lifecycle
    /// mutations. The number is a positional but is always digits (`uint64`), so no
    /// injection guard is needed.
    member _.PrCheckout(dir: string, number: uint64) =
        core.RunUnit(core.CommandIn(dir, [ "pr"; "checkout"; string number ]))

    /// The PR's checks (`gh pr checks <n> --json …`). gh signals the overall outcome
    /// through its exit code (0 all passed, 8 still pending, 1 some failed) and emits
    /// the same JSON either way, so all three return the parsed list; branch on each
    /// entry's `Bucket`. A PR with no checks yields an empty list. Any other exit errors.
    member _.PrChecks(dir: string, number: uint64) =
        task {
            match! core.Output(core.CommandIn(dir, [ "pr"; "checks"; string number; "--json"; CHECK_FIELDS ])) with
            | Error e -> return Error e
            | Ok res ->
                match res.Code with
                | Some 0 -> return mapParse (GitHubParse.parseChecks res.Stdout)
                | Some 1
                | Some 8 when res.Stdout.Trim() <> "" -> return mapParse (GitHubParse.parseChecks res.Stdout)
                // gh exits 1 with NO JSON for a PR that simply has no checks — the one
                // bare non-zero we read as an empty list (matched case-insensitively so
                // a capitalization tweak in gh's wording doesn't turn it into an error).
                | _ when res.Stderr.Contains("no checks reported", StringComparison.OrdinalIgnoreCase) -> return Ok []
                | _ ->
                    match ProcessResult.ensureSuccess res with
                    | Error e -> return Error e
                    | Ok _ -> return Ok [] // unreachable: a non-zero exit always errors above.
        }

    /// Submit a review (`gh pr review <n> --approve|--request-changes|--comment …`).
    /// See `ReviewAction` (request-changes/comment carry a required body).
    member _.PrReview(dir: string, number: uint64, action: ReviewAction) =
        let kindFlag =
            match action.Kind with
            | ReviewKind.Approve -> "--approve"
            | ReviewKind.RequestChanges -> "--request-changes"
            | ReviewKind.Comment -> "--comment"

        let args =
            [ "pr"; "review"; string number; kindFlag ]
            @ (match action.Body with
               | Some b -> [ "--body"; b ]
               | None -> [])

        core.RunUnit(core.CommandIn(dir, args))

    /// Add a conversation comment, returning its URL (`gh pr comment <n> --body <body>`).
    member _.PrComment(dir: string, number: uint64, body: string) =
        // `--body` is mandatory: without it gh falls back to an interactive prompt,
        // which would hang a headless run.
        core.Run(core.CommandIn(dir, [ "pr"; "comment"; string number; "--body"; body ]))

    /// Edit a pull request's title and/or body (`gh pr edit <n> [--title …] [--body …]`).
    /// At least one of `Title`/`Body` must be `Some` — both-`None` is refused before
    /// spawning. An empty string is a real value (gh clears the field).
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
                       | Some b -> [ "--body"; b ]
                       | None -> [])

                return! core.RunUnit(core.CommandIn(dir, args))
        }

    /// The PR's submitted reviews and conversation comments
    /// (`gh pr view <n> --json reviews,comments`).
    member _.PrFeedback(dir: string, number: uint64) =
        core.TryParse(
            core.CommandIn(dir, [ "pr"; "view"; string number; "--json"; "reviews,comments" ]),
            GitHubParse.parseFeedback
        )

    // --- Actions runs --------------------------------------------------------

    /// Recent workflow runs, newest first (`gh run list --limit <n> [--branch <b>] --json …`).
    /// `limit` is an `int` result cap, matching the count parameters on `Git.Log`/`Jj.OpLog`.
    member _.RunList(dir: string, limit: int, branch: string option) =
        let args =
            [ "run"; "list"; "--limit"; string limit ]
            @ (match branch with
               | Some b -> [ "--branch"; b ]
               | None -> [])
            @ [ "--json"; RUN_FIELDS ]

        core.TryParse(core.CommandIn(dir, args), GitHubParse.parseRunList)

    /// A single workflow run by id (`gh run view <id> --json …`).
    member _.RunView(dir: string, id: uint64) =
        core.TryParse(core.CommandIn(dir, [ "run"; "view"; string id; "--json"; RUN_FIELDS ]), GitHubParse.parseRun)

    /// Block until the run finishes, then return its final state (`gh run watch <id>`,
    /// then a `run view`). Inspect `Conclusion` for the outcome. Blocks for the whole
    /// run; a client `DefaultTimeout` kills the watch when it elapses (`Timeout`).
    member this.RunWatch(dir: string, id: uint64) =
        task {
            // `--exit-status` is deliberately NOT passed: it would map the run's
            // outcome onto the exit code, which can't be reported faithfully — the
            // follow-up `run view`'s `Conclusion` can. `ensureSuccess` surfaces a
            // killed watch as `Timeout` instead of reading a half-finished run.
            //
            // R5: `gh run watch` re-prints the full job table every few seconds, so over a
            // multi-hour run its (discarded) stdout would grow to tens of MB in memory. Bound the
            // capture to the last 256 lines / 256 KiB — we only need the tail's success/kill.
            let cmd =
                (core.CommandIn(dir, [ "run"; "watch"; string id ]))
                    .OutputBuffer(OutputBufferPolicy.Bounded(256).WithMaxBytes(256 * 1024))

            match! core.Output cmd with
            | Error e -> return Error e
            | Ok res ->
                match ProcessResult.ensureSuccess res with
                | Error e -> return Error e
                | Ok _ -> return! this.RunView(dir, id)
        }

    // --- Issues / releases ---------------------------------------------------

    /// Open an issue, returning its URL (`gh issue create --title <title> --body <body>`).
    member _.IssueCreate(dir: string, title: string, body: string) =
        core.Run(core.CommandIn(dir, [ "issue"; "create"; "--title"; title; "--body"; body ]))

    /// A single issue by number, with `Body`/`Url` filled (`gh issue view <n> --json …`).
    member _.IssueView(dir: string, number: uint64) =
        core.TryParse(
            core.CommandIn(dir, [ "issue"; "view"; string number; "--json"; ISSUE_VIEW_FIELDS ]),
            GitHubParse.parseIssue
        )

    /// Releases, newest first (`gh release list --limit 100 --json …`). `Body`/`Url`
    /// are not fetched here — use `ReleaseView`. Up to 100 releases.
    member _.ReleaseList(dir: string) =
        core.TryParse(
            core.CommandIn(dir, [ "release"; "list"; "--limit"; "100"; "--json"; RELEASE_LIST_FIELDS ]),
            GitHubParse.parseReleaseList
        )

    /// A single release by tag, with `Body`/`Url` filled (`gh release view <tag> --json …`).
    /// `IsLatest` is reported only by `ReleaseList`; here it defaults to `false`.
    member _.ReleaseView(dir: string, tag: string) =
        task {
            match checkFlags [ "tag", tag ] with
            | Error e -> return Error e
            | Ok() ->
                return!
                    core.TryParse(
                        core.CommandIn(dir, [ "release"; "view"; tag; "--json"; RELEASE_VIEW_FIELDS ]),
                        GitHubParse.parseRelease
                    )
        }

    /// A view of this client bound to repository `dir`: modelled methods drop their leading
    /// `dir` argument. The raw `Run`/`RunRaw` hatches stay process-cwd.
    member this.At(dir: string) : GitHubAt = GitHubAt(this, dir)

/// A view of a `GitHub` client bound to a repository `dir`. Every modelled method drops the
/// leading `dir` argument and injects the bound one, so `at.PrList()` is `github.PrList dir`
/// and `at.Api(endpoint)` is `github.Api(dir, endpoint)`. The raw `Run`/`RunRaw` escape hatches
/// are deliberately NOT rebound: like the client's, they run in the process's current working
/// directory, not the bound `dir`.
and [<Sealed>] GitHubAt internal (github: GitHub, dir: string) =

    // --- Escape hatches / version / auth (process-cwd, NOT the bound dir) -----

    /// Run `gh <args>` in the process's current directory (NOT the bound `dir`). Unguarded.
    member _.Run(args: string seq) = github.Run args

    /// Like `Run` but never errors on a non-zero exit. Process-cwd, NOT the bound `dir`.
    member _.RunRaw(args: string seq) = github.RunRaw args

    /// Installed GitHub CLI version (`gh --version`).
    member _.Version() = github.Version()

    /// The installed binary's parsed version, as `GitHubCapabilities`.
    member _.Capabilities() = github.Capabilities()

    /// Whether the user is authenticated (`gh auth status` exits zero).
    member _.AuthStatus() = github.AuthStatus()

    // --- Modelled methods (dir injected as the first argument) ----------------

    /// Raw GitHub REST/GraphQL response body for the bound `dir` (`gh api <endpoint>`).
    member _.Api(endpoint: string) = github.Api(dir, endpoint)

    /// The repository for the bound `dir` (`gh repo view --json …`).
    member _.RepoView() = github.RepoView dir

    /// Pull requests for the bound `dir` (`gh pr list …`).
    member _.PrList() = github.PrList dir

    /// Pull requests that merge `head` into `baseBranch`, any state.
    member _.PrListForBranch(head: string, baseBranch: string) =
        github.PrListForBranch(dir, head, baseBranch)

    /// A single pull request by number (`gh pr view <n> --json …`).
    member _.PrView(number: uint64) = github.PrView(dir, number)

    /// Issues for the bound `dir` (`gh issue list …`).
    member _.IssueList() = github.IssueList dir

    /// Open a pull request, returning its URL (`gh pr create`).
    member _.PrCreate(spec: PrCreate) = github.PrCreate(dir, spec)

    /// Merge a pull request (`gh pr merge <n> …`).
    member _.PrMerge(number: uint64, merge: PrMerge) = github.PrMerge(dir, number, merge)

    /// Mark a draft pull request as ready for review (`gh pr ready <n>`).
    member _.PrMarkReady(number: uint64) = github.PrMarkReady(dir, number)

    /// Close a pull request without merging (`gh pr close <n> [--delete-branch]`).
    member _.PrClose(number: uint64, deleteBranch: bool) =
        github.PrClose(dir, number, deleteBranch)

    /// Check out a pull request's branch locally (`gh pr checkout <n>`).
    member _.PrCheckout(number: uint64) = github.PrCheckout(dir, number)

    /// The PR's checks (`gh pr checks <n> --json …`).
    member _.PrChecks(number: uint64) = github.PrChecks(dir, number)

    /// Submit a review (`gh pr review <n> …`).
    member _.PrReview(number: uint64, action: ReviewAction) = github.PrReview(dir, number, action)

    /// Add a conversation comment, returning its URL (`gh pr comment <n> --body …`).
    member _.PrComment(number: uint64, body: string) = github.PrComment(dir, number, body)

    /// Edit a pull request's title and/or body (`gh pr edit <n> …`).
    member _.PrEdit(number: uint64, edit: PrEdit) = github.PrEdit(dir, number, edit)

    /// The PR's submitted reviews and conversation comments (`gh pr view <n> …`).
    member _.PrFeedback(number: uint64) = github.PrFeedback(dir, number)

    /// Recent workflow runs, newest first (`gh run list …`).
    member _.RunList(limit: int, branch: string option) = github.RunList(dir, limit, branch)

    /// A single workflow run by id (`gh run view <id> --json …`).
    member _.RunView(id: uint64) = github.RunView(dir, id)

    /// Block until the run finishes, then return its final state (`gh run watch <id>`).
    member _.RunWatch(id: uint64) = github.RunWatch(dir, id)

    /// Open an issue, returning its URL (`gh issue create …`).
    member _.IssueCreate(title: string, body: string) = github.IssueCreate(dir, title, body)

    /// A single issue by number (`gh issue view <n> --json …`).
    member _.IssueView(number: uint64) = github.IssueView(dir, number)

    /// Releases, newest first (`gh release list …`).
    member _.ReleaseList() = github.ReleaseList dir

    /// A single release by tag (`gh release view <tag> --json …`).
    member _.ReleaseView(tag: string) = github.ReleaseView(dir, tag)
