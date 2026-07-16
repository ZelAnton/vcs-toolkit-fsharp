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

    /// Conservative ceiling, in argv characters, on how much space this wrapper will spend
    /// inlining a path list into a `git` command line (`Add`/`CommitPaths`/`LogPaths`) before it
    /// switches transports. Well under Windows' hard ~32767-character `CreateProcess` command-line
    /// limit — the tightest ceiling among the supported platforms — leaving headroom for the rest
    /// of the argv (git's own flags, per-argument quoting overhead) and the parent process's own
    /// command line. `Add`/`CommitPaths` cross it into the NUL-safe `--pathspec-from-file=-
    /// --pathspec-file-nul` stdin transport (the paths then leave argv entirely); `LogPaths`, for
    /// which `git log` has no `--pathspec-from-file` support, instead chunks the pathspecs across
    /// several within-budget calls (`chunkPathspecs`) and merges the results.
    [<Literal>]
    let ArgvPathBudget = 30000

    /// Whether inlining `paths` into argv (each path plus a separating space, roughly — real
    /// quoting overhead only adds more) would approach `ArgvPathBudget`.
    let needsStdinPathTransport (paths: string list) : bool =
        let total = paths |> List.sumBy (fun p -> p.Length + 1)
        total > ArgvPathBudget

    /// Split `paths` into groups whose combined length (each entry's length plus one, matching
    /// `needsStdinPathTransport`'s accounting) stays within `ArgvPathBudget` — for `LogPaths`'s
    /// large-path-set fallback, since `git log` (unlike `Add`/`CommitPaths`) has no
    /// `--pathspec-from-file` transport. Every group gets at least one path (a single path already
    /// over budget still gets its own singleton group — nothing shorter is possible; `LogPaths`
    /// rejects such a path up front). Preserves `paths`' order, both within and across groups.
    let chunkPathspecs (paths: string list) : string list list =
        let completed, current, _ =
            paths
            |> List.fold
                (fun (chunks: string list list, cur: string list, curLen: int) (path: string) ->
                    let len = path.Length + 1

                    if not (List.isEmpty cur) && curLen + len > ArgvPathBudget then
                        (List.rev cur :: chunks, [ path ], len)
                    else
                        (chunks, path :: cur, curLen + len))
                ([], [], 0)

        let allChunks =
            if List.isEmpty current then
                completed
            else
                List.rev current :: completed

        List.rev allChunks

    /// Refuse a path set containing an embedded NUL character before it is used to build the
    /// NUL-delimited `--pathspec-file-nul` stdin transport, or spawn anything: a NUL inside a
    /// path would truncate that entry's framing in the NUL-delimited stream, splicing the
    /// remainder into the NEXT pathspec entry — a path-injection/corruption vector. Runs before
    /// any spawn decision (small-set or stdin-transport alike), so a refusal here never leaves a
    /// partial `Add`/`CommitPaths` behind.
    let checkNoEmbeddedNul (what: string) (paths: string list) : Result<unit, ProcessError> =
        if paths |> List.exists (fun p -> p.IndexOf(char 0) >= 0) then
            Error(
                ProcessError.Spawn(
                    BINARY,
                    sprintf
                        "%s set contains a path with an embedded NUL character — refusing to build a pathspec transport from it"
                        what
                )
            )
        else
            Ok()

    /// NUL-terminate each path and concatenate to UTF-8 bytes, for `--pathspec-file-nul` stdin
    /// (`Stdin.FromBytes`). Assumes `checkNoEmbeddedNul` already passed.
    let encodeNulPaths (paths: string list) : byte[] =
        paths
        |> List.map (fun p -> p + "\u0000")
        |> String.concat ""
        |> System.Text.Encoding.UTF8.GetBytes

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

    /// The self-contained time budget `Git.MergeAbortDetached`/`IsMergeInProgressDetached` give
    /// their cleanup (the merge-in-progress probe + `merge --abort`). It runs on a *fresh*
    /// cancellation token carrying this timeout — never the (possibly already-fired) token of
    /// the operation whose failure/cancellation triggered the cleanup — so a cancelled or
    /// timed-out `tryMerge` probe is still cleaned up. Mirrors `Jj.RollbackCleanupTimeout`
    /// (`src/VcsToolkit.Jj/Jj.fs`).
    let MergeAbortCleanupTimeout = TimeSpan.FromSeconds 30.0

    /// Build `git merge --abort` in the C locale (`cLocale`, matching every other
    /// classifier-read command) — the one argv shared by the token-inheriting `MergeAbort` and
    /// the detached `MergeAbortDetached`, so the two forms can never drift apart.
    let mergeAbortCommand (core: ManagedClient) (dir: string) : Command =
        cLocale (core.CommandIn(dir, [ "merge"; "--abort" ]))

    /// `git rev-parse --git-dir` resolved to an absolute path, run via the given `core` — the
    /// shared basis for `Git.ResolvedGitDir` (token-inheriting) and the detached
    /// merge-in-progress probe, so both resolve the git dir identically.
    let resolvedGitDirVia (core: ManagedClient) (dir: string) =
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

