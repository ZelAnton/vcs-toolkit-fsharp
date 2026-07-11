namespace VcsToolkit.Jj

open System
open System.Threading
open System.Threading.Tasks
open ProcessKit
open VcsToolkit.CliSupport
open VcsToolkit.Diff

/// jj-specific command shaping shared by the client's methods.
[<AutoOpen>]
module private JjHelpers =

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

    /// jj treats a bare `<NAMES>` / `-b <BOOKMARK>` / `--remote <REMOTE>` argument as a **glob**
    /// pattern (verified on 0.42: `bookmark delete '*'` deletes every bookmark; `git push -b '*'`
    /// pushes them all), so a name containing `*`/`?` — or a hostile `"*"` from a UI/bot — would
    /// fan the operation across every matching ref. `exact:` forces a literal match of exactly
    /// this name (a literal `*` in a name is then matched verbatim), so the typed methods mutate
    /// exactly the one ref the caller named.
    let exact (name: string) : string = "exact:" + name

    /// `exact:<name>@<remote>` (`BookmarkTrack`'s positional target) parses `exact:` as a
    /// string-pattern prefix on the whole `<name>@<remote>` token, but jj still splits that
    /// token on `@` and matches the remote segment positionally — a glob metacharacter in
    /// `remote` (verified on 0.42) is still glob-matched and can fan the track across every
    /// matching remote, or (with `exact:` misapplied to just the remote) silently track
    /// nothing. There is no `exact:`-on-remote form, so refuse `* ? [ ]` in `remote` outright
    /// before spawning, instead.
    let rejectGlobLike (program: string) (what: string) (value: string) : Result<unit, ProcessError> =
        let hasGlobChar = value |> Seq.exists (fun ch -> "*?[]".IndexOf(ch) >= 0)

        if hasGlobChar then
            Error(
                ProcessError.Spawn(
                    program,
                    sprintf
                        "%s \"%s\" contains a glob metacharacter (one of `* ? [ ]`) — refusing to pass it as a remote name"
                        what
                        value
                )
            )
        else
            Ok()

    /// R7: whether `dest` is one a clone could have *created* — absent, unreadable, or an empty
    /// directory — vs a non-empty pre-existing dir (the caller's data, which jj/git refuses to
    /// clone into). Captured **before** the clone so a failure cleans only its own partial output.
    let cloneDestCleanable (dest: string) : bool =
        try
            (not (System.IO.Directory.Exists dest))
            || (System.IO.Directory.EnumerateFileSystemEntries dest |> Seq.isEmpty)
        with _ ->
            true

    /// R7: on a failed clone into a `cleanable` `dest`, best-effort remove the partial output so a
    /// retry isn't blocked by "destination already exists". `timeout_grace` can't prevent the
    /// partial (Windows' job-kill is atomic; the Unix grace is too short for a multi-GB partial).
    /// Never touches a non-`cleanable` dest.
    let cloneCleanupOnError (dest: string) (cleanable: bool) (result: Result<unit, ProcessError>) =
        match result with
        | Error _ when cleanable ->
            try
                if System.IO.Directory.Exists dest then
                    System.IO.Directory.Delete(dest, true)
            with _ ->
                // Best-effort cleanup on the error path; a leftover partial is not fatal.
                ()
        | _ -> ()

        result

    /// The first bookmark name from an `.escape_json()`-framed `BOOKMARKS_TEMPLATE` render;
    /// `None` when the commit carries no local bookmark.
    let firstBookmark (rendered: string) : string option =
        rendered.Trim() |> JjParse.decodeNameList |> List.tryHead

    /// How many `jj workspace root` lookups `WorkspaceRoots` keeps in flight at once
    /// — a cap so a repo with many workspaces doesn't spawn an unbounded burst of
    /// processes, while still overlapping the (fast, network-free) calls.
    [<Literal>]
    let WorkspaceRootsConcurrency = 8

    /// How deep `RollbackTo` probes the op log for divergence before restoring. A
    /// transaction/probe performs only a handful of operations, so the captured op-head sits
    /// comfortably within this window on a clean rollback; if it has been pushed *out* of the
    /// window, a concurrent operation advanced past it and the rollback is refused rather than
    /// clobbering that work. Bounded so the probe is a single, cheap `op log` query. (Two
    /// digits, no `1`, so a test scripting `--limit <depth>` never collides with the
    /// `--limit 1` op-head capture.)
    [<Literal>]
    let RollbackProbeDepth = 32

    /// The self-contained time budget `RollbackTo` gives its cleanup (`op log` + `op
    /// restore`). It runs on a *fresh* cancellation token carrying this timeout — never the
    /// (possibly already-fired) token of the operation whose failure triggered the rollback —
    /// so a cancelled or timed-out closure is still cleaned up.
    let RollbackCleanupTimeout = TimeSpan.FromSeconds 30.0

    /// Whether a forward-slash-normalised path carries a literal `..` segment — i.e.
    /// escapes whatever root it was resolved relative to.
    let private escapesRoot (path: string) : bool =
        path.Split('/') |> Array.exists (fun seg -> seg = "..")

    /// Reject any `ChangedPath` whose `Path`/`OldPath` carries a `..` segment, instead
    /// of silently propagating a raw escape past the workspace-root normalisation.
    /// `Status`/`DiffSummary` already run the underlying `jj diff --summary` FROM the
    /// resolved workspace root (so a normal file can never escape it), so tripping this
    /// is a defensive backstop against unexpected jj output, not the primary safeguard.
    let rejectEscapingPaths (entries: ChangedPath list) : Result<ChangedPath list, ProcessError> =
        let escaping =
            entries
            |> List.tryFind (fun e -> escapesRoot e.Path || (e.OldPath |> Option.exists escapesRoot))

        match escaping with
        | Some e ->
            Error(
                ProcessError.Parse(
                    BINARY,
                    sprintf "jj diff --summary reported a path escaping the workspace root: \"%s\"" e.Path
                )
            )
        | None -> Ok entries

