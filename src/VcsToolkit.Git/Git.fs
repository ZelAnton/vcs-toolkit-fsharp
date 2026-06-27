namespace VcsToolkit.Git

open System
open System.IO
open ProcessKit
open VcsToolkit.CliSupport
open VcsToolkit.Diff

/// git-specific command shaping shared by the client's methods.
[<AutoOpen>]
module private GitHelpers =

    /// Force the C locale on a command whose output feeds the error classifiers
    /// (`isMergeConflict`, `isNothingToCommit`, `isTransientFetchError`): they match
    /// untranslated English substrings.
    let cLocale (cmd: Command) = cmd.Env("LC_ALL", "C")

    /// Point git's editor at a no-op so a command that would open `$EDITOR` succeeds
    /// non-interactively instead of hanging a headless caller.
    let noEditor (cmd: Command) =
        cmd.Env("GIT_EDITOR", "true").Env("GIT_SEQUENCE_EDITOR", "true")

    /// Set each secret environment variable on `cmd`. A no-op when `envs` is empty.
    let applySecretEnv (envs: (string * Secret) list) (cmd: Command) =
        envs
        |> List.fold (fun (c: Command) (name, value) -> c.Env(name, value.Expose())) cmd

    /// Apply the argv injection guard to each (what, value) pair, short-circuiting
    /// on the first refusal.
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

