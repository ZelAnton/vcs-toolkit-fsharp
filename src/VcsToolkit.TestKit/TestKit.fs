namespace VcsToolkit.TestKit

open System
open System.Diagnostics
open System.IO
open System.Threading

// Throwaway git/jj sandboxes (and a bare remote) for integration tests. Hands a test a
// real repository to drive: a unique self-cleaning `TempDir`, a configured `GitSandbox` /
// `JjSandbox` to build scenarios in, and a seeded `BareRemote` to clone/fetch/push
// against. Dependency-free (not even the wrapper libraries, so it can be a test dependency
// of any of them without a cycle), synchronous, and raising on failure (a broken fixture
// should fail loudly at the call site). The helpers run the actual `git`/`jj` on PATH.

/// Shared, isolated command plumbing. Internal — the public surface is the types below and
/// the `Raw` module.
[<AutoOpen>]
module internal Internals =

    let private counter = ref 0L

    /// A process-wide monotonic counter, so parallel tests in a run never collide.
    let nextCount () = Interlocked.Increment counter

    /// A path that cannot exist: a child of *this* process's own executable (a file, so it
    /// can have no children). Used to redirect git/jj at a guaranteed-missing config file;
    /// both treat a missing config as empty, so no temp file is needed.
    let private nonexistentConfigPath () =
        let baseP =
            match Environment.ProcessPath with
            | null -> "vcs-testkit-no-such"
            | p -> p

        Path.Combine(baseP, "vcs-testkit-nonexistent-config")

    /// Build an isolated `ProcessStartInfo` for `binary` in `cwd`. **Every** git/jj
    /// invocation routes through here so the sandbox is hermetic — it must not inherit the
    /// host user's VCS config. A host-global `init.templateDir`/`core.hooksPath` (git) or
    /// `[user]` block (jj) would otherwise leak in.
    let private buildStartInfo (binary: string) (cwd: string) (capture: bool) : ProcessStartInfo =
        let psi =
            ProcessStartInfo(FileName = binary, WorkingDirectory = cwd, UseShellExecute = false)
        // Only redirect when capturing — an unread redirected stream can deadlock a
        // chatty child, and `run` inherits the parent's stdio (git's `-q` keeps it quiet).
        psi.RedirectStandardOutput <- capture
        psi.RedirectStandardError <- capture
        let nonexistent = nonexistentConfigPath ()

        match binary with
        | "git" ->
            // Ignore system config; redirect global/system config at a nonexistent file
            // (defeats a host-set GIT_CONFIG_GLOBAL too); never block on a credential
            // prompt. Scrub any inherited GIT_DIR-style vars that point git elsewhere.
            psi.Environment.["GIT_CONFIG_NOSYSTEM"] <- "1"
            psi.Environment.["GIT_CONFIG_GLOBAL"] <- nonexistent
            psi.Environment.["GIT_CONFIG_SYSTEM"] <- nonexistent
            psi.Environment.["GIT_TERMINAL_PROMPT"] <- "0"

            for k in
                [ "GIT_CONFIG_PARAMETERS"
                  "GIT_CONFIG"
                  "GIT_DIR"
                  "GIT_COMMON_DIR"
                  "GIT_WORK_TREE"
                  "GIT_INDEX_FILE"
                  "GIT_OBJECT_DIRECTORY"
                  "GIT_NAMESPACE" ] do
                psi.Environment.Remove k |> ignore
        | "jj" ->
            // Read config exclusively from a nonexistent file (no host config), and stamp
            // a deterministic identity on *every* commit — including the working-copy
            // commit `jj git init` creates, which a later `config set --repo user.*`
            // cannot retroactively re-author.
            psi.Environment.["JJ_CONFIG"] <- nonexistent
            psi.Environment.["JJ_USER"] <- "test"
            psi.Environment.["JJ_EMAIL"] <- "test@example.com"
        | _ -> ()

        psi

    let private describeArgs (args: string list) = String.Join(" ", args)

    let private startOrFail (binary: string) (args: string list) (psi: ProcessStartInfo) : Process =
        let started =
            try
                Process.Start psi
            with e ->
                // A missing binary or a spawn failure: fail loudly with the command line.
                failwithf "failed to run `%s %s`: %s" binary (describeArgs args) e.Message

        match started with
        | null -> failwithf "failed to run `%s %s`: process did not start" binary (describeArgs args)
        | p -> p

    /// Run a binary in `cwd`, raising (with the command line) on a spawn failure or
    /// non-zero exit. The fixture contract: fail loudly.
    let run (binary: string) (cwd: string) (args: string list) : unit =
        let psi = buildStartInfo binary cwd false

        for a in args do
            psi.ArgumentList.Add a

        use proc = startOrFail binary args psi
        proc.WaitForExit()

        if proc.ExitCode <> 0 then
            failwithf "`%s %s` exited with %d" binary (describeArgs args) proc.ExitCode

    /// Like `run` but capturing trimmed stdout.
    let runCapture (binary: string) (cwd: string) (args: string list) : string =
        let psi = buildStartInfo binary cwd true

        for a in args do
            psi.ArgumentList.Add a

        use proc = startOrFail binary args psi
        // Read both streams concurrently, then wait, so neither full pipe deadlocks.
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()
        proc.WaitForExit()
        let stdout = stdoutTask.Result
        let stderr = stderrTask.Result

        if proc.ExitCode <> 0 then
            failwithf "`%s %s` exited with %d: %s" binary (describeArgs args) proc.ExitCode stderr

        stdout.TrimEnd()

    /// Stamp a git repo at `dir` with a deterministic identity and byte-stable behaviour.
    let configureIdentityAt (dir: string) =
        for key, value in
            [ "user.name", "Test"
              "user.email", "test@example.com"
              "commit.gpgsign", "false"
              "core.autocrlf", "false" ] do
            run "git" dir [ "config"; key; value ]

