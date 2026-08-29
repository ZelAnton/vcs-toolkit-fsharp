namespace VcsToolkit.GitHub

open System
open ProcessKit
open VcsToolkit.CliSupport
open VcsToolkit.Diff

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

    static let CompleteInventoryLimit = 1000

    static let validateLabels (operation: string) (labels: string list) : Result<unit, ProcessError> =
        if List.isEmpty labels || labels |> List.exists String.IsNullOrWhiteSpace then
            Error(ProcessError.Spawn(BINARY, sprintf "%s requires at least one non-empty label" operation))
        else
            Ok()

    static let validateWorkflowInputKey (key: string) : Result<unit, ProcessError> =
        if String.IsNullOrEmpty key || key.IndexOf('=') >= 0 || key.IndexOf(char 0) >= 0 then
            Error(ProcessError.Spawn(BINARY, "workflow input key must be non-empty and contain neither '=' nor NUL"))
        else
            Ok()

    static let validateWorkflowInputKeys (inputs: (string * string) list) : Result<unit, ProcessError> =
        match
            inputs
            |> List.tryPick (fun (key, _) ->
                match validateWorkflowInputKey key with
                | Error e -> Some e
                | Ok() -> None)
        with
        | Some e -> Error e
        | None -> Ok()

    static let validateWorkflowListLimit (limit: int) : Result<unit, ProcessError> =
        if limit <= 0 then
            Error(ProcessError.Spawn(BINARY, "workflow list limit must be greater than zero"))
        else
            Ok()

    static let resolveWorkflow (workflows: Workflow list) (selector: string) : Result<Workflow, ProcessError> =
        if String.IsNullOrEmpty selector then
            Error(ProcessError.Spawn(BINARY, "workflow view selector must not be empty"))
        else
            let selectorLower = selector.ToLowerInvariant()

            let isNumeric, numericId =
                match
                    UInt64.TryParse(
                        selector,
                        Globalization.NumberStyles.None,
                        Globalization.CultureInfo.InvariantCulture
                    )
                with
                | true, value -> true, Some value
                | false, _ -> false, None

            let isFile = selectorLower.EndsWith(".yml") || selectorLower.EndsWith(".yaml")

            let matches =
                workflows
                |> List.filter (fun workflow ->
                    if isNumeric then
                        numericId |> Option.exists ((=) workflow.Id)
                    elif isFile then
                        let lastSlash = workflow.Path.LastIndexOf('/')

                        let fileName =
                            if lastSlash >= 0 then
                                workflow.Path.Substring(lastSlash + 1)
                            else
                                workflow.Path

                        workflow.Path = selector || fileName = selector
                    else
                        String.Equals(workflow.Name, selector, StringComparison.OrdinalIgnoreCase))

            match matches with
            | [ workflow ] -> Ok workflow
            | [] -> Error(ProcessError.Parse(BINARY, sprintf "could not find workflow %A" selector))
            | _ ->
                Error(
                    ProcessError.Parse(
                        BINARY,
                        sprintf "workflow selector %A is ambiguous (%d matches)" selector matches.Length
                    )
                )

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

    /// Attach a diagnostic observer notified as each `gh` command starts and finishes
    /// (opt-in, off by default). See `ICommandObserver`.
    member _.WithObserver(observer: ICommandObserver) = GitHub(core.WithObserver observer)

    /// Bound retained process output for callers such as MCP that impose a response budget.
    member _.WithOutputBudget(bytes: int option) = GitHub(core.WithOutputBudget bytes)

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

    /// Bind this client to a GitHub `host`, so a supplied credential is injected into the
    /// environment variable `gh` reads for **that** host and `GH_HOST` pins gh's default
    /// host:
    ///
    /// - **github.com** (`GitHubHost.GitHubCom`) → the token goes to `GH_TOKEN` (the SaaS
    ///   default, unchanged) and `GH_HOST` is `github.com`.
    /// - a **GitHub Enterprise Server** host → the token goes to `GH_ENTERPRISE_TOKEN` (the
    ///   variable `gh` uses for a non-github.com host) and `GH_HOST` is that host, so gh's
    ///   non-repo commands resolve against it. The github.com `GH_TOKEN` is **not** set, so
    ///   an enterprise secret never lands in the github.com token env (nor vice versa).
    ///
    /// Compose with `WithCredentials`/`WithToken`/`WithEnvToken` in either order — the host
    /// selects the env var, the provider supplies the secret. The bound host also travels
    /// in each operation's `CredentialRequest`, so a **host-keyed** provider returns the
    /// secret for *this* host and never a neighbouring instance's. For several hosts, build
    /// **one client per host**: each injects only its own host's token, so a broken or
    /// missing credential for one host can't leak into another. Without a host binding the
    /// client behaves exactly as before — github.com semantics, credential injected as
    /// `GH_TOKEN`. (`GH_HOST` only steers gh's host inference for commands with **no**
    /// repository context; a repo-scoped method still resolves its host from the bound
    /// directory's remote, so point a host-bound client at repositories on that host.)
    member _.WithHost(host: GitHubHost) =
        GitHub(
            core
                .WithTokenEnv(CredentialService.GitHub, host.TokenEnvVar)
                .WithExpectedHost(host.Host)
                .DefaultEnv("GH_HOST", host.Host)
        )

    // Bind this client to a directory with `At(dir)` (a `GitHubAt` view whose modelled methods
    // drop the leading `dir` argument); see the `GitHubAt` type below.

    // --- Escape hatches / version / auth -------------------------------------

    /// Run `gh <args>` in the process's current directory, returning trimmed stdout. Unguarded
    /// — never forward untrusted argv (gh aliases/extensions and `gh api` can reach code
    /// execution). For an ad-hoc command scoped to a repository, use the `dir`-taking overload
    /// (`Run(dir, args)`) or a bound view's `at(dir).Run(args)`.
    member _.Run(args: string seq) = core.Run(core.Command args)

    /// Run `gh <args>` in `dir`, returning trimmed stdout — the `dir`-bound counterpart of
    /// `Run(args)` (which runs in the process cwd). Backs `GitHubAt.Run`. Equally unguarded.
    member _.Run(dir: string, args: string seq) = core.Run(core.CommandIn(dir, args))

    /// Like `Run` but never errors on a non-zero exit — returns the captured result.
    member _.RunRaw(args: string seq) = core.Output(core.Command args)

    /// Like `Run(dir, args)` but never errors on a non-zero exit — returns the captured
    /// result. Backs `GitHubAt.RunRaw`.
    member _.RunRaw(dir: string, args: string seq) = core.Output(core.CommandIn(dir, args))

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

    /// The login selected by this client's authentication context (`gh api user`).
    member _.AuthIdentity() =
        core.TryParse(core.Command [ "api"; "user" ], GitHubParse.parseUserLogin)

    /// Agent-only account proof pinned to the selected remote host.
    member internal _.AuthIdentityForHost(host: string) =
        task {
            match checkFlags BINARY [ "host", host ] with
            | Error error -> return Error error
            | Ok() ->
                return! core.TryParse(core.Command [ "api"; "--hostname"; host; "user" ], GitHubParse.parseUserLogin)
        }

    /// Whether the user is authenticated for `host` specifically (`gh auth status
    /// --hostname <host>` exits zero). Scoping the probe to one host means a broken session
    /// on a DIFFERENT host can't flip this false: the unscoped `AuthStatus` inspects every
    /// configured host and folds them together, so a single expired login there would read
    /// as a false negative for the host actually targeted. Any non-zero exit reads as
    /// `false`; only a spawn failure or timeout errors. `host` is a validated `GitHubHost`,
    /// so its `--hostname` value can never be flag-like or empty.
    member _.AuthStatusFor(host: GitHubHost) =
        task {
            match! core.ExitCode(core.Command [ "auth"; "status"; "--hostname"; host.Host ]) with
            | Error e -> return Error e
            | Ok code -> return Ok(code = 0)
        }

    /// Raw GitHub REST/GraphQL response body (`gh api <endpoint>`), run in the bound repo
    /// `dir` so a relative endpoint's `{owner}/{repo}` placeholder resolves against *that*
    /// repository's remote rather than whatever repo the process cwd happens to be in.
    member _.Api(dir: string, endpoint: string) =
        task {
            match checkFlags BINARY [ "endpoint", endpoint ] with
            | Error e -> return Error e
            | Ok() -> return! core.Run(core.CommandIn(dir, [ "api"; endpoint ]))
        }

    // --- Repo / lists --------------------------------------------------------

    /// The repository for `dir` (`gh repo view --json …`).
    member _.RepoView(dir: string) =
        core.TryParse(core.CommandIn(dir, [ "repo"; "view"; "--json"; REPO_FIELDS ]), GitHubParse.parseRepo)

    /// Pull requests for `dir` — the previous, options-less behaviour (open, up to 100).
    /// Kept as a genuine `(dir)`-only overload (rather than folding into `dir` plus an
    /// `?options` optional parameter) for CLR binary compatibility: F#'s `?options` sugar
    /// still compiles to a required parameter at the metadata level, so an already-compiled
    /// caller of the pre-`PrListOptions` `PrList(dir)` would hit `MissingMethodException`
    /// against a build that replaced it outright.
    member this.PrList(dir: string) = this.PrList(dir, PrListOptions.Default)

    /// Pull requests for `dir` (`gh pr list --state <state> --limit <limit> --json …`).
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
                  "--json"
                  PR_FIELDS ]
            ),
            GitHubParse.parsePrList
        )

    /// Pull requests that merge `head` into `base`, in any state (`--state all`). `head` and
    /// `baseBranch` land in `--head`/`--base` flag-value slots, but both are checked against
    /// `checkFlags` before spawning — a branch name is caller/repo-supplied, not a hardcoded
    /// literal like the other flag values in this file.
    member _.PrListForBranch(dir: string, head: string, baseBranch: string) =
        task {
            match checkFlags BINARY [ "head", head; "baseBranch", baseBranch ] with
            | Error e -> return Error e
            | Ok() ->
                return!
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
        }

    /// Pull requests whose head is `head`, in any state (`--state all`), against any base
    /// branch (`gh pr list --head <head> --state all --limit 100 --json …`). Prefer this
    /// two-argument form over `PrListForBranch(dir, head, baseBranch)` when the target
    /// branch isn't known — e.g. from `Forge.PrForBranch`, which is only given a source
    /// branch. `head` is checked against `checkFlags` before spawning, like the three-
    /// argument overload.
    member _.PrListForBranch(dir: string, head: string) =
        task {
            match checkFlags BINARY [ "head", head ] with
            | Error e -> return Error e
            | Ok() ->
                return!
                    core.TryParse(
                        core.CommandIn(
                            dir,
                            [ "pr"
                              "list"
                              "--head"
                              head
                              "--state"
                              "all"
                              "--limit"
                              "100"
                              "--json"
                              PR_FIELDS ]
                        ),
                        GitHubParse.parsePrList
                    )
        }

    /// Agent-only exact open PR inventory pinned to one selected repository. `gh` walks
    /// pages up to the requested limit; hitting the limit is not proof of completeness and
    /// therefore fails closed before a caller may create another PR.
    member internal _.PrListForBranchesComplete(dir: string, repository: string, head: string, baseBranch: string) =
        task {
            match checkFlags BINARY [ "repository", repository; "head", head; "baseBranch", baseBranch ] with
            | Error error -> return Error error
            | Ok() ->
                let! result =
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
                              "open"
                              "--limit"
                              string CompleteInventoryLimit
                              "--json"
                              RECOVERY_PR_FIELDS
                              "--repo"
                              repository ]
                        ),
                        GitHubParse.parseRecoveryPrList
                    )

                match result with
                | Error error -> return Error error
                | Ok pullRequests when pullRequests.Length >= CompleteInventoryLimit ->
                    return
                        Error(
                            ProcessError.Parse(
                                BINARY,
                                $"exact pull-request inventory reached the safety limit of {CompleteInventoryLimit}; completeness is not proven"
                            )
                        )
                | Ok pullRequests -> return Ok pullRequests
        }

    /// A single pull request by number (`gh pr view <n> --json …`).
    member _.PrView(dir: string, number: uint64) =
        core.TryParse(core.CommandIn(dir, [ "pr"; "view"; string number; "--json"; PR_FIELDS ]), GitHubParse.parsePr)

    /// The pull request's unified diff, parsed into per-file `FileDiff` values
    /// (`gh pr diff <n>`, then `VcsToolkit.Diff.parseDiff`). The number is a positional
    /// but is always digits (`uint64`), so no injection guard is needed.
    member _.PrDiff(dir: string, number: uint64) =
        task {
            match! runUntrimmed core (core.CommandIn(dir, [ "pr"; "diff"; string number ])) with
            | Error e -> return Error e
            | Ok raw -> return Ok(parseDiff raw)
        }

    /// Issues for `dir` — the previous, options-less behaviour (open, up to 100). Kept as a
    /// genuine `(dir)`-only overload for CLR binary compatibility (see `PrList`'s doc
    /// comment for the rationale).
    member this.IssueList(dir: string) =
        this.IssueList(dir, IssueListOptions.Default)

    /// Issues for `dir` (`gh issue list --state <state> --limit <limit> --json …`).
    member _.IssueList(dir: string, options: IssueListOptions) =
        core.TryParse(
            core.CommandIn(
                dir,
                [ "issue"
                  "list"
                  "--state"
                  options.State.Flag
                  "--limit"
                  string options.Limit
                  "--json"
                  ISSUE_LIST_FIELDS ]
            ),
            GitHubParse.parseIssueList
        )

    /// Active GitHub Actions workflow definitions for `dir`, up to 50.
    /// Disabled workflows are hidden by gh unless the options overload requests `--all`.
    member this.WorkflowList(dir: string) =
        this.WorkflowList(dir, WorkflowListOptions.Default)

    /// GitHub Actions workflow definitions for `dir` (`gh workflow list --limit <limit>
    /// [--all] --json id,name,path,state`). A non-positive limit is rejected before spawning.
    member _.WorkflowList(dir: string, options: WorkflowListOptions) =
        task {
            match validateWorkflowListLimit options.Limit with
            | Error e -> return Error e
            | Ok() ->
                let args =
                    [ "workflow"; "list"; "--limit"; string options.Limit ]
                    @ (if options.IncludeDisabled then [ "--all" ] else [])
                    @ [ "--json"; WORKFLOW_FIELDS ]

                return! core.TryParse(core.CommandIn(dir, args), GitHubParse.parseWorkflowList)
        }

    /// Resolve one workflow by numeric id, case-insensitive display name, filename, or
    /// repository-relative path. `gh workflow view` has no JSON mode, so this resolves
    /// against a complete disabled-inclusive workflow list and never scrapes human output.
    /// Missing or ambiguous selectors are structured parse errors; an empty selector is
    /// rejected before spawning.
    member this.WorkflowView(dir: string, selector: string) =
        task {
            if String.IsNullOrEmpty selector then
                return resolveWorkflow [] selector
            else
                let options =
                    WorkflowListOptions.Default.WithAll().WithLimit WORKFLOW_VIEW_LOOKUP_LIMIT

                match! this.WorkflowList(dir, options) with
                | Error e -> return Error e
                | Ok workflows -> return resolveWorkflow workflows selector
        }

    /// Open a pull request, returning its URL (`gh pr create`). See `PrCreate`.
    member _.PrCreate(dir: string, spec: PrCreate) =
        task {
            let labelCheck =
                if List.isEmpty spec.Labels then
                    Ok()
                else
                    validateLabels "pr create" spec.Labels

            match labelCheck with
            | Error e -> return Error e
            | Ok() ->
                let args =
                    [ "pr"; "create"; "--title"; spec.Title; "--body"; spec.Body ]
                    @ (match spec.Head with
                       | Some h -> [ "--head"; h ]
                       | None -> [])
                    @ (match spec.Base with
                       | Some b -> [ "--base"; b ]
                       | None -> [])
                    @ (spec.Labels |> List.collect (fun label -> [ "--label"; label ]))

                return! core.Run(core.CommandIn(dir, args))

        }

    /// Agent-only PR creation pinned to one selected repository.
    member internal _.PrCreateForRepository(dir: string, repository: string, spec: PrCreate) =
        task {
            match checkFlags BINARY [ "repository", repository ] with
            | Error error -> return Error error
            | Ok() ->
                let labelCheck =
                    if List.isEmpty spec.Labels then
                        Ok()
                    else
                        validateLabels "pr create" spec.Labels

                match labelCheck with
                | Error error -> return Error error
                | Ok() ->
                    let args =
                        [ "pr"; "create"; "--title"; spec.Title; "--body"; spec.Body ]
                        @ (match spec.Head with
                           | Some head -> [ "--head"; head ]
                           | None -> [])
                        @ (match spec.Base with
                           | Some baseBranch -> [ "--base"; baseBranch ]
                           | None -> [])
                        @ (spec.Labels |> List.collect (fun label -> [ "--label"; label ]))
                        @ [ "--repo"; repository ]

                    return! core.Run(core.CommandIn(dir, args))
        }

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
                | Some 0 -> return mapParse BINARY (GitHubParse.parseChecks res.Stdout)
                | Some 1
                | Some 8 when res.Stdout.Trim() <> "" -> return mapParse BINARY (GitHubParse.parseChecks res.Stdout)
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

    /// Add labels to an existing pull request (`gh pr edit <n> --add-label <name>`).
    member _.PrAddLabels(dir: string, number: uint64, labels: string list) =
        task {
            match validateLabels "pr add labels" labels with
            | Error e -> return Error e
            | Ok() ->
                let args =
                    [ "pr"; "edit"; string number ]
                    @ (labels |> List.collect (fun label -> [ "--add-label"; label ]))

                return! core.RunUnit(core.CommandIn(dir, args))
        }

    /// Remove labels from an existing pull request (`gh pr edit <n> --remove-label <name>`).
    member _.PrRemoveLabels(dir: string, number: uint64, labels: string list) =
        task {
            match validateLabels "pr remove labels" labels with
            | Error e -> return Error e
            | Ok() ->
                let args =
                    [ "pr"; "edit"; string number ]
                    @ (labels |> List.collect (fun label -> [ "--remove-label"; label ]))

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

    /// Workflow runs selected by one exact commit id (`gh run list --commit <sha>`).
    member _.RunListForRevision(dir: string, limit: int, revision: string) =
        task {
            if limit <= 0 then
                return Error(ProcessError.Spawn(BINARY, "run list limit must be positive"))
            else
                match checkFlags BINARY [ "revision", revision ] with
                | Error error -> return Error error
                | Ok() ->
                    return!
                        core.TryParse(
                            core.CommandIn(
                                dir,
                                [ "run"
                                  "list"
                                  "--limit"
                                  string limit
                                  "--commit"
                                  revision
                                  "--json"
                                  RUN_FIELDS ]
                            ),
                            GitHubParse.parseRunList
                        )
        }

    /// Agent-only complete exact-revision run inventory pinned to one selected repository.
    /// Hitting the safety limit fails closed rather than treating a partial list as success.
    member internal _.RunListForRevisionComplete(dir: string, repository: string, revision: string) =
        task {
            match checkFlags BINARY [ "repository", repository; "revision", revision ] with
            | Error error -> return Error error
            | Ok() ->
                let! result =
                    core.TryParse(
                        core.CommandIn(
                            dir,
                            [ "run"
                              "list"
                              "--limit"
                              string CompleteInventoryLimit
                              "--commit"
                              revision
                              "--json"
                              RUN_FIELDS
                              "--repo"
                              repository ]
                        ),
                        GitHubParse.parseRunList
                    )

                match result with
                | Error error -> return Error error
                | Ok runs when runs.Length >= CompleteInventoryLimit ->
                    return
                        Error(
                            ProcessError.Parse(
                                BINARY,
                                $"exact workflow-run inventory reached the safety limit of {CompleteInventoryLimit}; completeness is not proven"
                            )
                        )
                | Ok runs -> return Ok runs
        }

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

    /// Trigger a `workflow_dispatch` event for a workflow (`gh workflow run <workflow>
    /// [--ref <ref>] [--raw-field key=value …]`). See `WorkflowDispatch`.
    ///
    /// GitHub answers a dispatch with `204 No Content` — there is no run id to hand back, so
    /// this returns `Result<unit, _>`; poll `RunList` for the newly queued run instead. The
    /// workflow name/id is a bare positional, so an empty or `-`-leading value is refused before
    /// spawning (the same guard `ReleaseView` applies to a release tag). Input keys are also
    /// checked before spawning: they must be non-empty and contain neither `=` nor NUL. Input
    /// values are passed as **`--raw-field`, never `--field`**: `--field`'s value is subject to
    /// gh's `@`-syntax (a leading `@` reads a *local file* instead of taking the value literally),
    /// which would turn an input value the caller doesn't fully control into a local-file-disclosure
    /// vector; `--raw-field` always keeps the value a literal string.
    ///
    /// gh exit codes (checked against the installed gh 2.95.0 via `gh help exit-codes`, which
    /// documents these as the general codes across all commands, not dispatch-specific ones):
    /// `0` success; `1` any failure (unknown workflow, a workflow with no `workflow_dispatch`
    /// trigger, …); `2` cancelled; `4` not authenticated. No dispatch-specific classifier is
    /// added on top — the generic `isLockContention`/`isTransientFetchError` classifiers in
    /// `VcsToolkit.CliSupport.Classify` already reason over the `ProcessError.Exit` this surfaces,
    /// the same way they do for every other mutating command in this wrapper.
    member _.WorkflowDispatch(dir: string, spec: WorkflowDispatch) =
        task {
            match checkFlags BINARY [ "workflow", spec.Workflow ] with
            | Error e -> return Error e
            | Ok() ->
                match validateWorkflowInputKeys spec.Inputs with
                | Error e -> return Error e
                | Ok() ->
                    let args =
                        [ "workflow"; "run"; spec.Workflow ]
                        @ (match spec.Ref with
                           | Some r -> [ "--ref"; r ]
                           | None -> [])
                        @ (spec.Inputs
                           |> List.collect (fun (k, v) -> [ "--raw-field"; sprintf "%s=%s" k v ]))

                    return! core.RunUnit(core.CommandIn(dir, args))
        }

    /// Rerun a workflow run — the whole run, or only its failed jobs (`gh run rerun <id>
    /// [--failed]`). See `RerunScope`. The id is always digits (`uint64`), so no injection guard
    /// is needed.
    ///
    /// gh exit codes (gh 2.95.0): `0` success; `1` any failure (unknown run id, …); `2`
    /// cancelled; `4` not authenticated — the same general codes `WorkflowDispatch` documents,
    /// already reachable through the generic classifiers in `Classify.fs`.
    member _.RunRerun(dir: string, id: uint64, scope: RerunScope) =
        let args =
            [ "run"; "rerun"; string id ]
            @ (match scope with
               | RerunScope.FailedOnly -> [ "--failed" ]
               | RerunScope.All -> [])

        core.RunUnit(core.CommandIn(dir, args))

    /// Cancel a workflow run (`gh run cancel <id>`). The id is always digits (`uint64`), so no
    /// injection guard is needed.
    ///
    /// gh exit codes (gh 2.95.0): `0` success; `1` any failure (unknown run id, a run that
    /// already finished, …); `2` cancelled; `4` not authenticated — the same general codes
    /// `WorkflowDispatch` documents, already reachable through the generic classifiers in
    /// `Classify.fs`.
    member _.RunCancel(dir: string, id: uint64) =
        core.RunUnit(core.CommandIn(dir, [ "run"; "cancel"; string id ]))

    // --- Issues / releases ---------------------------------------------------

    /// Open an issue, returning its URL (`gh issue create --title <title> --body <body>`).
    member this.IssueCreate(dir: string, title: string, body: string) =
        this.IssueCreate(dir, IssueCreate.Create(title, body))

    /// Open an issue with optional labels (`gh issue create --label …`).
    member _.IssueCreate(dir: string, spec: IssueCreate) =
        task {
            let labelCheck =
                if List.isEmpty spec.Labels then
                    Ok()
                else
                    validateLabels "issue create" spec.Labels

            match labelCheck with
            | Error e -> return Error e
            | Ok() ->
                let args =
                    [ "issue"; "create"; "--title"; spec.Title; "--body"; spec.Body ]
                    @ (spec.Labels |> List.collect (fun label -> [ "--label"; label ]))

                return! core.Run(core.CommandIn(dir, args))
        }

    /// A single issue by number, with `Body`/`Url` filled (`gh issue view <n> --json …`).
    member _.IssueView(dir: string, number: uint64) =
        core.TryParse(
            core.CommandIn(dir, [ "issue"; "view"; string number; "--json"; ISSUE_VIEW_FIELDS ]),
            GitHubParse.parseIssue
        )

    /// Close an issue (`gh issue close <n>`). The number is always digits (`uint64`), so
    /// no injection guard is needed.
    member _.IssueClose(dir: string, number: uint64) =
        core.RunUnit(core.CommandIn(dir, [ "issue"; "close"; string number ]))

    /// Reopen an issue (`gh issue reopen <n>`). The number is always digits (`uint64`), so
    /// no injection guard is needed.
    member _.IssueReopen(dir: string, number: uint64) =
        core.RunUnit(core.CommandIn(dir, [ "issue"; "reopen"; string number ]))

    /// Add a comment to an issue, returning its URL (`gh issue comment <n> --body <body>`).
    /// By the `PrComment` pattern: `--body` is mandatory — without it gh falls back to an
    /// interactive prompt, which would hang a headless run.
    member _.IssueComment(dir: string, number: uint64, body: string) =
        core.Run(core.CommandIn(dir, [ "issue"; "comment"; string number; "--body"; body ]))

    /// Edit an issue's title and/or body (`gh issue edit <n> [--title …] [--body …]`).
    /// At least one field must be supplied; both-`None` is refused before spawning.
    member _.IssueEdit(dir: string, number: uint64, title: string option, body: string option) =
        task {
            match title, body with
            | None, None ->
                return Error(ProcessError.Spawn(BINARY, "issue edit requires at least a title or a body to change"))
            | _ ->
                let args =
                    [ "issue"; "edit"; string number ]
                    @ (match title with
                       | Some t -> [ "--title"; t ]
                       | None -> [])
                    @ (match body with
                       | Some b -> [ "--body"; b ]
                       | None -> [])

                return! core.RunUnit(core.CommandIn(dir, args))
        }

    /// Add labels to an existing issue (`gh issue edit <n> --add-label <name>`).
    member _.IssueAddLabels(dir: string, number: uint64, labels: string list) =
        task {
            match validateLabels "issue add labels" labels with
            | Error e -> return Error e
            | Ok() ->
                let args =
                    [ "issue"; "edit"; string number ]
                    @ (labels |> List.collect (fun label -> [ "--add-label"; label ]))

                return! core.RunUnit(core.CommandIn(dir, args))
        }

    /// Remove labels from an existing issue (`gh issue edit <n> --remove-label <name>`).
    member _.IssueRemoveLabels(dir: string, number: uint64, labels: string list) =
        task {
            match validateLabels "issue remove labels" labels with
            | Error e -> return Error e
            | Ok() ->
                let args =
                    [ "issue"; "edit"; string number ]
                    @ (labels |> List.collect (fun label -> [ "--remove-label"; label ]))

                return! core.RunUnit(core.CommandIn(dir, args))
        }

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
            match checkFlags BINARY [ "tag", tag ] with
            | Error e -> return Error e
            | Ok() ->
                return!
                    core.TryParse(
                        core.CommandIn(dir, [ "release"; "view"; tag; "--json"; RELEASE_VIEW_FIELDS ]),
                        GitHubParse.parseRelease
                    )
        }

    /// Create a release on `tag`, returning its URL (`gh release create <tag> [--title …]
    /// --notes … [--draft] [--prerelease]`). The tag lands in a bare positional slot, so an
    /// empty or `-`-leading value is refused before spawning; the `--title`/`--notes` values
    /// are consumed verbatim. `--notes` is always emitted (empty when unset) — like
    /// `PrComment`'s `--body`, omitting a notes source makes `gh release create` fall back to
    /// an interactive editor prompt that would hang a headless run. See `ReleaseCreate`.
    member _.ReleaseCreate(dir: string, spec: ReleaseCreate) =
        task {
            match checkFlags BINARY [ "tag", spec.Tag ] with
            | Error e -> return Error e
            | Ok() ->
                let args =
                    [ "release"; "create"; spec.Tag ]
                    @ (match spec.Title with
                       | Some t -> [ "--title"; t ]
                       | None -> [])
                    @ [ "--notes"; (defaultArg spec.Notes "") ]
                    @ (if spec.Draft then [ "--draft" ] else [])
                    @ (if spec.Prerelease then [ "--prerelease" ] else [])

                return! core.Run(core.CommandIn(dir, args))
        }

    /// Delete a release by tag (`gh release delete <tag> --yes`). The confirmation flag is
    /// always emitted so the headless wrapper never waits for an interactive prompt.
    member _.ReleaseDelete(dir: string, tag: string) =
        task {
            match checkFlags BINARY [ "tag", tag ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "release"; "delete"; tag; "--yes" ]))
        }

    /// A view of this client bound to repository `dir`: modelled methods drop their leading
    /// `dir` argument, and the raw `Run`/`RunRaw` hatches run in the bound `dir` too.
    member this.At(dir: string) : GitHubAt = GitHubAt(this, dir)