/// The real Git client: typed async methods that run the real `git`, parse its
/// output, and return structured values. `Git.Create()` uses the job-backed runner;
/// `Git.WithRunner` injects a fake one for tests. Wraps a `ManagedClient` (enable
/// lock-contention retry with `WithRetry`).
///
/// Injection safety: every method placing a caller-supplied name/revision/range/
/// remote/url in a positional argv slot rejects an empty or `-`-leading value
/// before spawning. Flag-value slots and `--`-separated paths are not guarded.
[<Sealed>]
type Git private (core: ManagedClient) =

    /// Create a client driving the real job-backed runner.
    static member Create() = Git(ManagedClient.Create BINARY)

    /// Create a client driving `runner` — inject a fake in tests.
    static member WithRunner(runner: IProcessRunner) =
        Git(ManagedClient.WithRunner(BINARY, runner))

    // --- Configuration (chainable; each returns a new client) ----------------

    /// Apply a default timeout to every command this client builds.
    member _.DefaultTimeout(timeout: TimeSpan) = Git(core.DefaultTimeout timeout)

    /// Set an environment variable on every command this client builds.
    member _.DefaultEnv(key: string, value: string) = Git(core.DefaultEnv(key, value))

    /// Remove an inherited environment variable on every command this client builds.
    member _.DefaultEnvRemove(key: string) = Git(core.DefaultEnvRemove key)

    /// Cancel every command this client builds when `token` fires.
    member _.DefaultCancelOn(token: Threading.CancellationToken) = Git(core.DefaultCancelOn token)

    /// Retry whole-repo lock-contention failures per `policy` (opt-in, off by default).
    member _.WithRetry(policy: RetryPolicy) = Git(core.WithRetry policy)

    /// Supply credentials for HTTPS remote operations via a provider (opt-in).
    member _.WithCredentials(provider: ICredentialProvider) = Git(core.WithCredentials provider)

    /// Authenticate HTTPS remotes with a single static token (default username `x-access-token`).
    member this.WithToken(token: string) =
        this.WithCredentials(StaticCredential.Token token :> ICredentialProvider)

    /// Read the HTTPS token from environment variable `var` at request time.
    member this.WithEnvToken(var: string) =
        this.WithCredentials(EnvToken var :> ICredentialProvider)

    /// Bind this client to `dir` is provided by the dir-taking methods directly; a
    /// cwd-bound view may be added later.

    // --- Escape hatches ------------------------------------------------------

    /// Run `git <args>` in the current directory, returning trimmed stdout.
    member _.Run(args: string seq) = core.Run(core.Command args)

    /// Like `Run` but never errors on a non-zero exit — returns the captured result.
    member _.RunRaw(args: string seq) = core.Output(core.Command args)

    /// Installed Git version (`git --version`).
    member _.Version() = core.Run(core.Command [ "--version" ])

    /// The installed binary's parsed version, as `GitCapabilities`.
    member this.Capabilities() =
        task {
            match! this.Version() with
            | Error e -> return Error e
            | Ok raw ->
                match GitParse.parseGitVersion raw with
                | Some v -> return Ok { Version = v }
                | None ->
                    return
                        Error(ProcessError.Parse(BINARY, sprintf "unrecognisable `git --version` output: \"%s\"" raw))
        }

    // --- Status & discovery --------------------------------------------------

    /// Working-tree status (`git status --porcelain=v1 -z`).
    member _.Status(dir: string) =
        core.Parse(core.CommandIn(dir, [ "status"; "--porcelain=v1"; "-z" ]), GitParse.parsePorcelain)

    /// Raw porcelain status text (`git status --porcelain=v1`).
    member _.StatusText(dir: string) =
        core.Run(core.CommandIn(dir, [ "status"; "--porcelain=v1" ]))

    /// Like `Status` but ignoring untracked files.
    member _.StatusTracked(dir: string) =
        core.Parse(
            core.CommandIn(dir, [ "status"; "--porcelain=v1"; "-z"; "--untracked-files=no" ]),
            GitParse.parsePorcelain
        )

    /// A combined branch + working-tree snapshot in one spawn.
    member _.BranchStatus(dir: string) =
        // GIT_OPTIONAL_LOCKS=0: don't persist the opportunistic index refresh-write,
        // so a watcher re-querying through this poll primitive isn't re-triggered.
        core.Parse(
            (core.CommandIn(dir, [ "status"; "--porcelain=v2"; "--branch"; "-z" ])).Env("GIT_OPTIONAL_LOCKS", "0"),
            GitParse.parsePorcelainV2
        )

    /// Paths with unresolved merge conflicts (`git diff --name-only --diff-filter=U -z`).
    member _.ConflictedFiles(dir: string) =
        core.Parse(core.CommandIn(dir, [ "diff"; "--name-only"; "--diff-filter=U"; "-z" ]), GitParse.parseNulPaths)

    /// Current branch name, or `None` on a detached HEAD.
    member _.CurrentBranch(dir: string) =
        task {
            match! core.Output(core.CommandIn(dir, [ "symbolic-ref"; "--quiet"; "--short"; "HEAD" ])) with
            | Error e -> return Error e
            | Ok res ->
                match res.Code with
                | Some 0 -> return Ok(Some(res.Stdout.Trim()))
                | Some 1 -> return Ok None // detached HEAD: no named branch
                | _ ->
                    match ProcessResult.ensureSuccess res with
                    | Error e -> return Error e
                    | Ok _ -> return Ok None
        }

    /// Local branches, current one flagged (`git branch`).
    member _.Branches(dir: string) =
        core.Parse(core.CommandIn(dir, [ "branch"; "--no-column" ]), GitParse.parseBranches)

    /// Up to `max` commits reachable from `revspec`, newest first.
    member _.Log(dir: string, revspec: string, max: int) =
        task {
            match checkFlags [ "revspec", revspec ] with
            | Error e -> return Error e
            | Ok() ->
                let n = sprintf "-n%d" max

                return!
                    core.Parse(
                        core.CommandIn(dir, [ "log"; revspec; n; "-z"; "--format=%H%x1f%h%x1f%an%x1f%aI%x1f%s" ]),
                        GitParse.parseLog
                    )
        }

    /// Resolve a revision to a full hash (`git rev-parse <rev>`).
    member _.RevParse(dir: string, rev: string) =
        task {
            match checkFlags [ "revision", rev ] with
            | Error e -> return Error e
            | Ok() -> return! core.Run(core.CommandIn(dir, [ "rev-parse"; rev ]))
        }

    /// Resolve a revision to its abbreviated hash (`git rev-parse --short <rev>`).
    member _.RevParseShort(dir: string, rev: string) =
        task {
            match checkFlags [ "revision", rev ] with
            | Error e -> return Error e
            | Ok() -> return! core.Run(core.CommandIn(dir, [ "rev-parse"; "--short"; rev ]))
        }

    /// Initialise a repository (`git init`).
    member _.Init(dir: string) =
        core.RunUnit(core.CommandIn(dir, [ "init" ]))

    /// Stage `paths` (`git add -- <paths>`).
    member _.Add(dir: string, paths: string list) =
        core.RunUnit((core.CommandIn(dir, [ "add"; "--" ])).Args paths)

    /// Commit staged changes (`git commit -m`).
    member _.Commit(dir: string, message: string) =
        core.RunUnit(cLocale (core.CommandIn(dir, [ "commit"; "-m"; message ])))

    /// Create a branch without switching to it (`git branch <name>`).
    member _.CreateBranch(dir: string, name: string) =
        task {
            match checkFlags [ "branch name", name ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "branch"; name ]))
        }

    /// Switch to a branch or revision (`git checkout <reference>`).
    member _.Checkout(dir: string, reference: string) =
        task {
            match checkFlags [ "reference", reference ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "checkout"; reference ]))
        }

    /// Check out a commit as a detached HEAD (`git checkout --detach <commit>`).
    member _.CheckoutDetach(dir: string, commit: string) =
        task {
            match checkFlags [ "commit", commit ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "checkout"; "--detach"; commit ]))
        }

    /// Commit exactly the spec's paths' working-tree content, ignoring the index.
    member _.CommitPaths(dir: string, spec: CommitPaths) =
        let baseCmd = cLocale (core.CommandIn(dir, [ "commit" ]))
        let withAmend = if spec.Amend then baseCmd.Arg "--amend" else baseCmd

        let cmd =
            (withAmend.Arg("-m").Arg(spec.Message).Arg("--only").Arg "--").Args spec.Paths

        core.RunUnit cmd

    /// The last commit's full message (`git log -1 --format=%B`).
    member _.LastCommitMessage(dir: string) =
        core.Run(core.CommandIn(dir, [ "log"; "-1"; "--format=%B" ]))

    /// Whether `HEAD` is unborn — a fresh repo with no commits yet.
    member _.IsUnborn(dir: string) =
        task {
            match! core.Probe(core.CommandIn(dir, [ "rev-parse"; "--verify"; "-q"; "HEAD" ])) with
            | Ok b -> return Ok(not b)
            | Error e -> return Error e
        }

    /// Whether the working tree has no unstaged modifications to tracked files.
    member _.DiffIsEmpty(dir: string) =
        core.Probe(core.CommandIn(dir, [ "diff"; "--quiet" ]))

    /// The repository's common git directory (stable across linked worktrees).
    member _.CommonDir(dir: string) =
        core.Run(core.CommandIn(dir, [ "rev-parse"; "--git-common-dir" ]))

    /// This worktree's git directory (`rev-parse --git-dir`).
    member _.GitDir(dir: string) =
        core.Run(core.CommandIn(dir, [ "rev-parse"; "--git-dir" ]))

    /// Resolve a revision to a commit hash, peeling tags.
    member _.ResolveCommit(dir: string, rev: string) =
        task {
            match checkFlags [ "revision", rev ] with
            | Error e -> return Error e
            | Ok() ->
                let spec = sprintf "%s^{commit}" rev
                return! core.Run(core.CommandIn(dir, [ "rev-parse"; "--verify"; spec ]))
        }

    /// The remote's default branch (short name); `None` when `origin/HEAD` is unset.
    member _.RemoteHeadBranch(dir: string) =
        task {
            match! core.Output(core.CommandIn(dir, [ "symbolic-ref"; "--quiet"; "refs/remotes/origin/HEAD" ])) with
            | Error e -> return Error e
            | Ok res ->
                match res.Code with
                | Some 0 ->
                    let out = res.Stdout.Trim()
                    let prefix = "refs/remotes/origin/"

                    let name =
                        if out.StartsWith(prefix, StringComparison.Ordinal) then
                            out.Substring prefix.Length
                        else
                            out

                    return Ok(Some name)
                | Some 1 -> return Ok None
                | _ ->
                    match ProcessResult.ensureSuccess res with
                    | Error e -> return Error e
                    | Ok _ -> return Ok None
        }

    /// Whether a local branch exists.
    member _.BranchExists(dir: string, name: string) =
        let refname = sprintf "refs/heads/%s" name
        core.Probe(core.CommandIn(dir, [ "show-ref"; "--verify"; "--quiet"; refname ]))

    /// A remote's URL (`remote get-url <remote>`).
    member _.RemoteUrl(dir: string, remote: string) =
        task {
            match checkFlags [ "remote name", remote ] with
            | Error e -> return Error e
            | Ok() -> return! core.Run(core.CommandIn(dir, [ "remote"; "get-url"; remote ]))
        }

    /// The current branch's upstream, e.g. `Some "origin/main"`; `None` when unset.
    member _.Upstream(dir: string) =
        task {
            match!
                core.Output(core.CommandIn(dir, [ "rev-parse"; "--abbrev-ref"; "--symbolic-full-name"; "@{u}" ]))
            with
            | Error e -> return Error e
            | Ok res ->
                match res.Code with
                | Some 0 ->
                    let name = res.Stdout.Trim()
                    return Ok(if name <> "" then Some name else None)
                | Some _ -> return Ok None // any non-zero exit => no upstream configured
                | None ->
                    match ProcessResult.ensureSuccess res with
                    | Error e -> return Error e
                    | Ok _ -> return Ok None
        }

    // --- Remote credentials --------------------------------------------------

    /// Resolve HTTPS credentials into the leading `-c` config args and the secret env.
    /// Both empty when no provider is configured (ambient git auth).
    member private _.RemoteCredentials() =
        task {
            match! core.ResolveCredential(CredentialService.Git, None) with
            | Error e -> return Error e
            | Ok None -> return Ok([], [])
            | Ok(Some cred) ->
                let helper = Credentials.gitCredentialHelper cred
                return Ok(helper.ConfigArgs, helper.Env)
        }

    /// Branch names on `remote`, without fetching (`ls-remote --heads <remote>`).
    member this.RemoteBranches(dir: string, remote: string) =
        task {
            match checkFlags [ "remote name", remote ] with
            | Error e -> return Error e
            | Ok() ->
                match! this.RemoteCredentials() with
                | Error e -> return Error e
                | Ok(pre, envs) ->
                    let args = pre @ [ "ls-remote"; "--heads"; remote ]

                    let cmd =
                        (core.CommandIn(dir, args)).Env("GIT_TERMINAL_PROMPT", "0")
                        |> applySecretEnv envs

                    return! core.Parse(cmd, GitParse.parseLsRemoteHeads)
        }

    /// Whether `origin` has `name`, without fetching.
    member this.RemoteBranchExists(dir: string, name: string) =
        task {
            let refname = sprintf "refs/heads/%s" name

            match! this.RemoteCredentials() with
            | Error e -> return Error e
            | Ok(pre, envs) ->
                let args = pre @ [ "ls-remote"; "origin"; refname ]

                let cmd =
                    (core.CommandIn(dir, args)).Env("GIT_TERMINAL_PROMPT", "0").Timeout(TimeSpan.FromSeconds 10.0)
                    |> applySecretEnv envs

                match! core.Output cmd with
                | Error e -> return Error e
                | Ok res -> return Ok(res.Code = Some 0 && res.Stdout.Trim() <> "")
        }

    // --- Branches ------------------------------------------------------------

    /// Whether `branch` is fully merged into `target`.
    member _.IsMerged(dir: string, branch: string, target: string) =
        task {
            match checkFlags [ "branch", branch; "target", target ] with
            | Error e -> return Error e
            | Ok() ->
                match! core.Run(core.CommandIn(dir, [ "branch"; "--merged"; target; "--no-column" ])) with
                | Error e -> return Error e
                | Ok out ->
                    let merged =
                        out.Split(char 10)
                        |> Array.map (fun l -> l.TrimEnd(char 13))
                        |> Array.exists (fun line -> line.Length >= 2 && line.Substring 2 = branch)

                    return Ok merged
        }

    /// Set `branch`'s upstream to `upstream`.
    member _.SetUpstream(dir: string, branch: string, upstream: string) =
        task {
            match checkFlags [ "branch name", branch ] with
            | Error e -> return Error e
            | Ok() ->
                let flag = sprintf "--set-upstream-to=%s" upstream
                return! core.RunUnit(core.CommandIn(dir, [ "branch"; flag; branch ]))
        }

    /// Delete a local branch (`branch -d`, or `-D` when `force`).
    member _.DeleteBranch(dir: string, name: string, force: bool) =
        task {
            match checkFlags [ "branch name", name ] with
            | Error e -> return Error e
            | Ok() ->
                let flag = if force then "-D" else "-d"
                return! core.RunUnit(core.CommandIn(dir, [ "branch"; flag; name ]))
        }

    /// Rename a local branch (`branch -m <old> <new>`).
    member _.RenameBranch(dir: string, oldName: string, newName: string) =
        task {
            match checkFlags [ "branch name", oldName; "branch name", newName ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "branch"; "-m"; oldName; newName ]))
        }

    /// Count commits in a range (`rev-list --count <range>`).
    member _.RevListCount(dir: string, range: string) =
        task {
            match checkFlags [ "range", range ] with
            | Error e -> return Error e
            | Ok() ->
                return!
                    core.TryParse(
                        core.CommandIn(dir, [ "rev-list"; "--count"; range ]),
                        fun s ->
                            let t = s.Trim()

                            if t.Length > 0 && t |> Seq.forall Char.IsAsciiDigit then
                                match
                                    Int32.TryParse(
                                        t,
                                        Globalization.NumberStyles.None,
                                        Globalization.CultureInfo.InvariantCulture
                                    )
                                with
                                | true, n -> Ok n
                                | _ -> Error "invalid rev-list count"
                            else
                                Error "invalid rev-list count"
                    )
        }

    /// Whether a diff range is empty (`diff --quiet <range>`).
    member _.DiffRangeIsEmpty(dir: string, range: string) =
        task {
            match checkFlags [ "range", range ] with
            | Error e -> return Error e
            | Ok() -> return! core.Probe(core.CommandIn(dir, [ "diff"; "--quiet"; range ]))
        }

    /// Aggregate change stats for a range (`diff --shortstat <range>`).
    member _.DiffStat(dir: string, range: string) =
        task {
            match checkFlags [ "range", range ] with
            | Error e -> return Error e
            | Ok() -> return! core.Parse(core.CommandIn(dir, [ "diff"; "--shortstat"; range ]), GitParse.parseShortstat)
        }

    /// Raw git-format unified diff text for `spec`.
    member this.DiffText(dir: string, spec: DiffSpec) =
        task {
            let args target =
                [ "diff"
                  target
                  "--no-color"
                  "--no-ext-diff"
                  "-M"
                  "--src-prefix=a/"
                  "--dst-prefix=b/" ]

            match spec with
            | DiffSpec.WorkingTree ->
                match! this.IsUnborn dir with
                | Error e -> return Error e
                | Ok unborn ->
                    let target = if unborn then EMPTY_TREE else "HEAD"
                    return! core.Run(core.CommandIn(dir, args target))
            | DiffSpec.Rev rev ->
                match checkFlags [ "revision", rev ] with
                | Error e -> return Error e
                | Ok() -> return! core.Run(core.CommandIn(dir, args rev))
        }

    /// Parsed per-file unified diff for `spec`.
    member this.Diff(dir: string, spec: DiffSpec) =
        task {
            match! this.DiffText(dir, spec) with
            | Error e -> return Error e
            | Ok text -> return Ok(parseDiff text)
        }

    // --- In-progress state ---------------------------------------------------

    /// Whether the index has no staged changes (`diff --cached --quiet`).
    member _.StagedIsEmpty(dir: string) =
        core.Probe(core.CommandIn(dir, [ "diff"; "--cached"; "--quiet" ]))

    /// `git_dir` resolved to an absolute path.
    member private _.ResolvedGitDir(dir: string) =
        task {
            match! core.Run(core.CommandIn(dir, [ "rev-parse"; "--git-dir" ])) with
            | Error e -> return Error e
            | Ok gitDir ->
                let resolved =
                    if Path.IsPathRooted gitDir then
                        gitDir
                    else
                        Path.Combine(dir, gitDir)

                return Ok resolved
        }

    /// Whether a rebase is in progress.
    member this.IsRebaseInProgress(dir: string) =
        task {
            match! this.ResolvedGitDir dir with
            | Error e -> return Error e
            | Ok g ->
                return
                    Ok(
                        Directory.Exists(Path.Combine(g, "rebase-merge"))
                        || Directory.Exists(Path.Combine(g, "rebase-apply"))
                    )
        }

    /// Whether a merge is in progress (a `MERGE_HEAD` exists under the git dir).
    member this.IsMergeInProgress(dir: string) =
        task {
            match! this.ResolvedGitDir dir with
            | Error e -> return Error e
            | Ok g -> return Ok(File.Exists(Path.Combine(g, "MERGE_HEAD")))
        }

    // --- Mutations: fetch / push / clone -------------------------------------

    member private this.RunFetch(dir: string, tail: string list) =
        task {
            match! this.RemoteCredentials() with
            | Error e -> return Error e
            | Ok(pre, envs) ->
                let args = pre @ [ "fetch"; "--quiet" ] @ tail

                let cmd =
                    (cLocale (core.CommandIn(dir, args)))
                        .Env("GIT_TERMINAL_PROMPT", "0")
                        .TimeoutGrace(FetchTimeoutGrace)
                        .Retry(FetchAttempts, FetchBackoff, (fun e -> isTransientFetchError e))
                    |> applySecretEnv envs

                return! core.RunUnit cmd
        }

    /// Fetch from the default remote, retrying transient failures.
    member this.Fetch(dir: string) = this.RunFetch(dir, [])

    /// Fetch from a named remote.
    member this.FetchFrom(dir: string, remote: string) =
        task {
            match checkFlags [ "remote", remote ] with
            | Error e -> return Error e
            | Ok() -> return! this.RunFetch(dir, [ remote ])
        }

    /// Fetch a single branch from `origin` into its remote-tracking ref.
    member this.FetchRemoteBranch(dir: string, branch: string) =
        let refspec = sprintf "refs/heads/%s:refs/remotes/origin/%s" branch branch
        this.RunFetch(dir, [ "origin"; refspec ])

    /// Push to a remote (`push [-u] <remote> <refspec>`).
    member this.Push(dir: string, spec: GitPush) =
        task {
            match checkFlags [ "remote", spec.Remote; "refspec", spec.Refspec ] with
            | Error e -> return Error e
            | Ok() ->
                match! this.RemoteCredentials() with
                | Error e -> return Error e
                | Ok(pre, envs) ->
                    let upstream = if spec.SetUpstream then [ "-u" ] else []
                    let args = pre @ [ "push" ] @ upstream @ [ spec.Remote; spec.Refspec ]

                    let cmd =
                        (core.CommandIn(dir, args)).Env("GIT_TERMINAL_PROMPT", "0").TimeoutGrace(FetchTimeoutGrace)
                        |> applySecretEnv envs

                    return! core.RunUnit cmd
        }

    /// Clone `url` into `dest` (pass an absolute `dest`).
    member this.CloneRepo(url: string, dest: string, spec: CloneSpec) =
        task {
            match checkFlags [ "url", url ] with
            | Error e -> return Error e
            | Ok() ->
                match! this.RemoteCredentials() with
                | Error e -> return Error e
                | Ok(pre, envs) ->
                    let mutable cmd = core.Command(pre @ [ "clone" ])

                    match spec.Branch with
                    | Some b -> cmd <- cmd.Arg("--branch").Arg b
                    | None -> ()

                    match spec.Depth with
                    | Some d -> cmd <- cmd.Arg("--depth").Arg(string d)
                    | None -> ()

                    if spec.Bare then
                        cmd <- cmd.Arg "--bare"

                    let cmd =
                        cmd.Arg(url).Arg(dest).Env("GIT_TERMINAL_PROMPT", "0").TimeoutGrace(FetchTimeoutGrace)
                        |> applySecretEnv envs

                    return! core.RunUnit cmd
        }

    // --- Mutations: merge / rebase / reset / stash ---------------------------

    /// Stage a branch's changes without committing (`merge --squash <branch>`).
    member _.MergeSquash(dir: string, branch: string) =
        task {
            match checkFlags [ "branch", branch ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "merge"; "--squash"; branch ]))
        }

    /// Merge a branch (`merge [--no-ff] [-m <msg> | --no-edit] <branch>`).
    member _.MergeCommit(dir: string, spec: MergeCommit) =
        task {
            match checkFlags [ "branch", spec.Branch ] with
            | Error e -> return Error e
            | Ok() ->
                let args =
                    [ "merge" ]
                    @ (if spec.NoFf then [ "--no-ff" ] else [])
                    @ (match spec.Message with
                       | Some m -> [ "-m"; m ]
                       | None -> [ "--no-edit" ])
                    @ [ spec.Branch ]

                return! core.RunUnit(cLocale (core.CommandIn(dir, args)))
        }

    /// Merge a branch but stop before committing.
    member _.MergeNoCommit(dir: string, spec: MergeNoCommit) =
        task {
            match checkFlags [ "branch", spec.Branch ] with
            | Error e -> return Error e
            | Ok() ->
                let middle =
                    if spec.Squash then [ "--squash" ]
                    elif spec.NoFf then [ "--no-ff" ]
                    else []

                let args = [ "merge"; "--no-commit" ] @ middle @ [ spec.Branch ]
                return! core.RunUnit(cLocale (core.CommandIn(dir, args)))
        }

    /// Abort an in-progress merge (`merge --abort`).
    member _.MergeAbort(dir: string) =
        core.RunUnit(cLocale (core.CommandIn(dir, [ "merge"; "--abort" ])))

    /// Finish a merge after resolving conflicts (`commit --no-edit`).
    member _.MergeContinue(dir: string) =
        core.RunUnit(noEditor (cLocale (core.CommandIn(dir, [ "commit"; "--no-edit" ]))))

    /// Undo an in-progress (or just-staged) merge (`reset --merge`).
    member _.ResetMerge(dir: string) =
        core.RunUnit(core.CommandIn(dir, [ "reset"; "--merge" ]))

    /// Hard-reset the working tree to a revision (`reset --hard <rev>`).
    member _.ResetHard(dir: string, rev: string) =
        task {
            match checkFlags [ "revision", rev ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "reset"; "--hard"; rev ]))
        }

    /// Rebase the current branch onto `onto` (`rebase <onto>`).
    member _.Rebase(dir: string, onto: string) =
        task {
            match checkFlags [ "rebase target", onto ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(noEditor (cLocale (core.CommandIn(dir, [ "rebase"; onto ]))))
        }

    /// Abort an in-progress rebase (`rebase --abort`).
    member _.RebaseAbort(dir: string) =
        core.RunUnit(cLocale (core.CommandIn(dir, [ "rebase"; "--abort" ])))

    /// Continue a rebase after resolving conflicts (`rebase --continue`).
    member _.RebaseContinue(dir: string) =
        core.RunUnit(noEditor (cLocale (core.CommandIn(dir, [ "rebase"; "--continue" ]))))

    /// Skip the current patch of a paused rebase (`rebase --skip`).
    member _.RebaseSkip(dir: string) =
        core.RunUnit(noEditor (cLocale (core.CommandIn(dir, [ "rebase"; "--skip" ]))))

    /// Stash the working tree (`stash push`, `--include-untracked` when asked).
    member _.StashPush(dir: string, includeUntracked: bool) =
        let cmd = core.CommandIn(dir, [ "stash"; "push" ])

        let cmd =
            if includeUntracked then
                cmd.Arg "--include-untracked"
            else
                cmd

        core.RunUnit cmd

    /// Restore the most recent stash and drop it (`stash pop`).
    member _.StashPop(dir: string) =
        core.RunUnit(core.CommandIn(dir, [ "stash"; "pop" ]))

    // --- Worktrees -----------------------------------------------------------

    /// List worktrees (`worktree list --porcelain`).
    member _.WorktreeList(dir: string) =
        core.Parse(core.CommandIn(dir, [ "worktree"; "list"; "--porcelain" ]), GitParse.parseWorktreePorcelain)

    /// Add a worktree.
    member _.WorktreeAdd(dir: string, spec: WorktreeAdd) =
        task {
            let checks =
                (spec.NewBranch |> Option.map (fun n -> "branch name", n) |> Option.toList)
                @ (spec.Commitish |> Option.map (fun c -> "commit-ish", c) |> Option.toList)

            match checkFlags checks with
            | Error e -> return Error e
            | Ok() ->
                let mutable cmd = core.CommandIn(dir, [ "worktree"; "add" ])

                match spec.NewBranch with
                | Some n -> cmd <- cmd.Arg("-b").Arg n
                | None -> ()

                if spec.NoCheckout then
                    cmd <- cmd.Arg "--no-checkout"

                cmd <- cmd.Arg spec.Path

                match spec.Commitish with
                | Some c -> cmd <- cmd.Arg c
                | None -> ()

                return! core.RunUnit cmd
        }

    /// Remove a worktree (`worktree remove [--force] <path>`).
    member _.WorktreeRemove(dir: string, path: string, force: bool) =
        let cmd = core.CommandIn(dir, [ "worktree"; "remove" ])
        let cmd = if force then cmd.Arg "--force" else cmd
        core.RunUnit(cmd.Arg path)

    /// Move a worktree (`worktree move <from> <to>`).
    member _.WorktreeMove(dir: string, fromPath: string, toPath: string) =
        core.RunUnit((core.CommandIn(dir, [ "worktree"; "move" ])).Arg(fromPath).Arg toPath)

    /// Prune stale worktree admin entries (`worktree prune`).
    member _.WorktreePrune(dir: string) =
        core.RunUnit(core.CommandIn(dir, [ "worktree"; "prune" ]))

    // --- Tags / inspection ---------------------------------------------------

    /// Create a lightweight tag at `rev` (`tag <name> [<rev>]`).
    member _.TagCreate(dir: string, name: string, rev: string option) =
        task {
            let checks =
                ("tag name", name)
                :: (rev |> Option.map (fun r -> "revision", r) |> Option.toList)

            match checkFlags checks with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "tag"; name ] @ Option.toList rev))
        }

    /// Create an annotated tag (`tag -a <name> -m <message> [<rev>]`).
    member _.TagCreateAnnotated(dir: string, spec: AnnotatedTag) =
        task {
            let checks =
                ("tag name", spec.Name)
                :: (spec.Rev |> Option.map (fun r -> "revision", r) |> Option.toList)

            match checkFlags checks with
            | Error e -> return Error e
            | Ok() ->
                return!
                    core.RunUnit(
                        core.CommandIn(dir, [ "tag"; "-a"; spec.Name; "-m"; spec.Message ] @ Option.toList spec.Rev)
                    )
        }

    /// Tag names, sorted by git's default ordering (`tag --list`).
    member _.TagList(dir: string) =
        task {
            match! core.Run(core.CommandIn(dir, [ "tag"; "--list"; "--no-column" ])) with
            | Error e -> return Error e
            | Ok out ->
                let tags =
                    out.Split(char 10)
                    |> Array.map (fun l -> l.TrimEnd(char 13))
                    |> Array.filter (fun l -> l <> "")
                    |> Array.toList

                return Ok tags
        }

    /// Delete a tag (`tag -d <name>`).
    member _.TagDelete(dir: string, name: string) =
        task {
            match checkFlags [ "tag name", name ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "tag"; "-d"; name ]))
        }

    /// A file's content at a revision (`git show <rev>:<path>`).
    member _.ShowFile(dir: string, rev: string, path: string) =
        task {
            match checkFlags [ "revision", rev ] with
            | Error e -> return Error e
            | Ok() ->
                // Windows: git rejects backslash separators in the <rev>:<path> spec.
                let p =
                    if OperatingSystem.IsWindows() then
                        path.Replace(char 92, '/')
                    else
                        path

                let spec = sprintf "%s:%s" rev p
                return! core.Run(core.CommandIn(dir, [ "show"; spec ]))
        }

    /// The value of a config key, or `None` when unset (`config --get <key>`).
    member _.ConfigGet(dir: string, key: string) =
        task {
            match checkFlags [ "config key", key ] with
            | Error e -> return Error e
            | Ok() ->
                match! core.Output(core.CommandIn(dir, [ "config"; "--get"; key ])) with
                | Error e -> return Error e
                | Ok res ->
                    match res.Code with
                    | Some 1 -> return Ok None
                    | Some 0 -> return Ok(Some(res.Stdout.TrimEnd(char 13, char 10)))
                    | _ ->
                        match ProcessResult.ensureSuccess res with
                        | Error e -> return Error e
                        | Ok _ -> return Ok None
        }

    /// Set a config key in the repository's local config (`config <key> <value>`).
    member _.ConfigSet(dir: string, key: string, value: string) =
        task {
            match checkFlags [ "config key", key ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "config"; key; value ]))
        }

    /// Add a remote (`remote add <name> <url>`).
    member _.RemoteAdd(dir: string, name: string, url: string) =
        task {
            match checkFlags [ "remote name", name; "url", url ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "remote"; "add"; name; url ]))
        }

    /// Change a remote's URL (`remote set-url <name> <url>`).
    member _.RemoteSetUrl(dir: string, name: string, url: string) =
        task {
            match checkFlags [ "remote name", name; "url", url ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "remote"; "set-url"; name; url ]))
        }

    /// Per-line authorship of `path` (`blame --line-porcelain [<rev>] -- <path>`).
    member _.Blame(dir: string, path: string, rev: string option) =
        task {
            let guard =
                match rev with
                | Some r -> checkFlags [ "revision", r ]
                | None -> Ok()

            match guard with
            | Error e -> return Error e
            | Ok() ->
                let args = [ "blame"; "--line-porcelain" ] @ Option.toList rev @ [ "--"; path ]
                return! core.Parse(core.CommandIn(dir, args), GitParse.parseBlamePorcelain)
        }

    // --- Sequencer -----------------------------------------------------------

    /// Apply a commit onto the current branch (`cherry-pick <rev>`).
    member _.CherryPick(dir: string, rev: string) =
        task {
            match checkFlags [ "revision", rev ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(noEditor (cLocale (core.CommandIn(dir, [ "cherry-pick"; rev ]))))
        }

    /// Revert a commit with the default message (`revert --no-edit <rev>`).
    member _.Revert(dir: string, rev: string) =
        task {
            match checkFlags [ "revision", rev ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(noEditor (cLocale (core.CommandIn(dir, [ "revert"; "--no-edit"; rev ]))))
        }

    // --- Composed operations / profiles --------------------------------------

    /// Switch to `branch`, carrying uncommitted changes across via the stash.
    member this.SwitchWithStash(dir: string, branch: string) =
        task {
            match! this.Status dir with
            | Error e -> return Error e
            | Ok entries ->
                if List.isEmpty entries then
                    return! this.Checkout(dir, branch)
                else
                    match! this.StashPush(dir, true) with
                    | Error e -> return Error e
                    | Ok() ->
                        match! this.Checkout(dir, branch) with
                        | Ok() -> return! this.StashPop dir
                        | Error err ->
                            // A failed checkout is atomic — pop restores the pre-call state.
                            let! _ = this.StashPop dir
                            return Error err
        }

    /// Harden this client for driving repositories it didn't create: hooks off,
    /// `GIT_*` redirectors/command-hooks scrubbed, system config skipped, repo-local
    /// `core.hooksPath`/`fsmonitor`/`sshCommand` pinned via env-config.
    member this.Harden() =
        let removed =
            [ "GIT_DIR"
              "GIT_WORK_TREE"
              "GIT_INDEX_FILE"
              "GIT_OBJECT_DIRECTORY"
              "GIT_ALTERNATE_OBJECT_DIRECTORIES"
              "GIT_NAMESPACE"
              "GIT_CEILING_DIRECTORIES"
              "GIT_CONFIG_PARAMETERS"
              "GIT_CONFIG_GLOBAL"
              "GIT_CONFIG_SYSTEM"
              "GIT_SSH_COMMAND"
              "GIT_SSH"
              "GIT_ASKPASS"
              "GIT_EXTERNAL_DIFF"
              "GIT_PAGER"
              "GIT_EDITOR"
              "GIT_SEQUENCE_EDITOR" ]

        let scrubbed =
            removed |> List.fold (fun (g: Git) key -> g.DefaultEnvRemove key) this

        scrubbed
            .DefaultEnv("GIT_CONFIG_NOSYSTEM", "1")
            .DefaultEnv("GIT_TERMINAL_PROMPT", "0")
            .DefaultEnv("GIT_CONFIG_COUNT", "3")
            .DefaultEnv("GIT_CONFIG_KEY_0", "core.hooksPath")
            .DefaultEnv("GIT_CONFIG_VALUE_0", "/dev/null")
            .DefaultEnv("GIT_CONFIG_KEY_1", "core.fsmonitor")
            .DefaultEnv("GIT_CONFIG_VALUE_1", "false")
            .DefaultEnv("GIT_CONFIG_KEY_2", "core.sshCommand")
            .DefaultEnv("GIT_CONFIG_VALUE_2", "")

    /// A hardened real (job-backed) client — `Git.Create().Harden()`.
    static member Hardened() = Git.Create().Harden()