/// A unique temporary directory, removed on `Dispose`.
///
/// Unique without a temp-dir library: process id + a process-wide monotonic counter, so
/// parallel tests within a run never collide. The name is kept deliberately short — jj's
/// `op_store` paths are deep, and a long prefix here can tip a nested path over Windows'
/// `MAX_PATH` (260) limit. Every fixture owns one; `use` it (or the owning sandbox).
[<Sealed>]
type TempDir(tag: string) =
    let path =
        let p =
            Path.Combine(Path.GetTempPath(), sprintf "vcs-testkit-%s-%d-%d" tag (Environment.ProcessId) (nextCount ()))

        Directory.CreateDirectory p |> ignore
        p

    /// The directory's path.
    member _.Path = path

    interface IDisposable with
        member _.Dispose() =
            try
                Directory.Delete(path, true)
            with
            | :? IOException
            | :? UnauthorizedAccessException
            | :? DirectoryNotFoundException ->
                // best-effort: a leaked temp dir (e.g. a file still open on Windows, or an
                // already-removed dir) must not fail the test run; the OS reclaims it later.
                ()

/// A throwaway **git** repository: owns its `TempDir`, initialised on branch `main` with a
/// deterministic identity. Scenario-building goes through the raw `Git` escape hatch plus
/// the convenience methods — the sandbox deliberately does not depend on the typed wrapper
/// libraries, so it can be a test dependency of any of them.
[<Sealed>]
type GitSandbox private (dir: TempDir) =

    /// Create and initialise a repository (`git init -b main --template=`, then a
    /// deterministic identity). `--template=` (empty) skips *any* init template, so a
    /// host-global `init.templateDir` cannot seed hooks into `.git/hooks`.
    static member Init(tag: string) : GitSandbox =
        let dir = new TempDir(tag)

        try
            run "git" dir.Path [ "init"; "-q"; "-b"; "main"; "--template=" ]
            configureIdentityAt dir.Path
            new GitSandbox(dir)
        with _ ->
            // construction failed after the temp dir was created — dispose it so a failed
            // fixture doesn't leak a dir (Rust's Drop fires during unwind; an F# `let` won't).
            (dir :> IDisposable).Dispose()
            reraise ()

    /// Create and initialise a repository under the **SHA-256** object format
    /// (`git init -b main --object-format=sha256 --template=`), otherwise identical to
    /// `Init`. For exercising SHA-256-specific behaviour (e.g. that the empty-tree id
    /// resolves to a 64-hex value rather than the SHA-1 hardcoded constant). Raises if
    /// the installed `git` was not built with SHA-256 support — callers on a real-`git`
    /// fixture should catch and `Assert.Ignore` rather than fail the run.
    static member InitSha256(tag: string) : GitSandbox =
        let dir = new TempDir(tag)

        try
            run "git" dir.Path [ "init"; "-q"; "-b"; "main"; "--object-format=sha256"; "--template=" ]
            configureIdentityAt dir.Path
            new GitSandbox(dir)
        with _ ->
            // construction failed after the temp dir was created — dispose it so a failed
            // fixture doesn't leak a dir (Rust's Drop fires during unwind; an F# `let` won't).
            (dir :> IDisposable).Dispose()
            reraise ()

    /// The repository's working-tree path.
    member _.Path = dir.Path

    /// Run `git <args>` in the repository, raising on failure.
    member _.Git(args: string list) = run "git" dir.Path args

    /// Write `content` to the repo-relative `path` (creating parent dirs).
    member _.Write(path: string, content: string) =
        let full = Path.Combine(dir.Path, path)

        match Path.GetDirectoryName full with
        | null
        | "" -> ()
        | parent -> Directory.CreateDirectory parent |> ignore

        File.WriteAllText(full, content)

    /// Stage everything (`git add -A`).
    member this.AddAll() = this.Git [ "add"; "-A" ]

    /// Commit the staged changes (`git commit -qm <message>`).
    member this.Commit(message: string) = this.Git [ "commit"; "-qm"; message ]

    /// Write + stage + commit one file — the everyday scenario step.
    member this.CommitFile(path: string, content: string, message: string) =
        this.Write(path, content)
        this.AddAll()
        this.Commit message

    /// Create a branch at HEAD without switching (`git branch <name>`).
    member this.Branch(name: string) = this.Git [ "branch"; "-q"; name ]

    /// Switch to a branch (`git checkout <name>`).
    member this.Checkout(name: string) = this.Git [ "checkout"; "-q"; name ]

    /// Resolve a revision to a full hash (`git rev-parse <rev>`).
    member _.RevParse(rev: string) : string =
        runCapture "git" dir.Path [ "rev-parse"; rev ]

    interface IDisposable with
        member _.Dispose() = (dir :> IDisposable).Dispose()

