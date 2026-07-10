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

    /// R7: whether `dest` is one a clone could have *created* — absent, unreadable, or an empty
    /// directory — as opposed to a non-empty pre-existing dir (the caller's data, which git
    /// refuses to clone into). Captured **before** the clone so a failure can clean only its own
    /// partial output.
    let cloneDestCleanable (dest: string) : bool =
        try
            (not (Directory.Exists dest))
            || (Directory.EnumerateFileSystemEntries dest |> Seq.isEmpty)
        with _ ->
            // Unreadable → the clone would create/populate it; treat as cleanable.
            true

    /// R7: on a failed clone into a `cleanable` `dest`, best-effort remove the partial output so a
    /// retry isn't blocked by "destination path already exists and is not empty". `timeout_grace`
    /// alone can't prevent the partial (Windows' job-kill is atomic; the Unix grace is too short
    /// for a multi-GB partial). Never touches a non-`cleanable` dest (the caller's data).
    let cloneCleanupOnError (dest: string) (cleanable: bool) (result: Result<unit, ProcessError>) =
        match result with
        | Error _ when cleanable ->
            try
                if Directory.Exists dest then
                    Directory.Delete(dest, true)
            with _ ->
                // Best-effort cleanup on the error path; a leftover partial is not fatal.
                ()
        | _ -> ()

        result

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

    /// A branch/ref name interpolated into a refspec (`FetchBranch`, `RemoteBranchExists`)
    /// without going through `checkFlags`. Empty, or a name containing a control character or
    /// any of the refspec/glob metacharacters `" *?[:"`, is refused before git spawns: any of
    /// them either breaks the refspec outright or turns it into a glob — an `ls-remote`/fetch
    /// fan-out across every matching ref instead of the single named one.
    let checkRefspecName (what: string) (name: string) : Result<unit, ProcessError> =
        let forbidden = set [ ' '; '*'; '?'; '['; ':' ]

        let bad =
            name = ""
            || name |> Seq.exists (fun c -> Char.IsControl c || Set.contains c forbidden)

        if bad then
            Error(
                ProcessError.Spawn(
                    BINARY,
                    sprintf
                        "%s \"%s\" is empty or contains a glob/control metacharacter — refusing to build a refspec from it"
                        what
                        name
                )
            )
        else
            Ok()

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

    /// Run `cmd` returning **untrimmed** stdout (unlike `core.Run`, which trims the trailing
    /// newline) — for blob content and diffs where a trailing newline is significant: a
    /// read-modify-write must be byte-exact, and a diff's trailing blank context line keeps the
    /// last hunk's `@@` count valid on re-parse/re-apply.
    let runUntrimmed (cmd: Command) : System.Threading.Tasks.Task<Result<string, ProcessError>> =
        // Capture as bytes and decode, not via the string verb: the latter reconstructs stdout from
        // lines and drops the trailing newline, which would defeat byte-exactness.
        task {
            match! core.OutputBytes cmd with
            | Error e -> return Error e
            | Ok res ->
                match ProcessResult.ensureSuccess res with
                | Error e -> return Error e
                | Ok ok -> return Ok(System.Text.Encoding.UTF8.GetString ok.Stdout)
        }

    /// Scrub the inherited repo-**redirector** environment variables on **every** command
    /// (not just `Harden`), so a `GIT_DIR`/`GIT_INDEX_FILE` (etc.) leaking from the parent —
    /// e.g. running inside a git hook, which exports them — can't silently redirect a command
    /// at a *different* repository than the bound `dir`. (`Harden` additionally scrubs the
    /// command-hook vars and pins hooks/fsmonitor/sshCommand off.)
    static member private scrubbed(core: ManagedClient) : ManagedClient =
        [ "GIT_DIR"
          "GIT_WORK_TREE"
          "GIT_INDEX_FILE"
          "GIT_COMMON_DIR"
          "GIT_OBJECT_DIRECTORY"
          "GIT_ALTERNATE_OBJECT_DIRECTORIES"
          "GIT_NAMESPACE" ]
        |> List.fold (fun (c: ManagedClient) key -> c.DefaultEnvRemove key) core

    /// Create a client driving the real job-backed runner.
    static member Create() =
        Git(Git.scrubbed (ManagedClient.Create BINARY))

    /// Create a client driving `runner` — inject a fake in tests.
    static member WithRunner(runner: IProcessRunner) =
        Git(Git.scrubbed (ManagedClient.WithRunner(BINARY, runner)))

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

    // Bind this client to a directory with `At(dir)` (a `GitAt` view whose modelled methods
    // drop the leading `dir` argument); see the `GitAt` type below.

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

    /// Local branches, current one flagged (`git branch`). `--no-color` so a user
    /// `color.branch=always` config can't inject ANSI escapes into the parsed names.
    member _.Branches(dir: string) =
        core.Parse(core.CommandIn(dir, [ "branch"; "--no-column"; "--no-color" ]), GitParse.parseBranches)

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

    /// Resolve a revision to a full hash (`git rev-parse --verify <rev>`). `--verify` (M13)
    /// makes git ERROR on a `rev` that is not a valid object — without it, a `rev` that happens
    /// to name a tracked path echoes back verbatim (a non-hash) with exit 0, so a caller
    /// resolving an untrusted revision would get garbage instead of a failure.
    member _.RevParse(dir: string, rev: string) =
        task {
            match checkFlags [ "revision", rev ] with
            | Error e -> return Error e
            | Ok() -> return! core.Run(core.CommandIn(dir, [ "rev-parse"; "--verify"; rev ]))
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

    /// Switch to a branch or revision (`git checkout <reference> --`). The trailing `--` is
    /// load-bearing: without it, a `reference` that names no ref but DOES name a tracked path
    /// falls into pathspec mode and silently restores that path (discarding unstaged edits,
    /// exit 0) instead of erroring — a data-loss trap. With `--`, an unknown reference is a
    /// hard error.
    member _.Checkout(dir: string, reference: string) =
        task {
            match checkFlags [ "reference", reference ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "checkout"; reference; "--" ]))
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

    /// Resolve HTTPS credentials into the leading `-c` config args and the secret env, scoped to
    /// `expectHost` (`Some host` for a clone whose URL is externally supplied — so a cross-host
    /// redirect/submodule can't extract the token; `None` for fetch/push/ls-remote, which target
    /// the already-configured remote). Both empty when no provider is configured (ambient auth).
    member private _.RemoteCredentials(expectHost: string option) =
        task {
            match! core.ResolveCredential(CredentialService.Git, None) with
            | Error e -> return Error e
            | Ok None -> return Ok([], [])
            | Ok(Some cred) ->
                let helper = Credentials.gitCredentialHelper cred expectHost
                return Ok(helper.ConfigArgs, helper.Env)
        }

    /// Branch names on `remote`, without fetching (`ls-remote --heads <remote>`).
    member this.RemoteBranches(dir: string, remote: string) =
        task {
            match checkFlags [ "remote name", remote ] with
            | Error e -> return Error e
            | Ok() ->
                match! this.RemoteCredentials None with
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
            match checkRefspecName "branch name" name with
            | Error e -> return Error e
            | Ok() ->
                let refname = sprintf "refs/heads/%s" name

                match! this.RemoteCredentials None with
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
                match! core.Run(core.CommandIn(dir, [ "branch"; "--merged"; target; "--no-column"; "--no-color" ])) with
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
                                    UInt64.TryParse(
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

    /// Aggregate change stats for a range (`diff --shortstat <range>`). C-locale so
    /// `parseShortstat`'s English "file"/"insertion"/"deletion" keying survives a non-English
    /// git (otherwise the counts read as all-zero).
    member _.DiffStat(dir: string, range: string) =
        task {
            match checkFlags [ "range", range ] with
            | Error e -> return Error e
            | Ok() ->
                return!
                    core.Parse(cLocale (core.CommandIn(dir, [ "diff"; "--shortstat"; range ])), GitParse.parseShortstat)
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
                    return! runUntrimmed (core.CommandIn(dir, args target))
            | DiffSpec.Rev rev ->
                match checkFlags [ "revision", rev ] with
                | Error e -> return Error e
                | Ok() -> return! runUntrimmed (core.CommandIn(dir, args rev))
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

    /// Whether a rebase is in progress. `rebase-merge/` is a merge-backend rebase;
    /// `rebase-apply/` is shared by an apply-backend rebase AND `git am` — but `git am` marks
    /// it with an `applying` file, so exclude that (an am aborts with `am --abort`, not
    /// `rebase --abort`; see `IsAmInProgress`). M20.
    member this.IsRebaseInProgress(dir: string) =
        task {
            match! this.ResolvedGitDir dir with
            | Error e -> return Error e
            | Ok g ->
                let rebaseApply = Path.Combine(g, "rebase-apply")

                let isRebaseApply =
                    Directory.Exists rebaseApply
                    && not (File.Exists(Path.Combine(rebaseApply, "applying")))

                return Ok(Directory.Exists(Path.Combine(g, "rebase-merge")) || isRebaseApply)
        }

    /// Whether a `git am` (mailbox apply) is in progress (`rebase-apply/applying`). Distinct
    /// from a rebase, which shares the `rebase-apply` dir but without the `applying` marker —
    /// aborting an am needs `am --abort`, not `rebase --abort`. M20.
    member this.IsAmInProgress(dir: string) =
        task {
            match! this.ResolvedGitDir dir with
            | Error e -> return Error e
            | Ok g -> return Ok(File.Exists(Path.Combine(g, "rebase-apply", "applying")))
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
            match! this.RemoteCredentials None with
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
    member this.FetchBranch(dir: string, branch: string) =
        match checkRefspecName "branch name" branch with
        | Error e -> task { return Error e }
        | Ok() ->
            let refspec = sprintf "refs/heads/%s:refs/remotes/origin/%s" branch branch
            this.RunFetch(dir, [ "origin"; refspec ])

    /// Push to a remote (`push [-u] <remote> <refspec>`).
    member this.Push(dir: string, spec: GitPush) =
        task {
            match checkFlags [ "remote", spec.Remote; "refspec", spec.Refspec ] with
            | Error e -> return Error e
            | Ok() ->
                // M16: `checkFlags` catches a leading `-`/empty/NUL, but not the refspec
                // metacharacters that silently change what a push DOES — a leading `+`
                // (force-push, overwriting the remote non-fast-forward) or an extra `:` (push to
                // an unexpected remote ref). A valid refspec is `branch` or `local:remote` (a
                // single, API-constructed `:`), so allow at most one `:` and no leading `+` on
                // either side; a genuine force-push must go through `Run [ "push"; "--force"; … ]`.
                let sides = spec.Refspec.Split(':')

                if sides.Length > 2 || sides |> Array.exists (fun s -> s.StartsWith '+') then
                    return
                        Error(
                            ProcessError.Spawn(
                                BINARY,
                                sprintf
                                    "push refspec %A contains a force (`+`) or multi-ref (`:`) metacharacter — pass a plain branch or `local:remote`, or use `Run [ \"push\"; … ]` for a force-push"
                                    spec.Refspec
                            )
                        )
                else

                    match! this.RemoteCredentials None with
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
                // Scope the credential helper to the clone URL's host, so a cross-host
                // redirect/submodule during the clone can't extract the token (the URL is often
                // externally supplied).
                match! this.RemoteCredentials(Credentials.httpsHost url) with
                | Error e -> return Error e
                | Ok(pre, envs) ->
                    // Capture whether `dest` is ours to clean BEFORE the clone populates it.
                    let cleanable = cloneDestCleanable dest
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

                    let! result = core.RunUnit cmd
                    return cloneCleanupOnError dest cleanable result
        }

    // --- Mutations: merge / rebase / reset / stash ---------------------------

    /// Stage a branch's changes without committing (`merge --squash <branch>`). C-locale so a
    /// conflicting squash-merge's `CONFLICT (...)` output stays classifiable by `isMergeConflict`.
    member _.MergeSquash(dir: string, branch: string) =
        task {
            match checkFlags [ "branch", branch ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(cLocale (core.CommandIn(dir, [ "merge"; "--squash"; branch ])))
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

    /// Abort an in-progress `git am` (`am --abort`), restoring the pre-`am` HEAD. M20.
    member _.AmAbort(dir: string) =
        core.RunUnit(cLocale (core.CommandIn(dir, [ "am"; "--abort" ])))

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

    /// Restore the most recent stash and drop it (`stash pop`). C-locale so a conflicting pop's
    /// merge-machinery `CONFLICT (...)` output stays classifiable by `isMergeConflict`.
    member _.StashPop(dir: string) =
        core.RunUnit(cLocale (core.CommandIn(dir, [ "stash"; "pop" ])))

    /// The number of entries in the stash list (`git stash list`) — used by `SwitchWithStash` to
    /// tell whether a `stash push` actually saved anything. Internal helper (private in Rust too).
    member private _.StashDepth(dir: string) =
        task {
            match! core.Run(core.CommandIn(dir, [ "stash"; "list" ])) with
            | Error e -> return Error e
            | Ok out -> return Ok(out.Split('\n') |> Array.filter (fun l -> l.TrimEnd('\r') <> "") |> Array.length)
        }

    /// `git stash pop --index` — restore the top stash **preserving** the staged/unstaged split
    /// (a bare `pop` returns everything unstaged). C-locale so a conflicting pop's `CONFLICT (...)`
    /// output still feeds `isMergeConflict`. Internal helper of `SwitchWithStash` (private in Rust).
    member private _.StashPopIndex(dir: string) =
        core.RunUnit(cLocale (core.CommandIn(dir, [ "stash"; "pop"; "--index" ])))

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
                // Untrimmed: a blob's trailing newline(s) must survive for a byte-exact
                // read-modify-write.
                return! runUntrimmed (core.CommandIn(dir, [ "show"; spec ]))
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
                // Untrimmed feed: a file ending in a blank line has a final `\t` (empty) content
                // line, and `parseBlamePorcelain` closes a record only on that `\t`-prefixed line —
                // trimming it (as `core.Parse` does) would silently drop the last blame entry.
                // Mirrors the jj `FileAnnotate` workaround.
                match! runUntrimmed (core.CommandIn(dir, args)) with
                | Error e -> return Error e
                | Ok out -> return Ok(GitParse.parseBlamePorcelain out)
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
                    // `stash push` exits 0 having saved NOTHING when the only dirt is unstashable
                    // (e.g. a submodule-only change that `status` still reports), so a bare `pop`
                    // afterwards would splat an UNRELATED pre-existing stash — data loss. Bracket
                    // the push with the stash-list depth and only pop when it actually saved.
                    match! this.StashDepth dir with
                    | Error e -> return Error e
                    | Ok depthBefore ->
                        match! this.StashPush(dir, true) with
                        | Error e -> return Error e
                        | Ok() ->
                            match! this.StashDepth dir with
                            | Error e -> return Error e
                            | Ok depthAfter ->
                                if depthAfter <= depthBefore then
                                    // Nothing was stashed — switch as-is rather than pop someone
                                    // else's entry.
                                    return! this.Checkout(dir, branch)
                                else
                                    // `--index` restores the staged/unstaged split faithfully; a
                                    // bare `pop` would bring everything back UNSTAGED.
                                    match! this.Checkout(dir, branch) with
                                    | Ok() -> return! this.StashPopIndex dir
                                    | Error err ->
                                        // A failed checkout is atomic — pop restores the pre-call
                                        // state. If the pop fails too, the stash is preserved.
                                        let! _ = this.StashPopIndex dir
                                        return Error err
        }

    /// Harden this client for driving repositories it didn't create: hooks off,
    /// `GIT_*` redirectors/command-hooks/code-execution vectors scrubbed, system config
    /// skipped, repo-local `core.hooksPath`/`fsmonitor`/`sshCommand` pinned via env-config.
    ///
    /// The config pins use the `GIT_CONFIG_COUNT`/`GIT_CONFIG_KEY_n`/`GIT_CONFIG_VALUE_n`
    /// environment mechanism, which requires **git ≥ 2.31**; on an older git those pins are
    /// silently ignored (the env-var scrubbing still applies).
    member this.Harden() =
        let removed =
            [ "GIT_DIR"
              "GIT_WORK_TREE"
              "GIT_INDEX_FILE"
              "GIT_COMMON_DIR"
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
              "GIT_SEQUENCE_EDITOR"
              // Code-execution vectors: a proxy program for `git://`, git's own exec dir
              // (attacker-chosen `git-<x>` sub-commands), and a template dir that seeds
              // hooks/config on init/clone.
              "GIT_PROXY_COMMAND"
              "GIT_EXEC_PATH"
              "GIT_TEMPLATE_DIR"
              // Pathspec-mode vars silently change which paths a command matches.
              "GIT_LITERAL_PATHSPECS"
              "GIT_GLOB_PATHSPECS"
              "GIT_NOGLOB_PATHSPECS"
              "GIT_ICASE_PATHSPECS" ]

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

    /// A view of this client bound to `dir`: the modelled methods drop their leading `dir`
    /// argument. `Run`/`RunRaw` stay bound to the process cwd (see `GitAt`).
    member this.At(dir: string) : GitAt = GitAt(this, dir)

/// A `Git` client with a working directory bound, so calls drop the leading `dir`
/// argument — `git.At(dir).Status()` is `git.Status dir`. Construct one with `Git.At`
/// (or, through the facade, `Repo.GitAt`). Cheap to construct: it only holds the client
/// and the path.
///
/// Asymmetry (deliberate, mirroring the Rust `GitAt`): the *modelled* methods are `dir`
/// forwarders — they inject the bound `dir` as the first argument. The raw `Run`/`RunRaw`
/// escape hatches are `bare` forwarders — they call `git.Run`/`git.RunRaw` unchanged and
/// therefore run in the **process's current working directory**, NOT the bound `dir`. If
/// you need an ad-hoc command to run in `dir`, pass an explicit `-C <dir>` yourself.
and [<Sealed>] GitAt internal (git: Git, dir: string) =

    // --- Escape hatches (bare: NOT bound to `dir` — run in the process cwd) ---

    /// Run `git <args>` in the process's current directory, returning trimmed stdout.
    member _.Run(args: string seq) = git.Run args

    /// Like `Run` but never errors on a non-zero exit — returns the captured result.
    member _.RunRaw(args: string seq) = git.RunRaw args

    /// Installed Git version (`git --version`).
    member _.Version() = git.Version()

    /// The installed binary's parsed version, as `GitCapabilities`.
    member _.Capabilities() = git.Capabilities()

    /// Clone `url` into `dest` (pass an absolute `dest`). Independent of the bound `dir`.
    member _.CloneRepo(url: string, dest: string, spec: CloneSpec) = git.CloneRepo(url, dest, spec)

    // --- dir forwarders (the bound `dir` is injected as the first argument) ---

    /// Working-tree status (`git status --porcelain=v1 -z`).
    member _.Status() = git.Status dir

    /// Raw porcelain status text (`git status --porcelain=v1`).
    member _.StatusText() = git.StatusText dir

    /// Like `Status` but ignoring untracked files.
    member _.StatusTracked() = git.StatusTracked dir

    /// A combined branch + working-tree snapshot in one spawn.
    member _.BranchStatus() = git.BranchStatus dir

    /// Paths with unresolved merge conflicts.
    member _.ConflictedFiles() = git.ConflictedFiles dir

    /// Current branch name, or `None` on a detached HEAD.
    member _.CurrentBranch() = git.CurrentBranch dir

    /// Local branches, current one flagged.
    member _.Branches() = git.Branches dir

    /// Up to `max` commits reachable from `revspec`, newest first.
    member _.Log(revspec: string, max: int) = git.Log(dir, revspec, max)

    /// Resolve a revision to a full hash (`git rev-parse --verify <rev>`).
    member _.RevParse(rev: string) = git.RevParse(dir, rev)

    /// Resolve a revision to its abbreviated hash (`git rev-parse --short <rev>`).
    member _.RevParseShort(rev: string) = git.RevParseShort(dir, rev)

    /// Initialise a repository (`git init`).
    member _.Init() = git.Init dir

    /// Stage `paths` (`git add -- <paths>`).
    member _.Add(paths: string list) = git.Add(dir, paths)

    /// Commit staged changes (`git commit -m`).
    member _.Commit(message: string) = git.Commit(dir, message)

    /// Create a branch without switching to it (`git branch <name>`).
    member _.CreateBranch(name: string) = git.CreateBranch(dir, name)

    /// Switch to a branch or revision (`git checkout <reference> --`).
    member _.Checkout(reference: string) = git.Checkout(dir, reference)

    /// Check out a commit as a detached HEAD (`git checkout --detach <commit>`).
    member _.CheckoutDetach(commit: string) = git.CheckoutDetach(dir, commit)

    /// Commit exactly the spec's paths' working-tree content, ignoring the index.
    member _.CommitPaths(spec: CommitPaths) = git.CommitPaths(dir, spec)

    /// The last commit's full message (`git log -1 --format=%B`).
    member _.LastCommitMessage() = git.LastCommitMessage dir

    /// Whether `HEAD` is unborn — a fresh repo with no commits yet.
    member _.IsUnborn() = git.IsUnborn dir

    /// Whether the working tree has no unstaged modifications to tracked files.
    member _.DiffIsEmpty() = git.DiffIsEmpty dir

    /// The repository's common git directory (stable across linked worktrees).
    member _.CommonDir() = git.CommonDir dir

    /// This worktree's git directory (`rev-parse --git-dir`).
    member _.GitDir() = git.GitDir dir

    /// Resolve a revision to a commit hash, peeling tags.
    member _.ResolveCommit(rev: string) = git.ResolveCommit(dir, rev)

    /// The remote's default branch (short name); `None` when `origin/HEAD` is unset.
    member _.RemoteHeadBranch() = git.RemoteHeadBranch dir

    /// Whether a local branch exists.
    member _.BranchExists(name: string) = git.BranchExists(dir, name)

    /// Whether `origin` has `name`, without fetching.
    member _.RemoteBranchExists(name: string) = git.RemoteBranchExists(dir, name)

    /// A remote's URL (`remote get-url <remote>`).
    member _.RemoteUrl(remote: string) = git.RemoteUrl(dir, remote)

    /// The current branch's upstream, e.g. `Some "origin/main"`; `None` when unset.
    member _.Upstream() = git.Upstream dir

    /// Branch names on `remote`, without fetching (`ls-remote --heads <remote>`).
    member _.RemoteBranches(remote: string) = git.RemoteBranches(dir, remote)

    /// Whether `branch` is fully merged into `target`.
    member _.IsMerged(branch: string, target: string) = git.IsMerged(dir, branch, target)

    /// Set `branch`'s upstream to `upstream`.
    member _.SetUpstream(branch: string, upstream: string) = git.SetUpstream(dir, branch, upstream)

    /// Delete a local branch (`branch -d`, or `-D` when `force`).
    member _.DeleteBranch(name: string, force: bool) = git.DeleteBranch(dir, name, force)

    /// Rename a local branch (`branch -m <old> <new>`).
    member _.RenameBranch(oldName: string, newName: string) = git.RenameBranch(dir, oldName, newName)

    /// Count commits in a range (`rev-list --count <range>`).
    member _.RevListCount(range: string) = git.RevListCount(dir, range)

    /// Whether a diff range is empty (`diff --quiet <range>`).
    member _.DiffRangeIsEmpty(range: string) = git.DiffRangeIsEmpty(dir, range)

    /// Aggregate change stats for a range (`diff --shortstat <range>`).
    member _.DiffStat(range: string) = git.DiffStat(dir, range)

    /// Raw git-format unified diff text for `spec`.
    member _.DiffText(spec: DiffSpec) = git.DiffText(dir, spec)

    /// Parsed per-file unified diff for `spec`.
    member _.Diff(spec: DiffSpec) = git.Diff(dir, spec)

    /// Whether the index has no staged changes (`diff --cached --quiet`).
    member _.StagedIsEmpty() = git.StagedIsEmpty dir

    /// Whether a rebase is in progress.
    member _.IsRebaseInProgress() = git.IsRebaseInProgress dir

    /// Whether a merge is in progress (a `MERGE_HEAD` exists under the git dir).
    member _.IsMergeInProgress() = git.IsMergeInProgress dir

    /// Whether a `git am` (mailbox apply) is in progress.
    member _.IsAmInProgress() = git.IsAmInProgress dir

    /// Fetch from the default remote, retrying transient failures.
    member _.Fetch() = git.Fetch dir

    /// Fetch from a named remote.
    member _.FetchFrom(remote: string) = git.FetchFrom(dir, remote)

    /// Fetch a single branch from `origin` into its remote-tracking ref.
    member _.FetchBranch(branch: string) = git.FetchBranch(dir, branch)

    /// Push to a remote (`push [-u] <remote> <refspec>`).
    member _.Push(spec: GitPush) = git.Push(dir, spec)

    /// Stage a branch's changes without committing (`merge --squash <branch>`).
    member _.MergeSquash(branch: string) = git.MergeSquash(dir, branch)

    /// Merge a branch (`merge [--no-ff] [-m <msg> | --no-edit] <branch>`).
    member _.MergeCommit(spec: MergeCommit) = git.MergeCommit(dir, spec)

    /// Merge a branch but stop before committing.
    member _.MergeNoCommit(spec: MergeNoCommit) = git.MergeNoCommit(dir, spec)

    /// Abort an in-progress merge (`merge --abort`).
    member _.MergeAbort() = git.MergeAbort dir

    /// Finish a merge after resolving conflicts (`commit --no-edit`).
    member _.MergeContinue() = git.MergeContinue dir

    /// Undo an in-progress (or just-staged) merge (`reset --merge`).
    member _.ResetMerge() = git.ResetMerge dir

    /// Hard-reset the working tree to a revision (`reset --hard <rev>`).
    member _.ResetHard(rev: string) = git.ResetHard(dir, rev)

    /// Rebase the current branch onto `onto` (`rebase <onto>`).
    member _.Rebase(onto: string) = git.Rebase(dir, onto)

    /// Abort an in-progress rebase (`rebase --abort`).
    member _.RebaseAbort() = git.RebaseAbort dir

    /// Abort an in-progress `git am` (`am --abort`).
    member _.AmAbort() = git.AmAbort dir

    /// Continue a rebase after resolving conflicts (`rebase --continue`).
    member _.RebaseContinue() = git.RebaseContinue dir

    /// Stash the working tree (`stash push`, `--include-untracked` when asked).
    member _.StashPush(includeUntracked: bool) = git.StashPush(dir, includeUntracked)

    /// Restore the most recent stash and drop it (`stash pop`).
    member _.StashPop() = git.StashPop dir

    /// Switch to `branch`, carrying uncommitted changes across via the stash.
    member _.SwitchWithStash(branch: string) = git.SwitchWithStash(dir, branch)

    /// List worktrees (`worktree list --porcelain`).
    member _.WorktreeList() = git.WorktreeList dir

    /// Add a worktree.
    member _.WorktreeAdd(spec: WorktreeAdd) = git.WorktreeAdd(dir, spec)

    /// Remove a worktree (`worktree remove [--force] <path>`).
    member _.WorktreeRemove(path: string, force: bool) = git.WorktreeRemove(dir, path, force)

    /// Move a worktree (`worktree move <from> <to>`).
    member _.WorktreeMove(fromPath: string, toPath: string) = git.WorktreeMove(dir, fromPath, toPath)

    /// Prune stale worktree admin entries (`worktree prune`).
    member _.WorktreePrune() = git.WorktreePrune dir

    /// Create a lightweight tag at `rev` (`tag <name> [<rev>]`).
    member _.TagCreate(name: string, rev: string option) = git.TagCreate(dir, name, rev)

    /// Create an annotated tag (`tag -a <name> -m <message> [<rev>]`).
    member _.TagCreateAnnotated(spec: AnnotatedTag) = git.TagCreateAnnotated(dir, spec)

    /// Tag names, sorted by git's default ordering (`tag --list`).
    member _.TagList() = git.TagList dir

    /// Delete a tag (`tag -d <name>`).
    member _.TagDelete(name: string) = git.TagDelete(dir, name)

    /// A file's content at a revision (`git show <rev>:<path>`).
    member _.ShowFile(rev: string, path: string) = git.ShowFile(dir, rev, path)

    /// The value of a config key, or `None` when unset (`config --get <key>`).
    member _.ConfigGet(key: string) = git.ConfigGet(dir, key)

    /// Set a config key in the repository's local config (`config <key> <value>`).
    member _.ConfigSet(key: string, value: string) = git.ConfigSet(dir, key, value)

    /// Add a remote (`remote add <name> <url>`).
    member _.RemoteAdd(name: string, url: string) = git.RemoteAdd(dir, name, url)

    /// Change a remote's URL (`remote set-url <name> <url>`).
    member _.RemoteSetUrl(name: string, url: string) = git.RemoteSetUrl(dir, name, url)

    /// Per-line authorship of `path` (`blame --line-porcelain [<rev>] -- <path>`).
    member _.Blame(path: string, rev: string option) = git.Blame(dir, path, rev)

    /// Apply a commit onto the current branch (`cherry-pick <rev>`).
    member _.CherryPick(rev: string) = git.CherryPick(dir, rev)

    /// Revert a commit with the default message (`revert --no-edit <rev>`).
    member _.Revert(rev: string) = git.Revert(dir, rev)

    /// Skip the current patch of a paused rebase (`rebase --skip`).
    member _.RebaseSkip() = git.RebaseSkip dir