/// A view of a `GitHub` client bound to a repository `dir`. Every modelled method drops the
/// leading `dir` argument and injects the bound one, so `at.PrList()` is `github.PrList dir`
/// and `at.Api(endpoint)` is `github.Api(dir, endpoint)`. The raw `Run`/`RunRaw` escape hatches
/// also run in the bound `dir` (forwarding to `github.Run(dir, …)`/`github.RunRaw(dir, …)`); for
/// a raw command that must run in the process's current directory instead, call `Run`/`RunRaw`
/// on the unbound `GitHub` client.
and [<Sealed>] GitHubAt internal (github: GitHub, dir: string) =

    // --- Escape hatches / version / auth (Run/RunRaw bound to `dir`) ----------

    /// Run `gh <args>` in the bound `dir`. Unguarded.
    member _.Run(args: string seq) = github.Run(dir, args)

    /// Like `Run` but never errors on a non-zero exit — returns the captured result.
    member _.RunRaw(args: string seq) = github.RunRaw(dir, args)

    /// Installed GitHub CLI version (`gh --version`).
    member _.Version() = github.Version()

    /// The installed binary's parsed version, as `GitHubCapabilities`.
    member _.Capabilities() = github.Capabilities()

    /// Whether the user is authenticated (`gh auth status` exits zero).
    member _.AuthStatus() = github.AuthStatus()

    /// The login selected by this client's authentication context.
    member _.AuthIdentity() = github.AuthIdentity()

    /// Whether the user is authenticated for `host` specifically
    /// (`gh auth status --hostname <host>`).
    member _.AuthStatusFor(host: GitHubHost) = github.AuthStatusFor host

    // --- Modelled methods (dir injected as the first argument) ----------------

    /// Raw GitHub REST/GraphQL response body for the bound `dir` (`gh api <endpoint>`).
    member _.Api(endpoint: string) = github.Api(dir, endpoint)

    /// The repository for the bound `dir` (`gh repo view --json …`).
    member _.RepoView() = github.RepoView dir

    /// Pull requests for the bound `dir` (`gh pr list …`) — the previous, options-less
    /// behaviour. Kept as a genuine zero-argument overload for CLR binary compatibility
    /// (see `GitHub.PrList`'s doc comment for the rationale).
    member _.PrList() = github.PrList dir

    /// Pull requests for the bound `dir`, filtered and capped by `options`.
    member _.PrList(options: PrListOptions) = github.PrList(dir, options)

    /// Pull requests that merge `head` into `baseBranch`, any state.
    member _.PrListForBranch(head: string, baseBranch: string) =
        github.PrListForBranch(dir, head, baseBranch)

    /// Pull requests whose head is `head`, in any state, against any base branch.
    member _.PrListForBranch(head: string) = github.PrListForBranch(dir, head)

    /// A single pull request by number (`gh pr view <n> --json …`).
    member _.PrView(number: uint64) = github.PrView(dir, number)

    /// The pull request's unified diff, parsed into per-file `FileDiff` values
    /// (`gh pr diff <n>`).
    member _.PrDiff(number: uint64) = github.PrDiff(dir, number)

    /// Issues for the bound `dir` (`gh issue list …`) — the previous, options-less
    /// behaviour. Kept as a genuine zero-argument overload for CLR binary compatibility
    /// (see `GitHub.PrList`'s doc comment for the rationale).
    member _.IssueList() = github.IssueList dir

    /// Issues for the bound `dir`, filtered and capped by `options`.
    member _.IssueList(options: IssueListOptions) = github.IssueList(dir, options)

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

    /// Add labels to an existing pull request (`gh pr edit <n> --add-label …`).
    member _.PrAddLabels(number: uint64, labels: string list) = github.PrAddLabels(dir, number, labels)

    /// Remove labels from an existing pull request (`gh pr edit <n> --remove-label …`).
    member _.PrRemoveLabels(number: uint64, labels: string list) =
        github.PrRemoveLabels(dir, number, labels)

    /// The PR's submitted reviews and conversation comments (`gh pr view <n> …`).
    member _.PrFeedback(number: uint64) = github.PrFeedback(dir, number)

    /// Recent workflow runs, newest first (`gh run list …`).
    member _.RunList(limit: int, branch: string option) = github.RunList(dir, limit, branch)

    /// Workflow runs selected by one exact commit id.
    member _.RunListForRevision(limit: int, revision: string) =
        github.RunListForRevision(dir, limit, revision)

    /// Active GitHub Actions workflow definitions, up to 50 (`gh workflow list …`).
    member _.WorkflowList() = github.WorkflowList(dir)

    /// GitHub Actions workflow definitions with disabled/limit options (`gh workflow list …`).
    member _.WorkflowList(options: WorkflowListOptions) = github.WorkflowList(dir, options)

    /// Resolve a GitHub Actions workflow by id, name, filename, or path.
    member _.WorkflowView(selector: string) = github.WorkflowView(dir, selector)

    /// A single workflow run by id (`gh run view <id> --json …`).
    member _.RunView(id: uint64) = github.RunView(dir, id)

    /// Block until the run finishes, then return its final state (`gh run watch <id>`).
    member _.RunWatch(id: uint64) = github.RunWatch(dir, id)

    /// Trigger a `workflow_dispatch` event (`gh workflow run …`).
    member _.WorkflowDispatch(spec: WorkflowDispatch) = github.WorkflowDispatch(dir, spec)

    /// Rerun a workflow run (`gh run rerun <id> [--failed]`).
    member _.RunRerun(id: uint64, scope: RerunScope) = github.RunRerun(dir, id, scope)

    /// Cancel a workflow run (`gh run cancel <id>`).
    member _.RunCancel(id: uint64) = github.RunCancel(dir, id)

    /// Open an issue, returning its URL (`gh issue create …`).
    member _.IssueCreate(title: string, body: string) = github.IssueCreate(dir, title, body)

    /// Open an issue with optional labels (`gh issue create --label …`).
    member _.IssueCreate(spec: IssueCreate) = github.IssueCreate(dir, spec)

    /// A single issue by number (`gh issue view <n> --json …`).
    member _.IssueView(number: uint64) = github.IssueView(dir, number)

    /// Close an issue (`gh issue close <n>`).
    member _.IssueClose(number: uint64) = github.IssueClose(dir, number)

    /// Reopen an issue (`gh issue reopen <n>`).
    member _.IssueReopen(number: uint64) = github.IssueReopen(dir, number)

    /// Add a comment to an issue, returning its URL (`gh issue comment <n> --body …`).
    member _.IssueComment(number: uint64, body: string) = github.IssueComment(dir, number, body)

    /// Edit an issue's title and/or body (`gh issue edit <n> …`).
    member _.IssueEdit(number: uint64, title: string option, body: string option) =
        github.IssueEdit(dir, number, title, body)

    /// Add labels to an existing issue (`gh issue edit <n> --add-label …`).
    member _.IssueAddLabels(number: uint64, labels: string list) =
        github.IssueAddLabels(dir, number, labels)

    /// Remove labels from an existing issue (`gh issue edit <n> --remove-label …`).
    member _.IssueRemoveLabels(number: uint64, labels: string list) =
        github.IssueRemoveLabels(dir, number, labels)

    /// Releases, newest first (`gh release list …`).
    member _.ReleaseList() = github.ReleaseList dir

    /// A single release by tag (`gh release view <tag> --json …`).
    member _.ReleaseView(tag: string) = github.ReleaseView(dir, tag)

    /// Create a release on `tag` (`gh release create <tag> …`).
    member _.ReleaseCreate(spec: ReleaseCreate) = github.ReleaseCreate(dir, spec)

    /// Delete a release by tag (`gh release delete <tag> --yes`).
    member _.ReleaseDelete(tag: string) = github.ReleaseDelete(dir, tag)