/// A populated **bare** git repository — a local clone/fetch/push source for integration
/// tests (no network). Seeded with one commit on `main` containing `seed.txt`.
[<Sealed>]
type BareRemote private (dir: TempDir, bare: string) =

    /// Build the seeded bare repository.
    static member Seeded(tag: string) : BareRemote =
        let dir = new TempDir(tag)

        try
            let work = Path.Combine(dir.Path, "seed-work")
            let bare = Path.Combine(dir.Path, "remote.git")
            Directory.CreateDirectory work |> ignore
            Directory.CreateDirectory bare |> ignore
            run "git" work [ "init"; "-q"; "-b"; "main"; "--template=" ]
            configureIdentityAt work
            File.WriteAllText(Path.Combine(work, "seed.txt"), "seed\n")
            run "git" work [ "add"; "-A" ]
            run "git" work [ "commit"; "-qm"; "seed" ]
            run "git" bare [ "init"; "-q"; "--bare"; "-b"; "main"; "--template=" ]
            run "git" work [ "push"; "-q"; bare; "main:main" ]
            new BareRemote(dir, bare)
        with _ ->
            // construction failed after the temp dir was created — dispose it so a failed
            // fixture doesn't leak a dir (Rust's Drop fires during unwind; an F# `let` won't).
            (dir :> IDisposable).Dispose()
            reraise ()

    /// The bare repository's path (use as a local remote URL).
    member _.Path = bare

    /// The path as a `string` — convenient for argv lists.
    member _.Url = bare

    /// The owning temp dir (kept alive as long as the remote is used).
    member _.TempDir = dir.Path

    interface IDisposable with
        member _.Dispose() = (dir :> IDisposable).Dispose()

/// A throwaway **jj** repository (git-backed) with a repo-scoped identity.
[<Sealed>]
type JjSandbox private (dir: TempDir) =

    /// Create and initialise the repository (`jj git init` + repo-scoped identity). The
    /// identity is supplied to *every* jj invocation as `JJ_USER`/`JJ_EMAIL` env, so the
    /// working-copy commit `jj git init` creates is authored deterministically.
    static member Init(tag: string) : JjSandbox =
        let dir = new TempDir(tag)

        try
            run "jj" dir.Path [ "git"; "init" ]
            run "jj" dir.Path [ "config"; "set"; "--repo"; "user.name"; "Test" ]
            run "jj" dir.Path [ "config"; "set"; "--repo"; "user.email"; "test@example.com" ]
            new JjSandbox(dir)
        with _ ->
            // construction failed after the temp dir was created — dispose it so a failed
            // fixture doesn't leak a dir (Rust's Drop fires during unwind; an F# `let` won't).
            (dir :> IDisposable).Dispose()
            reraise ()

    /// The workspace root path.
    member _.Path = dir.Path

    /// Run `jj <args>` in the workspace, raising on failure.
    member _.Jj(args: string list) = run "jj" dir.Path args

    /// Write `content` to the workspace-relative `path` (creating parents).
    member _.Write(path: string, content: string) =
        let full = Path.Combine(dir.Path, path)

        match Path.GetDirectoryName full with
        | null
        | "" -> ()
        | parent -> Directory.CreateDirectory parent |> ignore

        File.WriteAllText(full, content)

    /// Describe the working-copy change (`jj describe -m <message>`).
    member this.Describe(message: string) = this.Jj [ "describe"; "-m"; message ]

    /// Start a new change on top (`jj new -m <message>`).
    member this.NewChange(message: string) = this.Jj [ "new"; "-m"; message ]

    /// Create a bookmark at `@` (`jj bookmark create <name> -r @`).
    member this.Bookmark(name: string) =
        this.Jj [ "bookmark"; "create"; name; "-r"; "@" ]

    interface IDisposable with
        member _.Dispose() = (dir :> IDisposable).Dispose()

/// Raw `git`/`jj` steps for directories no sandbox owns (linked worktrees, fresh clones,
/// repos the code under test initialised) — each runs one command in `dir`, raising on
/// failure. Hermetic: they get the same host-config isolation as the sandbox methods.
[<RequireQualifiedAccess>]
module Raw =

    /// Run `git <args>` in `dir`, raising on failure.
    let git (dir: string) (args: string list) = run "git" dir args

    /// Run `jj <args>` in `dir`, raising on failure.
    let jj (dir: string) (args: string list) = run "jj" dir args

    /// Give the git repository at `dir` a deterministic identity and byte-stable behaviour
    /// (`user.*`, `commit.gpgsign=false`, `core.autocrlf=false`). Standalone, for tests
    /// whose *subject* is repository initialisation itself.
    let configureIdentity (dir: string) = configureIdentityAt dir