/// The real Jujutsu client: typed async methods that run the real `jj`, parse its
/// templated output, and return structured values. `Jj.Create()` uses the
/// job-backed runner; `Jj.WithRunner` injects a fake one for tests. Wraps a
/// `ManagedClient` (enable lock-contention retry with `WithRetry`).
///
/// Remote authentication is ambient: unlike the git client, `jj`'s git remote
/// support runs through its own in-process backend with no per-invocation
/// credential override, so `jj git fetch`/`push` authenticate from the ambient git
/// credential helpers / SSH agent. There is deliberately no `Harden` counterpart:
/// jj has no repo-local hooks. In a colocated repo the hook risk lives on the git
/// side — harden the `Git` client you point at it.
///
/// Injection safety: every method placing a caller-supplied bookmark name, revset,
/// operation id, or merge parent in a positional argv slot rejects an empty or
/// `-`-leading value before spawning. Flag-value slots (`-r <revset>`, `-m <msg>`)
/// and the `Run`/`RunRaw` escape hatches are not guarded; for eager validation see
/// `RevsetExpr`.
[<Sealed>]
type Jj private (core: ManagedClient, ignoreWorkingCopy: bool) =

    /// A repo-scoped `jj` command with `--color never` forced on. jj honours
    /// `ui.color = "always"` from user config even when its output is piped, which
    /// would wrap templated output — and the error text the classifiers read — in
    /// ANSI escapes and break parsing; `--color never` is the only thing that
    /// overrides that config. It is a global flag, appended here (no jj subcommand
    /// takes a trailing `--`, so this is safe).
    ///
    /// Used by the **mutating** commands; the read/query commands route through
    /// `cmdInRead`, which additionally honours the client's read-only mode.
    let cmdIn (dir: string) (args: string seq) : Command =
        (core.CommandIn(dir, args)).Arg("--color").Arg("never")

    /// A repo-scoped **read/query** command. On a normal client it is exactly `cmdIn`; on a
    /// read-only client (see `ReadOnly`) it prepends the global `--ignore-working-copy` flag
    /// **before** the subcommand, so the query reports the state of the *last recorded
    /// operation* — jj takes no working-copy lock, imports no bare filesystem edits into a
    /// fresh `@`, and records **no new operation** (`@` stays put). That is the read-only
    /// mode an observer (a `RepoWatcher`, a prompt refresh) needs: reading the repo must not
    /// perturb the state it reports.
    ///
    /// The flag is a hard-coded literal placed ahead of the caller's argv — it can never be
    /// injected from an untrusted revset / bookmark / operation id (a leading-`-` positional
    /// is already rejected by the argv guards), and jj reads it as a global flag regardless
    /// of the subcommand (safe for `file annotate`'s trailing `--`, unlike a trailing global
    /// flag). Only **read** methods route through here; the mutating commands keep using
    /// `cmdIn`, so the read-only mode never alters a mutation's argv.
    let cmdInRead (dir: string) (args: string seq) : Command =
        if ignoreWorkingCopy then
            cmdIn dir (Seq.append [ "--ignore-working-copy" ] args)
        else
            cmdIn dir args

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

    /// Create a client driving the real job-backed runner.
    static member Create() = Jj(ManagedClient.Create BINARY, false)

    /// Create a client driving `runner` — inject a fake in tests.
    static member WithRunner(runner: IProcessRunner) =
        Jj(ManagedClient.WithRunner(BINARY, runner), false)

    // --- Configuration (chainable; each returns a new client) ----------------

    /// Apply a default timeout to every command this client builds.
    member _.DefaultTimeout(timeout: TimeSpan) =
        Jj(core.DefaultTimeout timeout, ignoreWorkingCopy)

    /// Set an environment variable on every command this client builds.
    member _.DefaultEnv(key: string, value: string) =
        Jj(core.DefaultEnv(key, value), ignoreWorkingCopy)

    /// Remove an inherited environment variable on every command this client builds.
    member _.DefaultEnvRemove(key: string) =
        Jj(core.DefaultEnvRemove key, ignoreWorkingCopy)

    /// Cancel every command this client builds when `token` fires.
    member _.DefaultCancelOn(token: CancellationToken) =
        Jj(core.DefaultCancelOn token, ignoreWorkingCopy)

    /// Retry working-copy lock-contention failures per `policy` (opt-in, off by
    /// default). Safe even for mutating commands: a lock-acquisition failure is
    /// pre-execution (jj never ran). jj's operation log already auto-resolves most
    /// concurrency, so hard lock failures are rarer than with git.
    member _.WithRetry(policy: RetryPolicy) =
        Jj(core.WithRetry policy, ignoreWorkingCopy)

    /// A **read-only** view of this client (the analogue of jj's `WorkingCopy::Ignore`): its
    /// read/query methods pass the global `--ignore-working-copy` flag, so a `Status`/`Log`/
    /// `Diff`/bookmark/op-log/workspace query reports the last recorded operation's state
    /// **without** snapshotting the working copy or advancing the operation log (`@` stays
    /// put — no new jj operation). The **mutating** methods (`Describe`/`New`/`Commit`/
    /// `Squash`/`Bookmark…`/`Git…`/`Op…`/…) are deliberately unaffected: they still behave
    /// exactly as on a normal client. Chainable and non-destructive — returns a new client;
    /// this one is unchanged. Used by `VcsToolkit.Watch` so filesystem-driven re-queries
    /// observe the repository without perturbing it.
    ///
    /// Trade-off: because a read-only query does not snapshot, a bare working-tree edit that
    /// no jj command has recorded yet is invisible to it (state is as of the last operation).
    /// A caller that must observe such edits uses the normal (snapshotting) client instead.
    member _.ReadOnly() = Jj(core, true)

    /// Whether this client runs its read/query methods in read-only mode — i.e. whether they
    /// pass the global `--ignore-working-copy` flag (see `ReadOnly`). `false` on a client
    /// from `Create`/`WithRunner`; `true` after `ReadOnly()`.
    member _.IsReadOnly = ignoreWorkingCopy

    // --- Escape hatches / version --------------------------------------------

    /// Run `jj <args>` in the process's current directory, returning trimmed stdout. Unguarded
    /// — never forward untrusted argv (jj's `--config`/aliases can reach code execution). For an
    /// ad-hoc command scoped to a repository, use the `dir`-taking overload (`Run(dir, args)`)
    /// or a bound view's `at(dir).Run(args)`.
    member _.Run(args: string seq) = core.Run(core.Command args)

    /// Run `jj <args>` in `dir`, returning trimmed stdout — the `dir`-bound counterpart of
    /// `Run(args)` (which runs in the process cwd). Backs `JjAt.Run`. Equally unguarded.
    member _.Run(dir: string, args: string seq) = core.Run(core.CommandIn(dir, args))

    /// Like `Run` but never errors on a non-zero exit — returns the captured result.
    member _.RunRaw(args: string seq) = core.Output(core.Command args)

    /// Like `Run(dir, args)` but never errors on a non-zero exit — returns the captured
    /// result. Backs `JjAt.RunRaw`.
    member _.RunRaw(dir: string, args: string seq) = core.Output(core.CommandIn(dir, args))

    /// Installed Jujutsu version (`jj --version`).
    member _.Version() = core.Run(core.Command [ "--version" ])

    /// The installed binary's parsed version, as `JjCapabilities`.
    member this.Capabilities() =
        task {
            match! this.Version() with
            | Error e -> return Error e
            | Ok raw ->
                match JjParse.parseJjVersion raw with
                | Some v -> return Ok { Version = v }
                | None ->
                    return Error(ProcessError.Parse(BINARY, sprintf "unrecognisable `jj --version` output: \"%s\"" raw))
        }

    // --- Changes -------------------------------------------------------------

    /// Parsed working-copy changes — the files changed in `@` (`jj diff -r @ --summary`).
    /// Resolves `dir`'s workspace root first (`Root`) and runs the query FROM it — not
    /// from `dir` itself — so the returned paths are workspace-root-relative regardless
    /// of which subdirectory `dir` names, matching the facade's repo-relative promise
    /// for the same DTO on the git backend. A reported path that still carries a `..`
    /// segment is rejected as an error rather than propagated raw (see `rejectEscapingPaths`).
    member this.Status(dir: string) =
        task {
            match! this.Root dir with
            | Error e -> return Error e
            | Ok root ->
                match!
                    core.Parse(cmdInRead (root.Trim()) [ "diff"; "-r"; "@"; "--summary" ], JjParse.parseDiffSummary)
                with
                | Error e -> return Error e
                | Ok entries -> return rejectEscapingPaths entries
        }

    /// Raw `jj status` text (human-readable) — the unparsed counterpart of `Status`.
    member _.StatusText(dir: string) = core.Run(cmdInRead dir [ "status" ])

    /// Changes matching `revset`, newest first, up to `max` (`jj log`).
    member _.Log(dir: string, revset: string, max: int) =
        let n = sprintf "-n%d" max

        core.Parse(
            cmdInRead dir [ "log"; "-r"; revset; n; "--no-graph"; "-T"; JjParse.CHANGE_TEMPLATE ],
            JjParse.parseChanges
        )

    /// Like `Log`, but scoped to changes that touched `filesets` (`jj log -r <revset> -n<max>
    /// --no-graph -T … <filesets>`) — e.g. "who changed this module". `filesets` are exact-path
    /// `JjFileset`s (`file:"…"`), so a path metacharacter is matched literally rather than parsed
    /// as a fileset operator; they append after the template as jj's own path-scoping arguments (no
    /// argv-chunking is needed — jj, unlike `git log`, filters by revset/fileset natively rather
    /// than expanding paths into argv the way pathspec chunking guards against). An empty `filesets`
    /// is refused **before spawning**: a fileset-less `jj log -r <revset>` is UNRESTRICTED history,
    /// the opposite of "scoped to these paths" (mirrors `CommitPaths`/`SplitPaths`).
    member _.LogPaths(dir: string, revset: string, max: int, filesets: JjFileset list) =
        task {
            if List.isEmpty filesets then
                return
                    Error(
                        ProcessError.Spawn(
                            BINARY,
                            "LogPaths requires at least one fileset — an empty set would log unrestricted history, not history scoped to the named paths"
                        )
                    )
            else
                let n = sprintf "-n%d" max

                let args =
                    [ "log"; "-r"; revset; n; "--no-graph"; "-T"; JjParse.CHANGE_TEMPLATE ]
                    @ (filesets |> List.map (fun f -> f.Value))

                return! core.Parse(cmdInRead dir args, JjParse.parseChanges)
        }

    /// The working-copy change (`jj log -r @`).
    member this.CurrentChange(dir: string) =
        task {
            match! this.Log(dir, "@", 1) with
            | Error e -> return Error e
            | Ok changes ->
                match List.tryLast changes with
                | Some c -> return Ok c
                | None -> return Error(ProcessError.Parse(BINARY, "no working-copy change found"))
        }

    /// Set the working-copy change's description (`jj describe -m`).
    member _.Describe(dir: string, message: string) =
        core.RunUnit(cmdIn dir [ "describe"; "-m"; message ])

    /// Set the description of an arbitrary revision (`jj describe -r <revset> -m`).
    member _.DescribeRev(dir: string, revset: string, message: string) =
        core.RunUnit(cmdIn dir [ "describe"; "-r"; revset; "-m"; message ])

    /// Start a new change on top of the working copy (`jj new -m`).
    member _.NewChange(dir: string, message: string) =
        core.RunUnit(cmdIn dir [ "new"; "-m"; message ])

    // --- Bookmarks -----------------------------------------------------------

    /// Local bookmarks (`jj bookmark list`).
    member _.Bookmarks(dir: string) =
        core.Parse(cmdInRead dir [ "bookmark"; "list"; "-T"; JjParse.BOOKMARK_LIST_TEMPLATE ], JjParse.parseBookmarks)

    /// Local *and* remote-tracking bookmarks (`jj bookmark list -a`).
    member _.BookmarksAll(dir: string) =
        core.Parse(
            cmdInRead dir [ "bookmark"; "list"; "-a"; "-T"; JjParse.BOOKMARK_ALL_TEMPLATE ],
            JjParse.parseBookmarksAll
        )

    /// Local bookmarks on the nearest commits reachable from `@`
    /// (`log -r 'heads(::@ & bookmarks())'`) — the candidate targets a commit
    /// "belongs to". A commit carrying several bookmarks yields one entry each.
    member _.ReachableBookmarks(dir: string) =
        core.Parse(
            cmdInRead
                dir
                [ "log"
                  "-r"
                  "heads(::@ & bookmarks())"
                  "--no-graph"
                  "-T"
                  JjParse.REACHABLE_BOOKMARKS_TEMPLATE ],
            JjParse.parseReachableBookmarks
        )

    /// Track a remote bookmark (`jj bookmark track <name>@<remote>`).
    member _.BookmarkTrack(dir: string, name: string, remote: string) =
        task {
            // A leading-`-` name makes the whole `{name}@{remote}` token start with
            // `-`, which jj parses as a global flag; guard it.
            match checkFlags [ "bookmark name", name ] with
            | Error e -> return Error e
            | Ok() ->
                match rejectGlobLike BINARY "remote" remote with
                | Error e -> return Error e
                | Ok() ->
                    // `exact:` on the whole `name@remote` token stops a `*`/pattern name from
                    // tracking every remote bookmark at once.
                    let target = sprintf "exact:%s@%s" name remote
                    return! core.RunUnit(cmdIn dir [ "bookmark"; "track"; target ])
        }

    /// Point a bookmark at `revision` (`jj bookmark set <name> -r <revision>`).
    member _.BookmarkSet(dir: string, name: string, revision: string) =
        task {
            match checkFlags [ "bookmark name", name ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(cmdIn dir [ "bookmark"; "set"; name; "-r"; revision ])
        }

    /// Create a bookmark at a revision (`bookmark create <name> -r <rev>`).
    member _.BookmarkCreate(dir: string, name: string, revision: string) =
        task {
            match checkFlags [ "bookmark name", name ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(cmdIn dir [ "bookmark"; "create"; name; "-r"; revision ])
        }

    /// Rename a bookmark (`bookmark rename <old> <new>`).
    member _.BookmarkRename(dir: string, oldName: string, newName: string) =
        task {
            match checkFlags [ "bookmark name", oldName; "bookmark name", newName ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(cmdIn dir [ "bookmark"; "rename"; oldName; newName ])
        }

    /// Delete a bookmark (`bookmark delete exact:<name>`). `exact:` so a `*`/pattern name
    /// deletes only the one named, not every matching bookmark.
    member _.BookmarkDelete(dir: string, name: string) =
        task {
            match checkFlags [ "bookmark name", name ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(cmdIn dir [ "bookmark"; "delete"; exact name ])
        }

    /// Move a bookmark to a revision (`bookmark move exact:<name> --to <rev> [--allow-backwards]`).
    member _.BookmarkMove(dir: string, name: string, toRev: string, allowBackwards: bool) =
        task {
            match checkFlags [ "bookmark name", name ] with
            | Error e -> return Error e
            | Ok() ->
                // `exact:` on the name (a `*` would move every bookmark); `toRev` is a revision.
                let args =
                    [ "bookmark"; "move"; exact name; "--to"; toRev ]
                    @ (if allowBackwards then [ "--allow-backwards" ] else [])

                return! core.RunUnit(cmdIn dir args)
        }

    // --- Discovery / identity ------------------------------------------------

    /// Working-copy root of the current workspace (`jj root`).
    member _.Root(dir: string) : Task<Result<string, ProcessError>> = core.Run(cmdInRead dir [ "root" ])

    /// The local bookmark on the working-copy change `@`, if exactly one (or the
    /// first of several); `None` when `@` carries no bookmark.
    member _.CurrentBookmark(dir: string) =
        task {
            match!
                core.Run(
                    cmdInRead
                        dir
                        [ "log"
                          "-r"
                          "@"
                          "--no-graph"
                          "--limit"
                          "1"
                          "-T"
                          JjParse.BOOKMARKS_TEMPLATE ]
                )
            with
            | Error e -> return Error e
            | Ok out -> return Ok(firstBookmark out)
        }

    /// The trunk bookmark (`jj log -r 'trunk()'`); `None` when unresolved.
    member _.Trunk(dir: string) =
        task {
            match!
                core.Run(
                    cmdInRead
                        dir
                        [ "log"
                          "-r"
                          "trunk()"
                          "--no-graph"
                          "--limit"
                          "1"
                          "-T"
                          JjParse.BOOKMARKS_TEMPLATE ]
                )
            with
            | Error e -> return Error e
            | Ok out -> return Ok(firstBookmark out)
        }

    // --- Diff / query / state ------------------------------------------------

    /// Per-file change summary for a range (`diff -r <from>..<to> --summary`). Like
    /// `Status`, resolves `dir`'s workspace root and runs the query FROM it (including
    /// `OldPath` for a rename/copy), so paths are workspace-root-relative regardless of
    /// which subdirectory `dir` names, and an escaping `..` path is rejected as an error.
    member this.DiffSummary(dir: string, fromRev: string, toRev: string) =
        task {
            match! this.Root dir with
            | Error e -> return Error e
            | Ok root ->
                // Parenthesise each endpoint so a compound revset (e.g. `x | y`) keeps its
                // meaning inside the `..` range instead of binding by operator precedence.
                let range = sprintf "(%s)..(%s)" fromRev toRev

                match!
                    core.Parse(cmdInRead (root.Trim()) [ "diff"; "-r"; range; "--summary" ], JjParse.parseDiffSummary)
                with
                | Error e -> return Error e
                | Ok entries -> return rejectEscapingPaths entries
        }

    /// Aggregate change stats for a revset (`diff -r <revset> --stat`).
    member _.DiffStat(dir: string, revset: string) =
        core.Parse(cmdInRead dir [ "diff"; "-r"; revset; "--stat" ], JjParse.parseDiffStat)

    /// Raw git-format unified diff text for `spec` (`diff -r <spec> --git`).
    member _.DiffText(dir: string, spec: DiffSpec) =
        let revset =
            match spec with
            | DiffSpec.WorkingTree -> "@"
            | DiffSpec.Rev rev -> rev

        runUntrimmed (cmdInRead dir [ "diff"; "-r"; revset; "--git" ])

    /// Parsed per-file unified diff for `spec`, layered on `DiffText`.
    member this.Diff(dir: string, spec: DiffSpec) =
        task {
            match! this.DiffText(dir, spec) with
            | Error e -> return Error e
            | Ok text -> return Ok(parseDiff text)
        }

    /// Count commits in a revset (`log -r <revset> --no-graph`, one id per line).
    member _.CommitCount(dir: string, revset: string) =
        core.Parse(
            cmdInRead dir [ "log"; "-r"; revset; "--no-graph"; "-T"; JjParse.COUNT_TEMPLATE ],
            fun s ->
                s.Split('\n')
                |> Array.filter (fun line -> line <> "" && line <> "\r")
                |> Array.length
        )

    /// Whether the commit a revset resolves to has a conflict.
    member _.IsConflicted(dir: string, revset: string) =
        task {
            match!
                core.Run(
                    cmdInRead
                        dir
                        [ "log"
                          "-r"
                          revset
                          "--no-graph"
                          "--limit"
                          "1"
                          "-T"
                          JjParse.CONFLICT_TEMPLATE ]
                )
            with
            | Error e -> return Error e
            | Ok out -> return Ok(out.Trim() = "1")
        }

    /// Whether the working copy has unresolved conflicts.
    member this.HasWorkingCopyConflict(dir: string) = this.IsConflicted(dir, "@")

    /// Paths with unresolved conflicts in `revset` (`jj resolve --list -r <revset>`).
    /// Empty when there are none.
    member _.ResolveList(dir: string, revset: string) =
        task {
            match! core.Output(cmdInRead dir [ "resolve"; "--list"; "-r"; revset ]) with
            | Error e -> return Error e
            | Ok res ->
                match res.Code with
                | Some 0 -> return Ok(JjParse.parseResolveList res.Stdout)
                // jj exits non-zero with "No conflicts found …" when the revision is
                // conflict-free — the one non-zero we read as an empty list. Any other
                // failure (bad revset, not a repo, …) must surface. jj's output is
                // English-only, matched case-insensitively on the stable core phrase.
                | _ when res.Stderr.Contains("no conflicts", StringComparison.OrdinalIgnoreCase) -> return Ok []
                | _ ->
                    match ProcessResult.ensureSuccess res with
                    | Error e -> return Error e
                    | Ok _ -> return Ok [] // unreachable: a non-zero exit always errors above.
        }

    /// Run an arbitrary templated `jj log` query and return raw stdout
    /// (`log -r <revset> --no-graph [--limit n] -T <template>`).
    member _.TemplateQuery(dir: string, revset: string, template: string, limit: int option) =
        let args =
            [ "log"; "-r"; revset; "--no-graph" ]
            @ (match limit with
               | Some n -> [ "--limit"; string n ]
               | None -> [])
            @ [ "-T"; template ]

        // Untrimmed: a template's output can be significant to the trailing byte (a consumer
        // may render or round-trip it), matching the Rust `run_untrimmed`.
        runUntrimmed (cmdInRead dir args)

    /// The full (possibly multiline) description of the commit `revset` resolves to,
    /// trailing whitespace trimmed; empty for an undescribed change. A multi-commit
    /// revset yields only the newest commit's description (`--limit 1`).
    member this.Description(dir: string, revset: string) =
        task {
            // `TemplateQuery` is raw (untrimmed); `description` is a scalar, so strip the
            // trailing newline jj appends after the `description` keyword.
            match! this.TemplateQuery(dir, revset, "description", Some 1) with
            | Error e -> return Error e
            | Ok out -> return Ok(out.TrimEnd())
        }

    /// How the commit a revset resolves to evolved, newest snapshot first, up to
    /// `max` (`jj evolog -r <revset>`) — one `Change` row per recorded predecessor.
    member _.Evolog(dir: string, revset: string, max: int) =
        core.Parse(
            cmdInRead
                dir
                [ "evolog"
                  "-r"
                  revset
                  "--no-graph"
                  "--limit"
                  string max
                  "-T"
                  JjParse.EVOLOG_TEMPLATE ],
            JjParse.parseChanges
        )

    /// Per-line authorship of `path` (`jj file annotate <path> [-r <revset>]`;
    /// `None` = `@`): which change introduced each line.
    member _.FileAnnotate(dir: string, path: string, revset: string option) =
        // `file annotate` takes a plain PATH (not a fileset), so a leading-`-` path
        // would be parsed as a flag. The `--` separator before it keeps even a
        // `-dash.txt` literal safe — but global flags (`--color never`, and the read-only
        // `--ignore-working-copy`) MUST precede `--`, so this builds the command directly
        // (not via `cmdInRead`, which appends `--color never`). The read-only flag is
        // prepended at the very front (before the subcommand), so it precedes `--` too and
        // stays a hard-coded literal the untrusted `path` can never inject.
        let args =
            (if ignoreWorkingCopy then
                 [ "--ignore-working-copy" ]
             else
                 [])
            @ [ "file"; "annotate" ]
            @ (match revset with
               | Some r -> [ "-r"; r ]
               | None -> [])
            @ [ "-T"; JjParse.ANNOTATE_TEMPLATE; "--color"; "never"; "--"; path ]

        task {
            // Parse raw bytes, not the string verb (`Output`): the latter reconstructs stdout
            // from a line buffer, stripping every trailing `\r` (full CRLF→LF normalization) and
            // the final newline — which would destroy the `\r` that `parseAnnotate` is documented
            // to preserve for a CRLF-terminated source line. Rust's `core.parse` feeds raw stdout,
            // so this keeps byte-for-byte parity for the last line.
            match! core.OutputBytes(core.CommandIn(dir, args)) with
            | Error e -> return Error e
            | Ok res ->
                match ProcessResult.ensureSuccess res with
                | Error e -> return Error e
                | Ok ok -> return Ok(JjParse.parseAnnotate (System.Text.Encoding.UTF8.GetString ok.Stdout))
        }

    /// A file's content at a revision (`jj file show -r <revset> file:"<path>"` — the
    /// path is wrapped as an exact-path fileset, so metacharacters stay literal).
    member _.FileShow(dir: string, revset: string, path: string) =
        let fileset = JjFileset.Path path
        // Untrimmed: a blob's trailing newline(s) must survive for a byte-exact read-modify-write.
        runUntrimmed (cmdInRead dir [ "file"; "show"; "-r"; revset; fileset.Value ])

    // --- Mutations -----------------------------------------------------------

    /// Rebase the working copy onto a destination (`rebase -d <onto>`).
    member _.Rebase(dir: string, onto: string) =
        core.RunUnit(cmdIn dir [ "rebase"; "-d"; onto ])

    /// Rebase a whole branch onto a destination (`rebase -b <branch> -d <dest>`).
    member _.RebaseBranch(dir: string, branch: string, dest: string) =
        core.RunUnit(cmdIn dir [ "rebase"; "-b"; branch; "-d"; dest ])

    /// Move the working copy to a revision (`edit <rev>`).
    member _.Edit(dir: string, revset: string) =
        task {
            match checkFlags [ "revset", revset ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(cmdIn dir [ "edit"; revset ])
        }

    /// Start a new, undescribed change as a child of `parent` (`new <parent>`) —
    /// unlike `Edit`, `parent` itself is left untouched; the new change is a fresh
    /// commit stacked on top of it.
    member _.NewChild(dir: string, parent: string) =
        task {
            match checkFlags [ "parent", parent ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(cmdIn dir [ "new"; parent ])
        }

    /// Squash the working copy into a revision (`squash --into <rev>`). When
    /// `useDestinationMessage`, keep the destination's description instead of
    /// combining the two.
    member _.SquashInto(dir: string, into: string, useDestinationMessage: bool) =
        let cmd = cmdIn dir [ "squash"; "--into"; into ]

        let cmd =
            if useDestinationMessage then
                cmd.Arg "--use-destination-message"
            else
                cmd

        core.RunUnit cmd

    /// Finalise a commit from exactly these filesets (`commit -m <message>
    /// <filesets>`); the rest stay in the new working-copy change.
    member _.CommitPaths(dir: string, filesets: JjFileset list, message: string) =
        let args = [ "commit"; "-m"; message ] @ (filesets |> List.map (fun f -> f.Value))
        core.RunUnit(cmdIn dir args)

    /// Squash exactly these filesets from one revision into another
    /// (`squash --from <from> --into <into> [--use-destination-message] <filesets>`).
    member _.SquashPaths(dir: string, spec: SquashPaths) =
        let args =
            [ "squash"; "--from"; spec.From; "--into"; spec.Into ]
            @ (if spec.UseDestinationMessage then
                   [ "--use-destination-message" ]
               else
                   [])
            @ (spec.Filesets |> List.map (fun f -> f.Value))

        core.RunUnit(cmdIn dir args)

    /// Set the working copy's sparse patterns to exactly `patterns`
    /// (`sparse set --clear --add <p>…`); an empty list clears the working copy.
    member _.SparseSet(dir: string, patterns: string list) =
        // `--clear` empties the working copy first, then each `--add` reinstates a
        // pattern — so the working copy ends up holding exactly `patterns`.
        let args =
            [ "sparse"; "set"; "--clear" ]
            @ (patterns |> List.collect (fun p -> [ "--add"; p ]))

        core.RunUnit(cmdIn dir args)

    /// Create a new change with the given parents (`new -m <msg> <p1> <p2> …`).
    member _.NewMerge(dir: string, message: string, parents: string list) =
        task {
            // Parents are bare positionals — a leading-`-` one would be silently
            // consumed as a flag.
            match checkFlags (parents |> List.map (fun p -> "parent", p)) with
            | Error e -> return Error e
            | Ok() ->
                let args = [ "new"; "-m"; message ] @ parents
                return! core.RunUnit(cmdIn dir args)
        }

    /// Abandon a revision (`abandon <rev>`).
    member _.Abandon(dir: string, revset: string) =
        task {
            match checkFlags [ "revset", revset ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(cmdIn dir [ "abandon"; revset ])
        }

    /// Fold working-copy edits into the mutable ancestors that introduced the touched
    /// lines (`absorb [--from <revset>] [<filesets>…]`); empty `filesets` absorbs
    /// everything.
    member _.Absorb(dir: string, from: string option, filesets: JjFileset list) =
        let args =
            [ "absorb" ]
            @ (match from with
               | Some f -> [ "--from"; f ]
               | None -> [])
            @ (filesets |> List.map (fun f -> f.Value))

        core.RunUnit(cmdIn dir args)

    /// Split exactly these filesets out of `@` into their own commit described by
    /// `message` (`split -m <message> <filesets>…`); the remainder stays behind.
    /// `filesets` must be non-empty — a fileset-less split opens jj's interactive
    /// diff editor (a headless hang), so it is refused before spawning.
    member _.SplitPaths(dir: string, filesets: JjFileset list, message: string) =
        task {
            if List.isEmpty filesets then
                return
                    Error(
                        ProcessError.Spawn(
                            BINARY,
                            "split requires at least one fileset — an empty split opens jj's interactive diff editor"
                        )
                    )
            else
                // `-m` doubles as the description-editor suppressor.
                let args = [ "split"; "-m"; message ] @ (filesets |> List.map (fun f -> f.Value))
                return! core.RunUnit(cmdIn dir args)
        }

    /// Duplicate the commits a revset resolves to (`duplicate <revset>`).
    member _.Duplicate(dir: string, revset: string) =
        task {
            match checkFlags [ "revset", revset ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(cmdIn dir [ "duplicate"; revset ])
        }

    // --- Git sync ------------------------------------------------------------

    /// Fetch from the git remote (`jj git fetch`); transient (network) failures are
    /// retried (3 attempts, 500 ms backoff).
    member _.GitFetch(dir: string) =
        // Idempotent → retry replays it on a transient failure; graceful
        // terminate-then-kill on a per-client timeout so a timed-out fetch closes cleanly.
        let cmd =
            (cmdIn dir [ "git"; "fetch" ])
                .TimeoutGrace(FetchTimeoutGrace)
                .Retry(FetchAttempts, FetchBackoff, (fun e -> isTransientFetchError e))

        core.RunUnit cmd

    /// Fetch from a *named* git remote (`jj git fetch --remote <remote>`); transient
    /// failures are retried like `GitFetch`.
    member _.GitFetchFrom(dir: string, remote: string) =
        // `--remote` is glob-matched too, so `exact:` keeps a `*` remote from fetching every one.
        let cmd =
            (cmdIn dir [ "git"; "fetch"; "--remote"; exact remote ])
                .TimeoutGrace(FetchTimeoutGrace)
                .Retry(FetchAttempts, FetchBackoff, (fun e -> isTransientFetchError e))

        core.RunUnit cmd

    /// Fetch a single bookmark from origin (`git fetch --remote origin -b <branch>`);
    /// transient failures are retried (3×, 500 ms).
    member _.GitFetchBranch(dir: string, branch: string) =
        // `-b` is glob-matched, so `exact:` keeps a `*` branch from fetching every branch.
        let cmd =
            (cmdIn dir [ "git"; "fetch"; "--remote"; "origin"; "-b"; exact branch ])
                .TimeoutGrace(FetchTimeoutGrace)
                .Retry(FetchAttempts, FetchBackoff, (fun e -> isTransientFetchError e))

        core.RunUnit cmd

    /// Push to the git remote (`jj git push`, optionally `-b exact:<bookmark>`).
    member _.GitPush(dir: string, bookmark: string option) =
        // `-b` is glob-matched, so `exact:` keeps a `*` bookmark from pushing every local one.
        let args =
            [ "git"; "push" ]
            @ (match bookmark with
               | Some name -> [ "-b"; exact name ]
               | None -> [])

        // Graceful terminate-then-kill on a per-client timeout so a timed-out push
        // doesn't leave the remote ref half-updated.
        let cmd = (cmdIn dir args).TimeoutGrace(FetchTimeoutGrace)
        core.RunUnit cmd

    /// Import git refs into jj (`jj git import`) — colocated-repo sync.
    member _.GitImport(dir: string) =
        core.RunUnit(cmdIn dir [ "git"; "import" ])

    /// Clone a git repository into `dest` (`jj git clone <url> <dest>
    /// --colocate|--no-colocate`). Runs without a working directory — pass an
    /// absolute `dest`. The colocate flag is always passed explicitly: whether
    /// colocation is jj's default depends on the jj version *and* the user's
    /// `git.colocate` config, so `colocate` decides deterministically.
    member _.GitClone(url: string, dest: string, colocate: bool) =
        task {
            // `url` and `dest` are both bare positionals in `jj git clone <url> <dest>`: a
            // leading-`-` value in either would be parsed as a flag. Guard both before spawning
            // (a real URL/path never leads with `-`, so no false positives).
            match checkFlags [ "url", url; "destination", dest ] with
            | Error e -> return Error e
            | Ok() ->
                let colocateFlag = if colocate then "--colocate" else "--no-colocate"
                // Capture whether `dest` is ours to clean BEFORE the clone populates it.
                let cleanable = cloneDestCleanable dest

                let cmd =
                    (core.Command [ "git"; "clone"; url ])
                        .Arg(dest)
                        .Arg(colocateFlag)
                        .Arg("--color")
                        .Arg("never")
                        .TimeoutGrace(FetchTimeoutGrace)

                let! result = core.RunUnit cmd
                return cloneCleanupOnError dest cleanable result
        }

    // --- Operation log -------------------------------------------------------

    /// The current operation id (`op log --no-graph --limit 1`) — capture before a
    /// risky sequence to roll back to.
    member _.OpHead(dir: string) =
        core.Run(cmdInRead dir [ "op"; "log"; "--no-graph"; "--limit"; "1"; "-T"; "id.short()" ])

    /// The newest `limit` operations, newest first (`op log --no-graph --limit n`).
    member _.OpLog(dir: string, limit: int) =
        core.Parse(
            cmdInRead
                dir
                [ "op"
                  "log"
                  "--no-graph"
                  "--limit"
                  string limit
                  "-T"
                  JjParse.OP_TEMPLATE ],
            JjParse.parseOperations
        )

    /// Restore the repo to an operation (`op restore <id>`).
    member _.OpRestore(dir: string, opId: string) =
        task {
            match checkFlags [ "operation id", opId ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(cmdIn dir [ "op"; "restore"; opId ])
        }

    /// Undo the latest operation (`op undo`).
    member _.OpUndo(dir: string) =
        core.RunUnit(cmdIn dir [ "op"; "undo" ])

    // --- Workspaces ----------------------------------------------------------

    /// List workspaces (`workspace list`).
    member _.WorkspaceList(dir: string) =
        core.Parse(cmdInRead dir [ "workspace"; "list"; "-T"; JjParse.WORKSPACE_TEMPLATE ], JjParse.parseWorkspaces)

    /// Resolve a workspace's root path (`workspace root [--name <name>]`).
    member _.WorkspaceRoot(dir: string, name: string option) =
        let args =
            [ "workspace"; "root" ]
            @ (match name with
               | Some n -> [ "--name"; n ]
               | None -> [])

        core.Run(cmdInRead dir args)

    /// Add a workspace (`workspace add --name <name> -r <base> <path>`). `spec.Path` is a bare
    /// positional, so a leading-`-` value would be parsed as a flag — it is refused before
    /// spawning. (`Name`/`Base` ride in flag-value slots — `--name`/`-r` — so they are consumed
    /// verbatim and need no guard.)
    member _.WorkspaceAdd(dir: string, spec: WorkspaceAdd) =
        task {
            match checkFlags [ "workspace path", spec.Path ] with
            | Error e -> return Error e
            | Ok() ->
                // Built directly on `CommandIn` (not `cmdIn`) because the trailing
                // `--color never` must come after the chained value args, not between
                // `--name` and its value.
                let cmd =
                    (core.CommandIn(dir, [ "workspace"; "add"; "--name" ])).Arg(spec.Name).Arg("-r").Arg(spec.Base)

                let cmd =
                    match spec.SparsePatterns with
                    | Some mode -> cmd.Arg("--sparse-patterns").Arg(mode.AsArg)
                    | None -> cmd

                let cmd = cmd.Arg(spec.Path).Arg("--color").Arg("never")
                return! core.RunUnit cmd
        }

    /// Forget a workspace (`workspace forget <name>`).
    member _.WorkspaceForget(dir: string, name: string) =
        task {
            match checkFlags [ "workspace name", name ] with
            | Error e -> return Error e
            | Ok() -> return! core.RunUnit(cmdIn dir [ "workspace"; "forget"; name ])
        }

    /// Resolve several workspaces' root paths in one bounded fan-out — one
    /// `jj workspace root --name <n>` per name, at most 8 live at a time. Per-name
    /// `Ok`/`Error` mirrors `WorkspaceRoot` (a non-zero exit or spawn failure →
    /// `Error`); results come back in `names` order.
    member _.WorkspaceRoots(dir: string, names: string list) : Task<Result<string, ProcessError> list> =
        task {
            use sem = new SemaphoreSlim(WorkspaceRootsConcurrency)

            let runOne (n: string) =
                task {
                    do! sem.WaitAsync()

                    try
                        match! core.Output(cmdInRead dir [ "workspace"; "root"; "--name"; n ]) with
                        | Error e -> return Error e
                        | Ok res ->
                            match ProcessResult.ensureSuccess res with
                            | Error e -> return Error e
                            // `TrimEnd` (not `Trim`) for parity with the single
                            // `WorkspaceRoot`, whose `core.Run` trims trailing whitespace.
                            | Ok ok -> return Ok(ok.Stdout.TrimEnd())
                    finally
                        sem.Release() |> ignore
                }

            let! results = names |> List.map runOne |> Task.WhenAll
            return List.ofArray results
        }

    // --- Transactions --------------------------------------------------------

    /// Roll the repo back to a previously captured operation (`capturedOpHead`, from
    /// `OpHead`) — the shared op-log rollback protocol behind `Transaction` and
    /// `Repo.TryMerge`. Two safeguards distinguish it from a bare `OpRestore`:
    ///
    /// * **Fresh cancellation budget.** The cleanup (`op log` + `op restore`) runs on a
    ///   brand-new `CancellationToken` carrying its own timeout (`RollbackCleanupTimeout`),
    ///   *never* inheriting the — possibly already-fired — cancellation of the operation
    ///   whose failure triggered the rollback. So a cancelled or timed-out closure is still
    ///   cleaned up on a live budget.
    ///
    /// * **Divergence guard.** Before restoring, it probes the recent op log (bounded depth,
    ///   `RollbackProbeDepth`). If `capturedOpHead` is no longer visible there, the op-head
    ///   has advanced past it — a concurrent operation — and restoring would discard that
    ///   work; the rollback is *refused* and reported as `SkippedDiverged` instead of
    ///   silently clobbering. When the captured op is still present, it restores and reports
    ///   `RolledBack`.
    ///
    /// The outcome is returned, not swallowed, so a caller can tell "rolled back" from
    /// "left in place because the log diverged". A failed `op log`/`op restore` surfaces as
    /// `Error`.
    member this.RollbackTo(dir: string, capturedOpHead: string) : Task<Result<RollbackOutcome, ProcessError>> =
        task {
            // Fresh budget: a new client whose cleanup commands cancel only on `cts`, with
            // their own timeout — not on the token the failed operation ran under (which may
            // already be cancelled/timed-out, which is exactly when cleanup must still run).
            use cts = new CancellationTokenSource(RollbackCleanupTimeout)

            let cleanup =
                Jj((core.DefaultCancelOn cts.Token).DefaultTimeout RollbackCleanupTimeout, ignoreWorkingCopy)

            // Divergence probe: is the captured op still within the recent op log?
            match! cleanup.OpLog(dir, RollbackProbeDepth) with
            | Error e -> return Error e
            | Ok ops ->
                let ids = ops |> List.map (fun op -> op.Id)

                if List.contains capturedOpHead ids then
                    // Captured op still visible → the op-head is a descendant of it, so the
                    // restore only discards our own work — safe to roll back.
                    match! cleanup.OpRestore(dir, capturedOpHead) with
                    | Error e -> return Error e
                    | Ok() -> return Ok RollbackOutcome.RolledBack
                else
                    // Captured op pushed out of the window → a concurrent operation advanced
                    // past it; refuse rather than clobber, and report the divergence.
                    let current = ids |> List.tryHead |> Option.defaultValue ""
                    return Ok(RollbackOutcome.SkippedDiverged(capturedOpHead, current))
        }

    /// Run a mutation sequence with op-log rollback: capture the current operation
    /// (`OpHead`), run `f` with this client, and on `Error` roll the repo back to the
    /// captured operation via `RollbackTo`. The op log is jj's safety net; this wraps it as
    /// a scope.
    ///
    /// The rollback inherits `RollbackTo`'s guarantees: it runs on a *fresh* cancellation
    /// budget (so a cancelled/timed-out closure is still rolled back) and refuses to restore
    /// if a concurrent operation advanced the op-head past the captured point (rather than
    /// clobbering that work). The closure's own error is always what the caller sees — the
    /// rollback protocol never masks it. If the rollback is refused (divergence) or its `op
    /// restore` fails, the repo may be left mid-transaction and that closure error still
    /// surfaces; call `RollbackTo` directly when you need to observe the rollback outcome.
    member this.Transaction(dir: string, f: Jj -> Task<Result<'T, ProcessError>>) : Task<Result<'T, ProcessError>> =
        task {
            match! this.OpHead dir with
            | Error e -> return Error e
            | Ok pre ->
                match! f this with
                | Ok value -> return Ok value
                | Error err ->
                    // The closure's error is the cause and is what the caller must see, even
                    // when the rollback is refused (divergence) or itself fails; the rollback
                    // outcome is available to direct `RollbackTo` callers.
                    let! _rollback = this.RollbackTo(dir, pre)
                    return Error err
        }

    /// A view of this client bound to `dir`: the modelled methods drop their leading `dir`
    /// argument, and the raw `Run`/`RunRaw` hatches run in the bound `dir` too (see `JjAt`).
    member this.At(dir: string) : JjAt = JjAt(this, dir)

/// A `Jj` client with a working directory bound, so calls drop the leading `dir` argument —
/// `jj.At(dir).Status()` is `jj.Status dir`. Construct one with `Jj.At` (or, through the
/// facade, `Repo.JjAt`). Cheap to construct: it only holds the client and the path.
///
/// Every method — the *modelled* `dir` forwarders AND the raw `Run`/`RunRaw` escape hatches —
/// runs in the bound `dir`: the modelled methods inject it as their leading argument, and the
/// hatches forward to `jj.Run(dir, …)`/`jj.RunRaw(dir, …)`. For a raw command that must run in
/// the process's current directory instead, call `Run`/`RunRaw` on the unbound `Jj` client. As
/// on the client, the hatches are unguarded — never forward untrusted argv.
and [<Sealed>] JjAt internal (jj: Jj, dir: string) =

    // --- Escape hatches (bound to `dir`) -------------------------------------

    /// Run `jj <args>` in the bound `dir`, returning trimmed stdout.
    member _.Run(args: string seq) = jj.Run(dir, args)

    /// Like `Run` but never errors on a non-zero exit — returns the captured result.
    member _.RunRaw(args: string seq) = jj.RunRaw(dir, args)

    /// Installed Jujutsu version (`jj --version`).
    member _.Version() = jj.Version()

    /// The installed binary's parsed version, as `JjCapabilities`.
    member _.Capabilities() = jj.Capabilities()

    /// Clone a git repository into `dest`. Independent of the bound `dir`.
    member _.GitClone(url: string, dest: string, colocate: bool) = jj.GitClone(url, dest, colocate)

    // --- dir forwarders (the bound `dir` is injected as the first argument) ---

    /// Parsed working-copy changes (`jj diff -r @ --summary`).
    member _.Status() = jj.Status dir

    /// Raw `jj status` text (human-readable).
    member _.StatusText() = jj.StatusText dir

    /// Changes matching `revset`, newest first, up to `max` (`jj log`).
    member _.Log(revset: string, max: int) = jj.Log(dir, revset, max)

    /// Like `Log`, but scoped to changes that touched `filesets`.
    member _.LogPaths(revset: string, max: int, filesets: JjFileset list) = jj.LogPaths(dir, revset, max, filesets)

    /// The working-copy change (`jj log -r @`).
    member _.CurrentChange() = jj.CurrentChange dir

    /// Set the working-copy change's description (`jj describe -m`).
    member _.Describe(message: string) = jj.Describe(dir, message)

    /// Set the description of an arbitrary revision (`jj describe -r <revset> -m`).
    member _.DescribeRev(revset: string, message: string) = jj.DescribeRev(dir, revset, message)

    /// Start a new change on top of the working copy (`jj new -m`).
    member _.NewChange(message: string) = jj.NewChange(dir, message)

    /// Local bookmarks (`jj bookmark list`).
    member _.Bookmarks() = jj.Bookmarks dir

    /// Local *and* remote-tracking bookmarks (`jj bookmark list -a`).
    member _.BookmarksAll() = jj.BookmarksAll dir

    /// Local bookmarks on the nearest commits reachable from `@`.
    member _.ReachableBookmarks() = jj.ReachableBookmarks dir

    /// Track a remote bookmark (`jj bookmark track <name>@<remote>`).
    member _.BookmarkTrack(name: string, remote: string) = jj.BookmarkTrack(dir, name, remote)

    /// Point a bookmark at `revision` (`jj bookmark set <name> -r <revision>`).
    member _.BookmarkSet(name: string, revision: string) = jj.BookmarkSet(dir, name, revision)

    /// Fetch from the git remote (`jj git fetch`); transient failures are retried.
    member _.GitFetch() = jj.GitFetch dir

    /// Fetch from a *named* git remote (`jj git fetch --remote <remote>`).
    member _.GitFetchFrom(remote: string) = jj.GitFetchFrom(dir, remote)

    /// Push to the git remote (`jj git push`, optionally `-b exact:<bookmark>`).
    member _.GitPush(bookmark: string option) = jj.GitPush(dir, bookmark)

    /// Working-copy root of the current workspace (`jj root`).
    member _.Root() = jj.Root dir

    /// The local bookmark on the working-copy change `@`, if any.
    member _.CurrentBookmark() = jj.CurrentBookmark dir

    /// The trunk bookmark (`jj log -r 'trunk()'`); `None` when unresolved.
    member _.Trunk() = jj.Trunk dir

    /// Create a bookmark at a revision (`bookmark create <name> -r <rev>`).
    member _.BookmarkCreate(name: string, revision: string) = jj.BookmarkCreate(dir, name, revision)

    /// Rename a bookmark (`bookmark rename <old> <new>`).
    member _.BookmarkRename(oldName: string, newName: string) =
        jj.BookmarkRename(dir, oldName, newName)

    /// Delete a bookmark (`bookmark delete exact:<name>`).
    member _.BookmarkDelete(name: string) = jj.BookmarkDelete(dir, name)

    /// Move a bookmark to a revision (`bookmark move exact:<name> --to <rev>`).
    member _.BookmarkMove(name: string, toRev: string, allowBackwards: bool) =
        jj.BookmarkMove(dir, name, toRev, allowBackwards)

    /// Per-file change summary for a range (`diff -r <from>..<to> --summary`).
    member _.DiffSummary(fromRev: string, toRev: string) = jj.DiffSummary(dir, fromRev, toRev)

    /// Aggregate change stats for a revset (`diff -r <revset> --stat`).
    member _.DiffStat(revset: string) = jj.DiffStat(dir, revset)

    /// Raw git-format unified diff text for `spec` (`diff -r <spec> --git`).
    member _.DiffText(spec: DiffSpec) = jj.DiffText(dir, spec)

    /// Parsed per-file unified diff for `spec`.
    member _.Diff(spec: DiffSpec) = jj.Diff(dir, spec)

    /// Count commits in a revset (`log -r <revset> --no-graph`).
    member _.CommitCount(revset: string) = jj.CommitCount(dir, revset)

    /// Whether the commit a revset resolves to has a conflict.
    member _.IsConflicted(revset: string) = jj.IsConflicted(dir, revset)

    /// Whether the working copy has unresolved conflicts.
    member _.HasWorkingCopyConflict() = jj.HasWorkingCopyConflict dir

    /// Paths with unresolved conflicts in `revset` (`jj resolve --list -r <revset>`).
    member _.ResolveList(revset: string) = jj.ResolveList(dir, revset)

    /// Run an arbitrary templated `jj log` query and return raw stdout.
    member _.TemplateQuery(revset: string, template: string, limit: int option) =
        jj.TemplateQuery(dir, revset, template, limit)

    /// The full description of the commit `revset` resolves to.
    member _.Description(revset: string) = jj.Description(dir, revset)

    /// How the commit a revset resolves to evolved, newest first, up to `max`.
    member _.Evolog(revset: string, max: int) = jj.Evolog(dir, revset, max)

    /// Per-line authorship of `path` (`jj file annotate <path> [-r <revset>]`).
    member _.FileAnnotate(path: string, revset: string option) = jj.FileAnnotate(dir, path, revset)

    /// A file's content at a revision (`jj file show -r <revset> file:"<path>"`).
    member _.FileShow(revset: string, path: string) = jj.FileShow(dir, revset, path)

    /// Fold working-copy edits into the ancestors that introduced the touched lines.
    member _.Absorb(from: string option, filesets: JjFileset list) = jj.Absorb(dir, from, filesets)

    /// Split exactly these filesets out of `@` into their own commit.
    member _.SplitPaths(filesets: JjFileset list, message: string) = jj.SplitPaths(dir, filesets, message)

    /// Duplicate the commits a revset resolves to (`duplicate <revset>`).
    member _.Duplicate(revset: string) = jj.Duplicate(dir, revset)

    /// Rebase the working copy onto a destination (`rebase -d <onto>`).
    member _.Rebase(onto: string) = jj.Rebase(dir, onto)

    /// Rebase a whole branch onto a destination (`rebase -b <branch> -d <dest>`).
    member _.RebaseBranch(branch: string, dest: string) = jj.RebaseBranch(dir, branch, dest)

    /// Move the working copy to a revision (`edit <rev>`).
    member _.Edit(revset: string) = jj.Edit(dir, revset)

    /// Start a new, undescribed change as a child of `parent` (`new <parent>`).
    member _.NewChild(parent: string) = jj.NewChild(dir, parent)

    /// Squash the working copy into a revision (`squash --into <rev>`).
    member _.SquashInto(into: string, useDestinationMessage: bool) =
        jj.SquashInto(dir, into, useDestinationMessage)

    /// Finalise a commit from exactly these filesets (`commit -m <message> <filesets>`).
    member _.CommitPaths(filesets: JjFileset list, message: string) = jj.CommitPaths(dir, filesets, message)

    /// Squash exactly these filesets from one revision into another.
    member _.SquashPaths(spec: SquashPaths) = jj.SquashPaths(dir, spec)

    /// Set the working copy's sparse patterns to exactly `patterns`.
    member _.SparseSet(patterns: string list) = jj.SparseSet(dir, patterns)

    /// Create a new change with the given parents (`new -m <msg> <p1> <p2> …`).
    member _.NewMerge(message: string, parents: string list) = jj.NewMerge(dir, message, parents)

    /// Abandon a revision (`abandon <rev>`).
    member _.Abandon(revset: string) = jj.Abandon(dir, revset)

    /// Fetch a single bookmark from origin (`git fetch --remote origin -b <branch>`).
    member _.GitFetchBranch(branch: string) = jj.GitFetchBranch(dir, branch)

    /// Import git refs into jj (`jj git import`) — colocated-repo sync.
    member _.GitImport() = jj.GitImport dir

    /// The current operation id (`op log --no-graph --limit 1`).
    member _.OpHead() = jj.OpHead dir

    /// The newest `limit` operations, newest first (`op log --no-graph --limit n`).
    member _.OpLog(limit: int) = jj.OpLog(dir, limit)

    /// Restore the repo to an operation (`op restore <id>`).
    member _.OpRestore(opId: string) = jj.OpRestore(dir, opId)

    /// Roll back to a captured operation with a fresh cancellation budget and an op-log
    /// divergence guard (bound form of `Jj.RollbackTo`).
    member _.RollbackTo(capturedOpHead: string) = jj.RollbackTo(dir, capturedOpHead)

    /// Undo the latest operation (`op undo`).
    member _.OpUndo() = jj.OpUndo dir

    /// List workspaces (`workspace list`).
    member _.WorkspaceList() = jj.WorkspaceList dir

    /// Resolve a workspace's root path (`workspace root [--name <name>]`).
    member _.WorkspaceRoot(name: string option) = jj.WorkspaceRoot(dir, name)

    /// The `jj workspace root` for each name, bound to this view's dir. See `Jj.WorkspaceRoots`.
    member _.WorkspaceRoots(names: string list) = jj.WorkspaceRoots(dir, names)

    /// Add a workspace (`workspace add --name <name> -r <base> <path>`).
    member _.WorkspaceAdd(spec: WorkspaceAdd) = jj.WorkspaceAdd(dir, spec)

    /// Forget a workspace (`workspace forget <name>`).
    member _.WorkspaceForget(name: string) = jj.WorkspaceForget(dir, name)

    // --- Transaction (hand-written: the closure is generic) ------------------

    /// Bound form of `Jj.Transaction` (with `dir` pre-bound): run `f` with op-log rollback on
    /// `Error`. The F# `Jj.Transaction` hands its closure the raw `Jj` client, so this forwarder
    /// re-binds it to `dir` as a `JjAt` before invoking `f` — matching the Rust `JjAt.transaction`.
    member _.Transaction(f: JjAt -> Task<Result<'T, ProcessError>>) : Task<Result<'T, ProcessError>> =
        jj.Transaction(dir, (fun (bound: Jj) -> f (JjAt(bound, dir))))