/// The real Git client: typed async methods that run the real `git`, parse its
/// output, and return structured values. `Git.Create()` uses the job-backed runner;
/// `Git.WithRunner` injects a fake one for tests. Wraps a `ManagedClient` (enable
/// lock-contention retry with `WithRetry`).
///
/// Injection safety: every method placing a caller-supplied name/revision/range/
/// remote/url/path (including a clone destination, a worktree add/remove/move path)
/// in a positional argv slot rejects an empty or `-`-leading value before spawning.
/// The sole exception is `ConfigSet`'s value slot, where a legitimate value may
/// genuinely begin with `-` (e.g. `-1`): it is protected by an end-of-options `--`
/// separator instead of being refused. Flag-value slots and `--`-separated paths
/// are not guarded.
[<Sealed>]
type Git private (core: ManagedClient) =

    /// Build one `git --literal-pathspecs log <revs…> -n<max> -z --format=… -- <paths>` call —
    /// used both for `LogPaths`'s common case (everything fits one invocation, `revs` being the
    /// single caller `revspec`) and for each chunk of its large-path-set fallback (`revs` then the
    /// already-resolved commit-id tokens reused verbatim across every chunk). `--literal-pathspecs`
    /// matches a path containing `*`/`?`/`[]` literally rather than as pathspec glob magic; the
    /// trailing `--` keeps a path from being read as a flag. Chunked order is restored afterwards
    /// by `logOrderCommand`, not by anything embedded in a chunk's own output, so the format is the
    /// same on both paths.
    let logPathsCommand (dir: string) (revs: string list) (n: string) (paths: string list) : Command =
        let cmd =
            ((core.CommandIn(dir, [ "--literal-pathspecs"; "log" ])).Args revs)
                .Arg(n)
                .Arg("-z")
                .Arg("--format=%H%x1f%h%x1f%an%x1f%aI%x1f%s")
                .Arg("--")

        cmd.Args paths

    /// Build the pathless `git log <revs…> -z --format=%H` commit-order oracle used to restore
    /// git's own order across `LogPaths`'s merged chunk results. `revs` is the same already-resolved
    /// commit-id tokens the chunk calls used (resolving the revspec again here would reopen the race
    /// the one up-front resolution closes). No `-n` cap: a commit surviving path-filtering into the
    /// merged top-`max` can sit arbitrarily far back in the unrestricted history, so the oracle must
    /// be able to rank it. No paths, so no `--literal-pathspecs` is needed.
    let logOrderCommand (dir: string) (revs: string list) : Command =
        ((core.CommandIn(dir, [ "log" ])).Args revs).Arg("-z").Arg("--format=%H")

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

    /// Run `git <args>` in the process's current directory, returning trimmed stdout. For an
    /// ad-hoc command scoped to a repository, use the `dir`-taking overload (`Run(dir, args)`)
    /// or a bound view's `at(dir).Run(args)`.
    member _.Run(args: string seq) = core.Run(core.Command args)

    /// Run `git <args>` in `dir`, returning trimmed stdout — the `dir`-bound counterpart of
    /// `Run(args)` (which runs in the process cwd). Backs `GitAt.Run`.
    member _.Run(dir: string, args: string seq) = core.Run(core.CommandIn(dir, args))

    /// Like `Run` but never errors on a non-zero exit — returns the captured result.
    member _.RunRaw(args: string seq) = core.Output(core.Command args)

    /// Like `Run(dir, args)` but never errors on a non-zero exit — returns the captured
    /// result. Backs `GitAt.RunRaw`.
    member _.RunRaw(dir: string, args: string seq) = core.Output(core.CommandIn(dir, args))

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
            match checkFlags BINARY [ "revspec", revspec ] with
            | Error e -> return Error e
            | Ok() ->
                let n = sprintf "-n%d" max

                return!
                    core.Parse(
                        core.CommandIn(dir, [ "log"; revspec; n; "-z"; "--format=%H%x1f%h%x1f%an%x1f%aI%x1f%s" ]),
                        GitParse.parseLog
                    )
        }

    /// Like `Log`, but scoped to commits that touched `paths` (`git --literal-pathspecs log
    /// <revspec> -n<max> -z --format=… -- <paths>`) — e.g. "who changed this module".
    /// `--literal-pathspecs` matches a path containing `*`/`?`/`[]` literally rather than as
    /// pathspec glob magic, and the trailing `--` keeps a path from being read as a flag (same
    /// convention as `Add`/`CommitPaths`). An empty `paths` is refused **before spawning**:
    /// silently falling back to `Log`'s unrestricted history would defeat the "scoped to these
    /// paths" contract.
    ///
    /// Unlike `Add`/`CommitPaths`, `git log` has no `--pathspec-from-file` transport (verified
    /// against real git: the flag is rejected outright), so a `paths` set that would risk exceeding
    /// the OS argv limit is instead split into several within-budget `git log` calls
    /// (`chunkPathspecs`); the per-call results are merged (deduplicated by hash — a commit can
    /// touch paths spread across more than one chunk) and restored to git's own commit order using
    /// a separate, pathless `git log <revspec> --format=%H` oracle (`logOrderCommand`): pathspec
    /// filtering only drops non-matching commits, it never reorders the ones that remain, so the
    /// oracle's order over the *same* revspec gives the exact relative order a single, unchunked
    /// call would have produced — without any assumption about author-vs-committer timestamps or
    /// same-second ties. Requesting `-n<max>` per chunk is enough: any commit within the true
    /// merged top-`max` has at most `max - 1` newer commits across the whole union, hence at most
    /// that many within its own chunk too, so it always ranks within that chunk's own top `max`.
    /// A `paths` set that fits one call is unaffected — same single invocation, same order.
    ///
    /// Before any of that, `revspec` is resolved exactly once (`git rev-parse`) into the fixed
    /// commit ids every chunk call and the oracle then reuse verbatim: without this, each of those
    /// several independent invocations would re-resolve the same symbolic text on its own, so a
    /// concurrent commit/ref-move between two of them could make them observe different snapshots
    /// (silently omitting a newer match, including an unreachable one, or interleaving two
    /// histories). This resolution happens only on the chunked path; the single-call fast path is
    /// untouched. Also before any of that, every path is checked against the argv budget on its own:
    /// `git log` has no NUL-safe transport to fall back to, so a path that alone cannot fit in argv
    /// is rejected up front rather than forwarded as an over-budget singleton chunk.
    member _.LogPaths(dir: string, revspec: string, max: int, paths: string list) =
        task {
            match checkFlags BINARY [ "revspec", revspec ] with
            | Error e -> return Error e
            | Ok() ->
                if List.isEmpty paths then
                    return
                        Error(
                            ProcessError.Spawn(
                                BINARY,
                                "LogPaths requires at least one path — an empty set would log unrestricted history, not history scoped to the named paths"
                            )
                        )
                else
                    match paths |> List.tryFind (fun p -> p.Length + 1 > ArgvPathBudget) with
                    | Some oversized ->
                        return
                            Error(
                                ProcessError.Spawn(
                                    BINARY,
                                    sprintf
                                        "LogPaths: a single path is %d bytes, exceeding the %d-byte argv pathspec budget on its own — `git log` has no NUL-safe pathspec-from-file transport (unlike Add/CommitPaths), so this path cannot be transmitted at all: %A"
                                        (oversized.Length + 1)
                                        ArgvPathBudget
                                        oversized
                                )
                            )
                    | None ->
                        let n = sprintf "-n%d" max
                        let chunks = chunkPathspecs paths

                        if List.length chunks <= 1 then
                            // The common case: everything fits one call. A single invocation can't
                            // observe a moving repository state mid-operation, so `revspec` is
                            // forwarded as-is — no up-front resolution needed.
                            return! core.Parse(logPathsCommand dir [ revspec ] n paths, GitParse.parseLog)
                        else
                            // Large path set: resolve the revspec once, run each chunk over that
                            // fixed snapshot, dedup by hash, then reorder by the pathless oracle.
                            match! core.Run(core.CommandIn(dir, [ "rev-parse"; revspec ])) with
                            | Error e -> return Error e
                            | Ok raw ->
                                let resolvedRevs =
                                    raw.Split('\n')
                                    |> Array.map (fun s -> s.Trim())
                                    |> Array.filter (fun s -> s <> "")
                                    |> Array.toList

                                // Accumulate each chunk's commits (deduped by hash) sequentially, over
                                // the one fixed snapshot; a mutable while-loop (not an inner `let rec`,
                                // which the task state machine can't statically compile) so a chunk
                                // failure short-circuits the rest.
                                let mutable chunkError: ProcessError option = None
                                let mutable acc: Commit list = []
                                let mutable seen: Set<string> = Set.empty
                                let mutable remaining = chunks

                                while chunkError.IsNone && not (List.isEmpty remaining) do
                                    let chunk = List.head remaining
                                    remaining <- List.tail remaining

                                    match! core.Parse(logPathsCommand dir resolvedRevs n chunk, GitParse.parseLog) with
                                    | Error e -> chunkError <- Some e
                                    | Ok commits ->
                                        for c in commits do
                                            if not (Set.contains c.Hash seen) then
                                                acc <- c :: acc
                                                seen <- Set.add c.Hash seen

                                match chunkError with
                                | Some e -> return Error e
                                | None ->
                                    let merged = List.rev acc

                                    match! core.Parse(logOrderCommand dir resolvedRevs, GitParse.parseCommitOrder) with
                                    | Error e -> return Error e
                                    | Ok order ->
                                        let rank = order |> List.mapi (fun i h -> (h, i)) |> Map.ofList

                                        let ranked =
                                            merged
                                            |> List.sortBy (fun (c: Commit) ->
                                                match Map.tryFind c.Hash rank with
                                                | Some r -> r
                                                | None -> System.Int32.MaxValue)
                                            |> List.truncate max

                                        return Ok ranked
        }

    /// Resolve a revision to a full hash (`git rev-parse --verify <rev>`). `--verify` (M13)
    /// makes git ERROR on a `rev` that is not a valid object — without it, a `rev` that happens
    /// to name a tracked path echoes back verbatim (a non-hash) with exit 0, so a caller
    /// resolving an untrusted revision would get garbage instead of a failure.
    member _.RevParse(dir: string, rev: string) =
        task {
            match checkFlags BINARY [ "revision", rev ] with
            | Error e -> return Error e
            | Ok() -> return! core.Run(core.CommandIn(dir, [ "rev-parse"; "--verify"; rev ]))
        }

    /// Resolve a revision to its abbreviated hash (`git rev-parse --short <rev>`).
    member _.RevParseShort(dir: string, rev: string) =
        task {
            match checkFlags BINARY [ "revision", rev ] with
            | Error e -> return Error e
            | Ok() -> return! core.Run(core.CommandIn(dir, [ "rev-parse"; "--short"; rev ]))
        }

    /// Initialise a repository (`git init`).
    member _.Init(dir: string) =
        core.RunUnit(core.CommandIn(dir, [ "init" ]))

    /// Stage `paths` (`git --literal-pathspecs add -- <paths>`). `--literal-pathspecs` is applied
    /// unconditionally so a path containing a glob metacharacter (`*`, `?`, `[]`) is matched
    /// literally instead of being expanded as a pathspec pattern. A path set whose combined argv
    /// length would approach `ArgvPathBudget` (the tightest supported OS command-line ceiling —
    /// Windows' ~32767-character `CreateProcess` limit) is routed through the NUL-safe
    /// `--pathspec-from-file=- --pathspec-file-nul` stdin transport instead, keeping the paths out
    /// of argv entirely; a small set still goes through a single argv-based call, its argv
    /// unchanged apart from the added `--literal-pathspecs`.
    member _.Add(dir: string, paths: string list) =
        task {
            match checkNoEmbeddedNul "path" paths with
            | Error e -> return Error e
            | Ok() ->
                if needsStdinPathTransport paths then
                    let cmd =
                        (core.CommandIn(
                            dir,
                            [ "--literal-pathspecs"
                              "add"
                              "--pathspec-from-file=-"
                              "--pathspec-file-nul" ]
                        ))
                            .Stdin(Stdin.FromBytes(encodeNulPaths paths))

                    return! core.RunUnit cmd
                else
                    return! core.RunUnit((core.CommandIn(dir, [ "--literal-pathspecs"; "add"; "--" ])).Args paths)
        }

    /// Commit staged changes (`git commit -m`).
    member _.Commit(dir: string, message: string) =
        core.RunUnit(cLocale (core.CommandIn(dir, [ "commit"; "-m"; message ])))

    /// Create a branch without switching to it (`git branch <name>`).
    member _.CreateBranch(dir: string, name: string) =
        task {
            match checkFlags BINARY [ "branch name", name ] with
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
            match checkFlags BINARY [ "reference", reference ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "checkout"; reference; "--" ]))
        }

    /// Check out a commit as a detached HEAD (`git checkout --detach <commit>`).
    member _.CheckoutDetach(dir: string, commit: string) =
        task {
            match checkFlags BINARY [ "commit", commit ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "checkout"; "--detach"; commit ]))
        }

    /// Commit exactly the spec's paths' working-tree content, ignoring the index
    /// (`git --literal-pathspecs commit [--amend] -m <message> --only -- <paths>`).
    /// `--literal-pathspecs` is applied unconditionally (see `Add`), and a path set whose
    /// combined argv length would approach `ArgvPathBudget` is routed through the NUL-safe
    /// `--pathspec-from-file=- --pathspec-file-nul` stdin transport instead. The embedded-NUL
    /// guard runs before any spawn decision, so a rejected input never leaves a partial commit.
    member _.CommitPaths(dir: string, spec: CommitPaths) =
        task {
            match checkNoEmbeddedNul "path" spec.Paths with
            | Error e -> return Error e
            | Ok() ->
                let baseCmd = cLocale (core.CommandIn(dir, [ "--literal-pathspecs"; "commit" ]))
                let withAmend = if spec.Amend then baseCmd.Arg "--amend" else baseCmd
                let withMsg = withAmend.Arg("-m").Arg(spec.Message).Arg "--only"

                if needsStdinPathTransport spec.Paths then
                    let cmd =
                        withMsg
                            .Arg("--pathspec-from-file=-")
                            .Arg("--pathspec-file-nul")
                            .Stdin(Stdin.FromBytes(encodeNulPaths spec.Paths))

                    return! core.RunUnit cmd
                else
                    return! core.RunUnit((withMsg.Arg "--").Args spec.Paths)
        }

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

    /// This repository's empty-tree object id, in its **actual** object format — SHA-1's
    /// well-known `4b825dc…`, or the 64-hex equivalent under
    /// `extensions.objectFormat=sha256`. Computed via `git hash-object -t tree --stdin`
    /// fed empty stdin: `--stdin` (not `-w`) only hashes, it writes nothing to the object
    /// database. Unlike the hardcoded `EMPTY_TREE` constant, this resolves correctly
    /// regardless of the repository's object format — use it (not the constant) wherever
    /// "the empty tree of this repository" is meant, e.g. as the unborn-`HEAD` diff target.
    member _.EmptyTreeOid(dir: string) =
        core.Run((core.CommandIn(dir, [ "hash-object"; "-t"; "tree"; "--stdin" ])).Stdin(Stdin.Empty))

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
            match checkFlags BINARY [ "revision", rev ] with
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
            match checkFlags BINARY [ "remote name", remote ] with
            | Error e -> return Error e
            | Ok() -> return! core.Run(core.CommandIn(dir, [ "remote"; "get-url"; remote ]))
        }

    /// The current branch's upstream, e.g. `Some "origin/main"`; `None` when unset.
    /// Requires HEAD to be an attached branch (`symbolic-ref` first): detached HEAD, a
    /// directory outside a repository, or any other `symbolic-ref` failure surfaces as
    /// `Error`, not a false "no upstream". Only on an attached branch does `@{u}`'s
    /// exit code 128 — which git also uses for those same failure modes — get mapped
    /// to `Ok None`; any other non-zero code or no-code outcome (e.g. timeout) is
    /// still a real `Error`.
    member _.Upstream(dir: string) =
        task {
            match! core.Output(core.CommandIn(dir, [ "symbolic-ref"; "--quiet"; "--short"; "HEAD" ])) with
            | Error e -> return Error e
            | Ok symRes ->
                match ProcessResult.ensureSuccess symRes with
                | Error e -> return Error e
                | Ok _ ->
                    match!
                        core.Output(
                            core.CommandIn(dir, [ "rev-parse"; "--abbrev-ref"; "--symbolic-full-name"; "@{u}" ])
                        )
                    with
                    | Error e -> return Error e
                    | Ok res ->
                        match res.Code with
                        | Some 0 ->
                            let name = res.Stdout.Trim()
                            return Ok(if name <> "" then Some name else None)
                        | Some 128 -> return Ok None // no upstream configured for the current branch
                        | _ ->
                            match ProcessResult.ensureSuccess res with
                            | Error e -> return Error e
                            | Ok _ -> return Ok None
        }

    // --- Remote credentials --------------------------------------------------

    /// Resolve HTTPS credentials into the leading `-c` config args and the secret env, scoped to
    /// `expectHost` (`Some host` for a clone whose URL is externally supplied — so a cross-host
    /// redirect/submodule can't extract the token; `None` for fetch/push/ls-remote, which target
    /// the already-configured remote). The same `expectHost` also scopes the *provider lookup*
    /// (`ResolveCredential`), so a host-keyed provider serves the secret for this host — and
    /// because provider selection and the credential-helper's host gate are driven by the one
    /// `expectHost`, they can never disagree (no cross-host secret release). Both empty when no
    /// provider is configured (ambient auth).
    member private _.RemoteCredentials(expectHost: string option) =
        task {
            match! core.ResolveCredential(CredentialService.Git, expectHost) with
            | Error e -> return Error e
            | Ok None -> return Ok([], [])
            | Ok(Some cred) ->
                let helper = Credentials.gitCredentialHelper cred expectHost
                return Ok(helper.ConfigArgs, helper.Env)
        }

    // Both read-only `ls-remote` queries inherit the caller's `DefaultTimeout` and have no
    // `TimeoutGrace`: unlike fetch/push, they cannot leave local or remote state half-applied.
    /// Branch names on `remote`, without fetching (`ls-remote --heads <remote>`).
    member this.RemoteBranches(dir: string, remote: string) =
        task {
            match checkFlags BINARY [ "remote name", remote ] with
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
                        (core.CommandIn(dir, args)).Env("GIT_TERMINAL_PROMPT", "0")
                        |> applySecretEnv envs

                    match! core.Output cmd with
                    | Error e -> return Error e
                    | Ok res -> return Ok(res.Code = Some 0 && res.Stdout.Trim() <> "")
        }

    // --- Branches ------------------------------------------------------------

    /// Whether `branch` is fully merged into `target`.
    member _.IsMerged(dir: string, branch: string, target: string) =
        task {
            match checkFlags BINARY [ "branch", branch; "target", target ] with
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
            match checkFlags BINARY [ "branch name", branch ] with
            | Error e -> return Error e
            | Ok() ->
                let flag = sprintf "--set-upstream-to=%s" upstream
                return! core.RunUnit(core.CommandIn(dir, [ "branch"; flag; branch ]))
        }

    /// Delete a local branch (`branch -d`, or `-D` when `force`).
    member _.DeleteBranch(dir: string, name: string, force: bool) =
        task {
            match checkFlags BINARY [ "branch name", name ] with
            | Error e -> return Error e
            | Ok() ->
                let flag = if force then "-D" else "-d"
                return! core.RunUnit(core.CommandIn(dir, [ "branch"; flag; name ]))
        }

    /// Rename a local branch (`branch -m <old> <new>`).
    member _.RenameBranch(dir: string, oldName: string, newName: string) =
        task {
            match checkFlags BINARY [ "branch name", oldName; "branch name", newName ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "branch"; "-m"; oldName; newName ]))
        }

    /// Count commits in a range (`rev-list --count <range>`).
    member _.RevListCount(dir: string, range: string) =
        task {
            match checkFlags BINARY [ "range", range ] with
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
            match checkFlags BINARY [ "range", range ] with
            | Error e -> return Error e
            | Ok() -> return! core.Probe(core.CommandIn(dir, [ "diff"; "--quiet"; range ]))
        }

    /// Aggregate change stats for a range (`diff --shortstat <range>`). C-locale so
    /// `parseShortstat`'s English "file"/"insertion"/"deletion" keying survives a non-English
    /// git (otherwise the counts read as all-zero).
    member _.DiffStat(dir: string, range: string) =
        task {
            match checkFlags BINARY [ "range", range ] with
            | Error e -> return Error e
            | Ok() ->
                return!
                    core.Parse(cLocale (core.CommandIn(dir, [ "diff"; "--shortstat"; range ])), GitParse.parseShortstat)
        }

    /// Raw git-format unified diff text for `spec`, untrimmed and UTF-8-decoded — the trailing
    /// blank context line is preserved so the last hunk's `@@` count stays valid on re-parse. git
    /// emits this as text; a non-UTF-8 byte quoted inside the diff would decode to U+FFFD (git
    /// escapes such content in its own diff format, so this is not a byte-exactness concern here).
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
                | Ok false -> return! runUntrimmed core (core.CommandIn(dir, args "HEAD"))
                | Ok true ->
                    match! this.EmptyTreeOid dir with
                    | Error e -> return Error e
                    | Ok emptyTree -> return! runUntrimmed core (core.CommandIn(dir, args emptyTree))
            | DiffSpec.Rev rev ->
                match checkFlags BINARY [ "revision", rev ] with
                | Error e -> return Error e
                | Ok() -> return! runUntrimmed core (core.CommandIn(dir, args rev))
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
    member private _.ResolvedGitDir(dir: string) = resolvedGitDirVia core dir

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

    /// Whether a merge is in progress (a `MERGE_HEAD` exists under the git dir), probed on a
    /// **fresh** cancellation budget (`MergeAbortCleanupTimeout`) instead of this client's own
    /// cancellation token — the detached counterpart of `IsMergeInProgress`, for a cleanup path
    /// that must still probe even when the operation whose failure triggered the cleanup has
    /// already been cancelled or timed out (`GitBackend.tryMerge`'s three cleanup branches).
    /// Mirrors `Jj.RollbackTo`'s fresh-budget cleanup client.
    member _.IsMergeInProgressDetached(dir: string) =
        task {
            use cts = new Threading.CancellationTokenSource(MergeAbortCleanupTimeout)

            let cleanupCore =
                (core.DefaultCancelOn cts.Token).DefaultTimeout MergeAbortCleanupTimeout

            match! resolvedGitDirVia cleanupCore dir with
            | Error e -> return Error e
            | Ok g -> return Ok(File.Exists(Path.Combine(g, "MERGE_HEAD")))
        }

    /// Whether a cherry-pick is in progress (`CHERRY_PICK_HEAD` under the git dir). A
    /// cherry-pick conflict writes `CHERRY_PICK_HEAD`, **not** `MERGE_HEAD`, so this is
    /// distinct from a merge and is aborted/continued with `cherry-pick --abort` /
    /// `cherry-pick --continue`, not `merge --abort`.
    member this.IsCherryPickInProgress(dir: string) =
        task {
            match! this.ResolvedGitDir dir with
            | Error e -> return Error e
            | Ok g -> return Ok(File.Exists(Path.Combine(g, "CHERRY_PICK_HEAD")))
        }

    /// Whether a revert is in progress (`REVERT_HEAD` under the git dir). Like a cherry-pick,
    /// a revert conflict writes its own head file, not `MERGE_HEAD`; it is driven with
    /// `revert --abort` / `revert --continue`.
    member this.IsRevertInProgress(dir: string) =
        task {
            match! this.ResolvedGitDir dir with
            | Error e -> return Error e
            | Ok g -> return Ok(File.Exists(Path.Combine(g, "REVERT_HEAD")))
        }

    /// Whether a `git bisect` session is in progress (`BISECT_LOG` under the git dir).
    /// `BISECT_LOG` is git's own canonical "a bisect is running" marker (it also drives
    /// `git bisect log`); the other `BISECT_*` files are session details. A bisect is ended
    /// with `bisect reset` — there is no `--continue`.
    member this.IsBisectInProgress(dir: string) =
        task {
            match! this.ResolvedGitDir dir with
            | Error e -> return Error e
            | Ok g -> return Ok(File.Exists(Path.Combine(g, "BISECT_LOG")))
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
            match checkFlags BINARY [ "remote", remote ] with
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
            match checkFlags BINARY [ "remote", spec.Remote; "refspec", spec.Refspec ] with
            | Error e -> return Error e
            | Ok() ->
                // M16: `checkFlags` catches a leading `-`/empty/NUL, but not the refspec
                // metacharacters that silently change what a push DOES — a leading `+`
                // (force-push, overwriting the remote non-fast-forward), an extra `:` (push to
                // an unexpected remote ref), or an empty side (`:branch` deletes the remote
                // branch, `:` pushes all matching branches, `local:` pushes to an empty remote
                // ref — all destructive fan-out/deletion the typed API claims impossible). A
                // valid refspec is `branch` or `local:remote` (a single, API-constructed `:`
                // with both sides non-empty), so allow at most one `:`, no leading `+`, and no
                // empty side; a genuine force-push/delete must go through
                // `Run [ "push"; "--force"; … ]`.
                let sides = spec.Refspec.Split(':')

                if sides.Length > 2 || sides |> Array.exists (fun s -> s.StartsWith '+' || s = "") then
                    return
                        Error(
                            ProcessError.Spawn(
                                BINARY,
                                sprintf
                                    "push refspec %A contains a force (`+`), multi-ref (`:`), or empty-side (delete/all-matching) metacharacter — pass a plain branch or `local:remote`, or use `Run [ \"push\"; … ]` for a force-push/delete"
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

    /// Clone `url` into `dest` (pass an absolute `dest`). Both `url` and `dest` are bare
    /// positionals here (`git clone <url> <dest>`), so a leading-`-` value in either would be
    /// read as a flag — e.g. a `dest` of `--upload-pack=<cmd>` is a command-execution vector,
    /// since git accepts options after positionals — and both are refused before any spawn.
    member this.CloneRepo(url: string, dest: string, spec: CloneSpec) =
        task {
            match checkFlags BINARY [ "url", url; "destination", dest ] with
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
            match checkFlags BINARY [ "branch", branch ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(cLocale (core.CommandIn(dir, [ "merge"; "--squash"; branch ])))
        }

    /// Merge a branch (`merge [--no-ff] [-m <msg> | --no-edit] <branch>`).
    member _.MergeCommit(dir: string, spec: MergeCommit) =
        task {
            match checkFlags BINARY [ "branch", spec.Branch ] with
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
            match checkFlags BINARY [ "branch", spec.Branch ] with
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
        core.RunUnit(mergeAbortCommand core dir)

    /// Abort an in-progress merge (`merge --abort`) on a **fresh** cancellation budget
    /// (`MergeAbortCleanupTimeout`) instead of this client's own cancellation token — the
    /// detached counterpart of `MergeAbort`, so a cleanup triggered by a cancelled or
    /// timed-out `tryMerge` probe still runs to completion instead of racing the caller's own
    /// (possibly already-fired) token. Builds the identical `merge --abort` argv as
    /// `MergeAbort` via the shared `mergeAbortCommand`, so the two forms never drift apart.
    /// Mirrors `Jj.RollbackTo`.
    member _.MergeAbortDetached(dir: string) =
        task {
            use cts = new Threading.CancellationTokenSource(MergeAbortCleanupTimeout)

            let cleanupCore =
                (core.DefaultCancelOn cts.Token).DefaultTimeout MergeAbortCleanupTimeout

            return! cleanupCore.RunUnit(mergeAbortCommand cleanupCore dir)
        }

    /// Finish a merge after resolving conflicts (`commit --no-edit`).
    member _.MergeContinue(dir: string) =
        core.RunUnit(noEditor (cLocale (core.CommandIn(dir, [ "commit"; "--no-edit" ]))))

    /// Undo an in-progress (or just-staged) merge (`reset --merge`).
    member _.ResetMerge(dir: string) =
        core.RunUnit(core.CommandIn(dir, [ "reset"; "--merge" ]))

    /// Hard-reset the working tree to a revision (`reset --hard <rev>`).
    member _.ResetHard(dir: string, rev: string) =
        task {
            match checkFlags BINARY [ "revision", rev ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "reset"; "--hard"; rev ]))
        }

    /// Rebase the current branch onto `onto` (`rebase <onto>`).
    member _.Rebase(dir: string, onto: string) =
        task {
            match checkFlags BINARY [ "rebase target", onto ] with
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

    /// Abort an in-progress cherry-pick (`cherry-pick --abort`), restoring the
    /// pre-cherry-pick state. No editor on `--abort`, but keep the C locale so any failure
    /// output still feeds the classifiers uniformly with the rest of the sequencer.
    member _.CherryPickAbort(dir: string) =
        core.RunUnit(cLocale (core.CommandIn(dir, [ "cherry-pick"; "--abort" ])))

    /// Continue a cherry-pick after resolving conflicts (`cherry-pick --continue`); the editor
    /// is suppressed so the message-confirm never hangs a headless caller. On a multi-commit
    /// pick it can stop again on the next commit's conflict (exit non-zero) — a conflict, not a
    /// hard error (`GitBackend.continueInProgress` classifies that via `ConflictedFiles`).
    member _.CherryPickContinue(dir: string) =
        core.RunUnit(noEditor (cLocale (core.CommandIn(dir, [ "cherry-pick"; "--continue" ]))))

    /// Abort an in-progress revert (`revert --abort`), restoring the pre-revert state.
    member _.RevertAbort(dir: string) =
        core.RunUnit(cLocale (core.CommandIn(dir, [ "revert"; "--abort" ])))

    /// Continue a revert after resolving conflicts (`revert --continue`); the editor is
    /// suppressed like `CherryPickContinue`, and it too can stop on the next commit's conflict.
    member _.RevertContinue(dir: string) =
        core.RunUnit(noEditor (cLocale (core.CommandIn(dir, [ "revert"; "--continue" ]))))

    /// End a `git bisect` session (`bisect reset`), returning to the branch/commit that was
    /// checked out before it started. This is the "abort" for a bisect; bisect has no
    /// `--continue`.
    member _.BisectReset(dir: string) =
        core.RunUnit(cLocale (core.CommandIn(dir, [ "bisect"; "reset" ])))

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

    /// Add a worktree. `spec.Path` is a bare positional (`worktree add … <path> [<commit-ish>]`),
    /// so a leading-`-` path would be read as a flag — it is refused before spawning, alongside
    /// the already-guarded `NewBranch`/`Commitish`.
    member _.WorktreeAdd(dir: string, spec: WorktreeAdd) =
        task {
            let checks =
                [ "worktree path", spec.Path ]
                @ (spec.NewBranch |> Option.map (fun n -> "branch name", n) |> Option.toList)
                @ (spec.Commitish |> Option.map (fun c -> "commit-ish", c) |> Option.toList)

            match checkFlags BINARY checks with
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

    /// Remove a worktree (`worktree remove [--force] <path>`). `path` is a bare positional, so a
    /// leading-`-` value would be read as a flag — it is refused before spawning.
    member _.WorktreeRemove(dir: string, path: string, force: bool) =
        task {
            match checkFlags BINARY [ "worktree path", path ] with
            | Error e -> return Error e
            | Ok() ->
                let cmd = core.CommandIn(dir, [ "worktree"; "remove" ])
                let cmd = if force then cmd.Arg "--force" else cmd
                return! core.RunUnit(cmd.Arg path)
        }

    /// Move a worktree (`worktree move <from> <to>`). Both `fromPath` and `toPath` are bare
    /// positionals, so a leading-`-` value in either would be read as a flag — both are refused
    /// before spawning.
    member _.WorktreeMove(dir: string, fromPath: string, toPath: string) =
        task {
            match checkFlags BINARY [ "worktree source path", fromPath; "worktree destination path", toPath ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit((core.CommandIn(dir, [ "worktree"; "move" ])).Arg(fromPath).Arg toPath)
        }

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

            match checkFlags BINARY checks with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "tag"; name ] @ Option.toList rev))
        }

    /// Create an annotated tag (`tag -a <name> -m <message> [<rev>]`).
    member _.TagCreateAnnotated(dir: string, spec: AnnotatedTag) =
        task {
            let checks =
                ("tag name", spec.Name)
                :: (spec.Rev |> Option.map (fun r -> "revision", r) |> Option.toList)

            match checkFlags BINARY checks with
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
            match checkFlags BINARY [ "tag name", name ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "tag"; "-d"; name ]))
        }

    /// A file's content at a revision (`git show <rev>:<path>`), untrimmed and UTF-8-decoded.
    /// Byte-exact for UTF-8/text blobs (the trailing newline survives a read-modify-write); a
    /// non-UTF-8 byte (a binary or legacy-encoded blob) is replaced with U+FFFD and does NOT
    /// round-trip — use `ShowFileBytes` for a verbatim byte-for-byte read of such content.
    member _.ShowFile(dir: string, rev: string, path: string) =
        task {
            match checkFlags BINARY [ "revision", rev ] with
            | Error e -> return Error e
            | Ok() ->
                // Windows: git rejects backslash separators in the <rev>:<path> spec.
                let p =
                    if OperatingSystem.IsWindows() then
                        path.Replace(char 92, '/')
                    else
                        path

                let spec = sprintf "%s:%s" rev p
                // Untrimmed so a text blob's trailing newline survives; UTF-8-decoded, so this is
                // NOT byte-exact for non-UTF-8 content (see the doc-comment / `ShowFileBytes`).
                return! runUntrimmed core (core.CommandIn(dir, [ "show"; spec ]))
        }

    /// A file's content at a revision as raw, verbatim **bytes** (`git show <rev>:<path>`) —
    /// arbitrary (binary, legacy-encoded, non-UTF-8) content round-trips byte-for-byte, unlike
    /// `ShowFile`, which UTF-8-decodes and replaces any non-UTF-8 byte with U+FFFD. Use this for a
    /// byte-exact read-modify-write of blob content.
    member _.ShowFileBytes(dir: string, rev: string, path: string) =
        task {
            match checkFlags BINARY [ "revision", rev ] with
            | Error e -> return Error e
            | Ok() ->
                // Windows: git rejects backslash separators in the <rev>:<path> spec.
                let p =
                    if OperatingSystem.IsWindows() then
                        path.Replace(char 92, '/')
                    else
                        path

                let spec = sprintf "%s:%s" rev p
                return! runUntrimmedBytes core (core.CommandIn(dir, [ "show"; spec ]))
        }

    /// The value of a config key, or `None` when unset (`config --get <key>`).
    member _.ConfigGet(dir: string, key: string) =
        task {
            match checkFlags BINARY [ "config key", key ] with
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

    /// Set a config key in the repository's local config (`config -- <key> <value>`). Unlike the
    /// name/revision slots, a config `value` may legitimately begin with `-` (e.g. `-1`), so it
    /// cannot be refused via the leading-`-` guard. Instead an end-of-options `--` separator is
    /// emitted before the positionals: `git config` has no pathspec semantics, so `--`
    /// unambiguously terminates option parsing and a `value` like `-1` is taken verbatim rather
    /// than parsed as a flag (`--global`/`--unset`/… would otherwise redirect or subvert the
    /// write). `--` is chosen over `--end-of-options` deliberately — it is honoured by
    /// `git config`'s option parser across the toolkit's whole supported git 2.x range, whereas
    /// `--end-of-options` requires git >= 2.24 — and mirrors the end-of-options convention already
    /// used elsewhere in this client (`checkout … --`, `add -- …`). The `key` stays guarded (a
    /// config key never legitimately begins with `-`).
    member _.ConfigSet(dir: string, key: string, value: string) =
        task {
            match checkFlags BINARY [ "config key", key ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "config"; "--"; key; value ]))
        }

    /// Add a remote (`remote add <name> <url>`).
    member _.RemoteAdd(dir: string, name: string, url: string) =
        task {
            match checkFlags BINARY [ "remote name", name; "url", url ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "remote"; "add"; name; url ]))
        }

    /// Change a remote's URL (`remote set-url <name> <url>`).
    member _.RemoteSetUrl(dir: string, name: string, url: string) =
        task {
            match checkFlags BINARY [ "remote name", name; "url", url ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(core.CommandIn(dir, [ "remote"; "set-url"; name; url ]))
        }

    /// Per-line authorship of `path` (`blame --line-porcelain [<rev>] -- <path>`).
    member _.Blame(dir: string, path: string, rev: string option) =
        task {
            let guard =
                match rev with
                | Some r -> checkFlags BINARY [ "revision", r ]
                | None -> Ok()

            match guard with
            | Error e -> return Error e
            | Ok() ->
                let args = [ "blame"; "--line-porcelain" ] @ Option.toList rev @ [ "--"; path ]
                // Untrimmed feed (UTF-8-decoded porcelain text): a file ending in a blank line has
                // a final `\t` (empty) content line, and `parseBlamePorcelain` closes a record only
                // on that `\t`-prefixed line — trimming it (as `core.Parse` does) would silently
                // drop the last blame entry. Mirrors the jj `FileAnnotate` workaround.
                match! runUntrimmed core (core.CommandIn(dir, args)) with
                | Error e -> return Error e
                | Ok out -> return Ok(GitParse.parseBlamePorcelain out)
        }

    // --- Sequencer -----------------------------------------------------------

    /// Apply a commit onto the current branch (`cherry-pick <rev>`).
    member _.CherryPick(dir: string, rev: string) =
        task {
            match checkFlags BINARY [ "revision", rev ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(noEditor (cLocale (core.CommandIn(dir, [ "cherry-pick"; rev ]))))
        }

    /// Revert a commit with the default message (`revert --no-edit <rev>`).
    member _.Revert(dir: string, rev: string) =
        task {
            match checkFlags BINARY [ "revision", rev ] with
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
    /// argument, and the raw `Run`/`RunRaw` hatches run in the bound `dir` too (see `GitAt`).
    member this.At(dir: string) : GitAt = GitAt(this, dir)

/// A `Git` client with a working directory bound, so calls drop the leading `dir`
/// argument — `git.At(dir).Status()` is `git.Status dir`. Construct one with `Git.At`
/// (or, through the facade, `Repo.GitAt`). Cheap to construct: it only holds the client
/// and the path.
///
/// Every method — the *modelled* `dir` forwarders AND the raw `Run`/`RunRaw` escape hatches —
/// runs in the bound `dir`: the modelled methods inject it as their leading argument, and the
/// hatches forward to `git.Run(dir, …)`/`git.RunRaw(dir, …)`. For a raw command that must run
/// in the process's current directory instead, call `Run`/`RunRaw` on the unbound `Git` client.
and [<Sealed>] GitAt internal (git: Git, dir: string) =

    // --- Escape hatches (bound to `dir`) -------------------------------------

    /// Run `git <args>` in the bound `dir`, returning trimmed stdout.
    member _.Run(args: string seq) = git.Run(dir, args)

    /// Like `Run` but never errors on a non-zero exit — returns the captured result.
    member _.RunRaw(args: string seq) = git.RunRaw(dir, args)

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

    /// Like `Log`, but scoped to commits that touched `paths`.
    member _.LogPaths(revspec: string, max: int, paths: string list) = git.LogPaths(dir, revspec, max, paths)

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

    /// This repository's empty-tree object id, in its actual object format.
    member _.EmptyTreeOid() = git.EmptyTreeOid dir

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

    // Both read-only `ls-remote` queries inherit the caller's `DefaultTimeout` and have no
    // `TimeoutGrace`: unlike fetch/push, they cannot leave local or remote state half-applied.
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

    /// The detached counterpart of `IsMergeInProgress` — probes on a fresh cancellation
    /// budget instead of this client's own token.
    member _.IsMergeInProgressDetached() = git.IsMergeInProgressDetached dir

    /// Whether a `git am` (mailbox apply) is in progress.
    member _.IsAmInProgress() = git.IsAmInProgress dir

    /// Whether a cherry-pick is in progress (`CHERRY_PICK_HEAD` under the git dir).
    member _.IsCherryPickInProgress() = git.IsCherryPickInProgress dir

    /// Whether a revert is in progress (`REVERT_HEAD` under the git dir).
    member _.IsRevertInProgress() = git.IsRevertInProgress dir

    /// Whether a `git bisect` session is in progress (`BISECT_LOG` under the git dir).
    member _.IsBisectInProgress() = git.IsBisectInProgress dir

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

    /// The detached counterpart of `MergeAbort` — runs on a fresh cancellation budget
    /// instead of this client's own token.
    member _.MergeAbortDetached() = git.MergeAbortDetached dir

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

    /// Abort an in-progress cherry-pick (`cherry-pick --abort`).
    member _.CherryPickAbort() = git.CherryPickAbort dir

    /// Continue a cherry-pick after resolving conflicts (`cherry-pick --continue`).
    member _.CherryPickContinue() = git.CherryPickContinue dir

    /// Abort an in-progress revert (`revert --abort`).
    member _.RevertAbort() = git.RevertAbort dir

    /// Continue a revert after resolving conflicts (`revert --continue`).
    member _.RevertContinue() = git.RevertContinue dir

    /// End a `git bisect` session (`bisect reset`).
    member _.BisectReset() = git.BisectReset dir

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

    /// A file's content at a revision (`git show <rev>:<path>`), UTF-8-decoded (non-UTF-8 bytes
    /// become U+FFFD — use `ShowFileBytes` for verbatim binary content).
    member _.ShowFile(rev: string, path: string) = git.ShowFile(dir, rev, path)

    /// A file's content at a revision as raw, verbatim bytes (`git show <rev>:<path>`).
    member _.ShowFileBytes(rev: string, path: string) = git.ShowFileBytes(dir, rev, path)

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
