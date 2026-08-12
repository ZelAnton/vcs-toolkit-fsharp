module VcsToolkit.TestKit.Tests

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open NUnit.Framework
open VcsToolkit.TestKit

/// Whether a probe (a `<binary> --version` call) runs without raising — i.e. the binary is
/// on PATH.
let private binaryAvailable (probe: unit -> unit) : bool =
    try
        probe ()
        true
    with _ ->
        // the binary isn't on PATH (or failed to spawn) — the guarded test can't run.
        false

let private requireBinary (name: string) (probe: unit -> unit) =
    if not (binaryAvailable probe) then
        let message = $"{name} not available on PATH"

        if name = "jj" && Environment.GetEnvironmentVariable "REQUIRE_JJ" = "1" then
            Assert.Fail $"REQUIRE_JJ=1 but {message}"
        else
            Assert.Ignore message

let private assertPathArgumentException (path: string) (action: string -> unit) =
    let caughtException =
        Assert.Throws<ArgumentException>(Action(fun () -> action path))

    match caughtException with
    | null -> raise (InvalidOperationException "Assert.Throws returned null unexpectedly")
    | caught -> Assert.That(caught.ParamName, Is.EqualTo "path")

let private isWithinTempRoot (path: string) =
    let root = Path.GetFullPath(Path.GetTempPath())
    let candidate = Path.GetFullPath path
    let relative = Path.GetRelativePath(root, candidate)
    let parentPrefix separator = ".." + string separator

    not (
        Path.IsPathRooted relative
        || relative = ".."
        || relative.StartsWith(parentPrefix Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || relative.StartsWith(parentPrefix Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
    )

let private tryCreateDirectoryLink (link: string) (target: string) : bool =
    try
        if OperatingSystem.IsWindows() then
            let psi =
                ProcessStartInfo(FileName = "cmd.exe", UseShellExecute = false, CreateNoWindow = true)

            psi.ArgumentList.Add "/c"
            psi.ArgumentList.Add "mklink"
            psi.ArgumentList.Add "/J"
            psi.ArgumentList.Add link
            psi.ArgumentList.Add target

            match Process.Start psi |> Option.ofObj with
            | None -> false
            | Some childProcess ->
                use childProcess = childProcess
                childProcess.WaitForExit()
                childProcess.ExitCode = 0 && Directory.Exists link
        else
            Directory.CreateSymbolicLink(link, target) |> ignore
            Directory.Exists link
    with
    | :? UnauthorizedAccessException
    | :? IOException
    | :? PlatformNotSupportedException ->
        // Link creation can be unavailable when the platform or test account disallows it.
        false
    | :? Win32Exception ->
        // A missing Windows command processor means the junction probe cannot run.
        false

// ---------------------------------------------------------------------------
// TempDir — hermetic (needs no binary)
// ---------------------------------------------------------------------------

[<TestFixture>]
type TempDirTests() =

    [<Test>]
    member _.UniqueAndRemovedOnDispose() =
        let a = new TempDir("unique")
        let b = new TempDir("unique")

        try
            Assert.That(a.Path, Is.Not.EqualTo b.Path, "two temp dirs never collide")
            Assert.That(Directory.Exists a.Path, Is.True)
            Assert.That(Directory.Exists b.Path, Is.True)

            let kept = a.Path
            (a :> IDisposable).Dispose()
            Assert.That(Directory.Exists kept, Is.False, "removed on dispose")
        finally
            (b :> IDisposable).Dispose()

    [<Test>]
    member _.PathIsUnderTempAndTagged() =
        let dir = new TempDir("tagme")
        let kept = dir.Path

        try
            Assert.That(Path.GetFileName kept, Does.StartWith "vcs-testkit-tagme-")
            Assert.That(isWithinTempRoot kept, Is.True, "safe tags stay under the canonical temp root")
            Assert.That(Directory.Exists kept, Is.True)
        finally
            (dir :> IDisposable).Dispose()

        Assert.That(Directory.Exists kept, Is.False, "safe tags retain self-cleaning disposal")

    [<Test>]
    member _.TraversalAndRootedTagsCannotEscapeTempRoot() =
        let tempRoot = Path.GetFullPath(Path.GetTempPath())

        let tempParent =
            Directory.GetParent(tempRoot)
            |> Option.ofObj
            |> Option.map (fun parent -> parent.FullName)
            |> Option.defaultWith (fun () -> failwith "the OS temp root must have a parent")

        let marker = $"vcs-testkit-tempdir-escape-{Guid.NewGuid():N}"
        let separator = string Path.DirectorySeparatorChar
        let alternateSeparator = string Path.AltDirectorySeparatorChar
        let traversalTag = $"{separator}..{alternateSeparator}..{separator}{marker}"

        let rootedTag =
            Path.GetPathRoot tempRoot
            |> Option.ofObj
            |> Option.map (fun root -> Path.Combine(root, marker))
            |> Option.defaultWith (fun () -> failwith "the OS temp root must have a path root")

        let outsideDirectories () =
            Directory.GetDirectories(tempParent, marker + "-*")

        let disposeAndRemoveOutside (dir: TempDir) =
            (dir :> IDisposable).Dispose()

            for outside in outsideDirectories () do
                if Directory.Exists outside then
                    Directory.Delete(outside, true)
                elif File.Exists outside then
                    File.Delete outside

        Assert.That(outsideDirectories (), Is.Empty, "the unique marker must not pre-exist")

        for tag in [ traversalTag; rootedTag ] do
            let dir = new TempDir(tag)

            try
                Assert.That(isWithinTempRoot dir.Path, Is.True, $"tag stayed contained: {tag}")
                Assert.That(Directory.Exists dir.Path, Is.True)
            finally
                disposeAndRemoveOutside dir

        Assert.That(
            outsideDirectories (),
            Is.Empty,
            "separator and rooted tags must not create a directory beside the temp root"
        )

// ---------------------------------------------------------------------------
// GitSandbox / BareRemote — require the git binary (present on CI runners)
// ---------------------------------------------------------------------------

[<TestFixture>]
type GitSandboxTests() =

    [<Test>]
    member _.BuildsScenarios() =
        requireBinary "git" (fun () -> Raw.git "." [ "--version" ])
        use repo = GitSandbox.Init "sandbox"
        repo.CommitFile("a.txt", "one\n", "first")
        repo.Branch "feature"
        repo.Checkout "feature"
        repo.CommitFile("sub/b.txt", "two\n", "second")

        let head = repo.RevParse "HEAD"
        Assert.That(head.Length, Is.EqualTo 40, "rev-parse yields a full hash")
        Assert.That(head, Is.Not.EqualTo(repo.RevParse "main"), "feature has diverged from main")

    [<Test>]
    member _.HasNoLeakedHooks() =
        requireBinary "git" (fun () -> Raw.git "." [ "--version" ])
        use repo = GitSandbox.Init "hooks"
        repo.CommitFile("a.txt", "one\n", "first")
        let hooks = Path.Combine(repo.Path, ".git", "hooks")

        let enabled =
            if Directory.Exists hooks then
                // git ships `*.sample` hooks (inert); only non-sample files run.
                Directory.GetFiles hooks
                |> Array.filter (fun f -> not (f.EndsWith(".sample", StringComparison.Ordinal)))
            else
                [||]

        Assert.That(enabled, Is.Empty, "sandbox should have no live hooks")

    [<Test>]
    member _.BareRemoteSeedsAndFetches() =
        requireBinary "git" (fun () -> Raw.git "." [ "--version" ])
        use repo = GitSandbox.Init "local"
        repo.CommitFile("a.txt", "one\n", "first")
        use remote = BareRemote.Seeded "origin"
        repo.Git [ "remote"; "add"; "origin"; remote.Url ]
        repo.Git [ "fetch"; "-q"; "origin" ]

        // The seed commit is now fetchable through the tracking ref.
        Assert.That((repo.RevParse "origin/main").Length, Is.EqualTo 40, "seed commit fetched")

    [<Test>]
    member _.WriteRejectsRootedAndTraversalPaths() =
        requireBinary "git" (fun () -> Raw.git "." [ "--version" ])
        use repo = GitSandbox.Init "write-boundary"
        let outsideName = $"vcs-testkit-outside-{Guid.NewGuid():N}.txt"
        let outside = Path.GetFullPath(Path.Combine(repo.Path, "..", outsideName))
        let traversal = Path.Combine("..", outsideName)

        try
            assertPathArgumentException outside (fun path -> repo.Write(path, "blocked\n"))
            assertPathArgumentException traversal (fun path -> repo.Write(path, "blocked\n"))
            Assert.That(File.Exists outside, Is.False, "rejected writes must not create an outside file")
        finally
            if File.Exists outside then
                File.Delete outside

    [<Test>]
    member _.WriteAllowsNestedRepoRelativePaths() =
        requireBinary "git" (fun () -> Raw.git "." [ "--version" ])
        use repo = GitSandbox.Init "write-relative"
        let relative = Path.Combine("nested", "allowed.txt")
        repo.Write(relative, "allowed\n")
        Assert.That(File.ReadAllText(Path.Combine(repo.Path, relative)), Is.EqualTo "allowed\n")

    [<Test>]
    member _.CommitFileRejectsRootedAndTraversalPaths() =
        requireBinary "git" (fun () -> Raw.git "." [ "--version" ])
        use repo = GitSandbox.Init "commit-boundary"
        let outsideName = $"vcs-testkit-commit-outside-{Guid.NewGuid():N}.txt"
        let outside = Path.GetFullPath(Path.Combine(repo.Path, "..", outsideName))
        let traversal = Path.Combine("..", outsideName)

        try
            let commitFile path =
                repo.CommitFile(path, "blocked\n", "blocked")

            assertPathArgumentException outside commitFile
            assertPathArgumentException traversal commitFile
            Assert.That(File.Exists outside, Is.False, "rejected commits must not create an outside file")
        finally
            if File.Exists outside then
                File.Delete outside

    [<Test>]
    member _.WriteAndCommitFileRejectExistingDirectoryLinks() =
        requireBinary "git" (fun () -> Raw.git "." [ "--version" ])
        use repo = GitSandbox.Init "link-boundary"

        let outsideDir =
            Path.Combine(Path.GetTempPath(), $"vcs-testkit-link-target-${Guid.NewGuid():N}")

        let link = Path.Combine(repo.Path, "linked")
        let linkedPath = Path.Combine("linked", "blocked.txt")
        let outsideFile = Path.Combine(outsideDir, "blocked.txt")
        Directory.CreateDirectory outsideDir |> ignore

        try
            if not (tryCreateDirectoryLink link outsideDir) then
                Assert.Ignore "directory-link creation is unavailable on this platform or account"

            assertPathArgumentException linkedPath (fun path -> repo.Write(path, "blocked\n"))
            assertPathArgumentException linkedPath (fun path -> repo.CommitFile(path, "blocked\n", "blocked"))
            Assert.That(File.Exists outsideFile, Is.False, "link traversal must not create an outside file")
        finally
            if File.Exists outsideFile then
                File.Delete outsideFile

            if Directory.Exists outsideDir then
                Directory.Delete(outsideDir, true)

// ---------------------------------------------------------------------------
// JjSandbox — requires the jj binary (skipped locally when it is unavailable)
// ---------------------------------------------------------------------------

[<TestFixture>]
type JjSandboxTests() =

    let realJjRepoStoreRoots () =
        let env name =
            match Environment.GetEnvironmentVariable name with
            | value when String.IsNullOrWhiteSpace value -> None
            | value -> Some(string value)

        let userProfile = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

        let configRoots =
            if OperatingSystem.IsWindows() then
                [ env "APPDATA"
                  env "LOCALAPPDATA"
                  Some(string (Environment.GetFolderPath Environment.SpecialFolder.ApplicationData))
                  Some(string (Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData)) ]
            elif OperatingSystem.IsMacOS() then
                [ env "XDG_CONFIG_HOME"
                  Some(Path.Combine(string userProfile, "Library", "Application Support")) ]
            else
                [ env "XDG_CONFIG_HOME"; Some(Path.Combine(string userProfile, ".config")) ]

        configRoots
        |> List.choose id
        |> List.choose (fun path -> if String.IsNullOrWhiteSpace path then None else Some path)
        |> List.map (fun path -> Path.GetFullPath(Path.Combine(path, "jj", "repos")))
        |> List.distinct

    let storeFingerprint (roots: string list) =
        roots
        |> List.map (fun root ->
            let entries =
                if Directory.Exists root then
                    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    |> Seq.map (fun path ->
                        let info = FileInfo(path)
                        let relative = Path.GetRelativePath(root, path)
                        $"\${relative}|\${info.Length}|\${info.LastWriteTimeUtc.Ticks}")
                    |> Seq.sort
                    |> String.concat "\n"
                else
                    "<absent>"

            $"{root}\n{entries}")
        |> String.concat "\n---\n"

    [<Test>]
    member _.BuildsScenarios() =
        requireBinary "jj" (fun () -> Raw.jj "." [ "--version" ])
        let realStoreRoots = realJjRepoStoreRoots ()
        let realBefore = storeFingerprint realStoreRoots
        use repo = JjSandbox.Init "sandbox"
        // The colocated jj repo has its state dir.
        Assert.That(Directory.Exists(Path.Combine(repo.Path, ".jj")), Is.True, "jj init created .jj")

        Assert.That(
            Directory.Exists(Path.Combine(repo.Path, ".vcs-toolkit-jj-config", "jj", "repos")),
            Is.True,
            "repo-scoped jj config must stay inside the disposable sandbox"
        )

        repo.Jj [ "config"; "set"; "--repo"; "test.vcs-toolkit.hermetic"; "sandbox-only" ]
        let realAfter = storeFingerprint realStoreRoots
        Assert.That(realAfter, Is.EqualTo(realBefore), "real per-user jj repo config must not change")

        // A full scenario builds without raising (each step is a real jj command).
        repo.Write("a.txt", "one\n")
        repo.Describe "base"
        repo.Bookmark "mark"
        repo.NewChange "next"
        Assert.Pass "jj scenario built without error"

    [<Test>]
    member _.WriteRejectsRootedAndTraversalPaths() =
        requireBinary "jj" (fun () -> Raw.jj "." [ "--version" ])
        use repo = JjSandbox.Init "write-boundary"
        let outsideName = $"vcs-testkit-jj-outside-{Guid.NewGuid():N}.txt"
        let outside = Path.GetFullPath(Path.Combine(repo.Path, "..", outsideName))
        let traversal = Path.Combine("..", outsideName)

        try
            assertPathArgumentException outside (fun path -> repo.Write(path, "blocked\n"))
            assertPathArgumentException traversal (fun path -> repo.Write(path, "blocked\n"))
            Assert.That(File.Exists outside, Is.False, "rejected writes must not create an outside file")
        finally
            if File.Exists outside then
                File.Delete outside

    [<Test>]
    member _.WriteAllowsNestedWorkspaceRelativePaths() =
        requireBinary "jj" (fun () -> Raw.jj "." [ "--version" ])
        use repo = JjSandbox.Init "write-relative"
        let relative = Path.Combine("nested", "allowed.txt")
        repo.Write(relative, "allowed\n")
        Assert.That(File.ReadAllText(Path.Combine(repo.Path, relative)), Is.EqualTo "allowed\n")

    [<Test>]
    member _.WriteRejectsExistingDirectoryLinks() =
        requireBinary "jj" (fun () -> Raw.jj "." [ "--version" ])
        use repo = JjSandbox.Init "link-boundary"

        let outsideDir =
            Path.Combine(Path.GetTempPath(), $"vcs-testkit-jj-link-target-${Guid.NewGuid():N}")

        let link = Path.Combine(repo.Path, "linked")
        let linkedPath = Path.Combine("linked", "blocked.txt")
        let outsideFile = Path.Combine(outsideDir, "blocked.txt")
        Directory.CreateDirectory outsideDir |> ignore

        try
            if not (tryCreateDirectoryLink link outsideDir) then
                Assert.Ignore "directory-link creation is unavailable on this platform or account"

            assertPathArgumentException linkedPath (fun path -> repo.Write(path, "blocked\n"))
            Assert.That(File.Exists outsideFile, Is.False, "link traversal must not create an outside file")
        finally
            if File.Exists outsideFile then
                File.Delete outsideFile

            if Directory.Exists outsideDir then
                Directory.Delete(outsideDir, true)

// ---------------------------------------------------------------------------
// Construction failure must not leak the temp dir (only forceable when jj is
// absent, so it skips wherever jj is installed).
// ---------------------------------------------------------------------------

[<TestFixture>]
type ConstructionFailureTests() =

    [<Test>]
    member _.FailedConstructionDisposesTheTempDir() =
        if binaryAvailable (fun () -> Raw.jj "." [ "--version" ]) then
            Assert.Ignore "jj is present, so JjSandbox.Init can't be forced to fail mid-construction"

        let tag = $"leak{Guid.NewGuid():N}"

        let matching () =
            Directory.GetDirectories(Path.GetTempPath(), $"vcs-testkit-{tag}-*")

        let mutable raised = false

        try
            (JjSandbox.Init tag :> IDisposable).Dispose()
        with _ ->
            raised <- true

        Assert.That(raised, Is.True, "Init must raise (fail loudly) when jj is absent")
        Assert.That(matching (), Is.Empty, "a failed construction must leave no temp dir behind")
