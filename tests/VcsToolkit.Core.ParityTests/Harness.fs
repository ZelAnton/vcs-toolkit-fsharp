module VcsToolkit.Core.ParityTests.Harness

open System
open System.IO
open NUnit.Framework
open VcsToolkit.Core
open VcsToolkit.TestKit

// The parity harness. A scenario is written once against the uniform `ScenarioRepo` steps and
// replayed - step for step - on a real `GitSandbox` and a real `JjSandbox`, so a fixture can
// compare what the `Repo` facade reports for each. Everything backend-specific (how a commit is
// made, how a conflict is produced, which revision expression names "the history") lives here;
// a fixture only ever sees the two `Repo` handles, which is what keeps a scenario honest - the
// same steps really did run on both sides.
//
// The point of the suite is git-vs-jj comparison, not correctness in isolation: an assertion
// here compares the two backends' facade results with each other. Where the facade documents a
// genuine divergence (`Commit.Author` is git-only, `OperationState` is asymmetric, ...) the
// divergence itself is asserted, so it stays a deliberate, visible contract rather than drift.

/// Which VCS drives one side of a parity pair.
[<RequireQualifiedAccess>]
type ParityBackend =
    /// A plain git repository (`GitSandbox`).
    | Git
    /// A colocated jj repository (`JjSandbox`).
    | Jj

    /// The tool's short name, for assertion messages.
    member this.Name =
        match this with
        | ParityBackend.Git -> "git"
        | ParityBackend.Jj -> "jj"

// --- Binary gates ------------------------------------------------------------

/// git must be on PATH for any fixture here; a host without it skips rather than fails.
let requireGit () =
    try
        Raw.git "." [ "--version" ]
    with _ ->
        // git isn't on PATH — a hermetic CI without it must skip, not fail, this fixture.
        Assert.Ignore "git not available on PATH"

/// jj gate. CI installs jj and sets `REQUIRE_JJ=1`, where a missing jj is a hard failure —
/// a parity suite that silently skipped half of its matrix in CI would be worthless. Local
/// development without jj still skips.
let requireJj () =
    try
        Raw.jj "." [ "--version" ]
    with _ ->
        if Environment.GetEnvironmentVariable "REQUIRE_JJ" = "1" then
            Assert.Fail "REQUIRE_JJ=1 but jj not available on PATH"
        else
            // Local development does not require jj, so the integration fixture remains optional.
            Assert.Ignore "jj not available on PATH"

// --- Assertion helpers -------------------------------------------------------

/// Unwrap a facade result, failing with the backend name and the operation on an error.
let expectOk (backend: string) (operation: string) (result: Result<'a, RepoError>) : 'a =
    match result with
    | Ok value -> value
    | Error e ->
        Assert.Fail $"{operation} failed on {backend}: {e.Message}"
        failwith "unreachable: Assert.Fail always throws"

/// Assert the two backends produced the same value, reporting both sides on a mismatch.
/// Comparison is F# structural `=` rather than `Is.EqualTo`, which is ambiguous under NUnit's
/// overload set for F# collection/option values even with a type annotation (KB K-017).
let assertSame (what: string) (gitValue: 'a) (jjValue: 'a) =
    // The comparison is parenthesised on purpose: a bare `a = b` in an argument position is
    // parsed as a named argument, not as an equality test.
    Assert.That((gitValue = jjValue), Is.True, $"{what} differs between backends: git=%A{gitValue}, jj=%A{jjValue}")

/// Assert two DTO lists match element by element, comparing each element through `describe`
/// (a projection to a readable string, so a mismatch names the offending element). Count is
/// asserted first, so the indexed pass never sees a length mismatch.
let assertSameList (what: string) (describe: 'a -> string) (gitItems: 'a list) (jjItems: 'a list) =
    let gitText = gitItems |> List.map describe
    let jjText = jjItems |> List.map describe

    Assert.That(
        List.length gitItems,
        Is.EqualTo(List.length jjItems),
        $"{what}: element count differs — git=%A{gitText}, jj=%A{jjText}"
    )

    List.iteri2
        (fun i (g: string) (j: string) -> Assert.That(g, Is.EqualTo j, $"{what}: element {i} differs"))
        gitText
        jjText

/// Assert a list equals an expected literal, through F# structural `=` (`Is.EqualTo` on an F#
/// list is ambiguous under NUnit's overload set — KB K-017).
let assertListEquals (what: string) (expected: 'a list) (actual: 'a list) =
    Assert.That((actual = expected), Is.True, $"{what}: expected %A{expected}, got %A{actual}")

/// Assert two values of the same backend agree — an intra-backend invariant, as opposed to
/// `assertSame`'s cross-backend comparison.
let assertEquals (what: string) (expected: 'a) (actual: 'a) =
    Assert.That((actual = expected), Is.True, $"{what}: expected %A{expected}, got %A{actual}")

/// The `/`-separated form of a path — the facade's documented path shape, and the only form in
/// which a Windows-native path (jj renders `jj workspace root` with OS separators) compares to
/// what git prints.
let toSlash (path: string) = path.Replace('\\', '/')

/// Assert a facade-reported path honours the documented shape: repo-relative (never rooted,
/// never a `..` escape) and `/`-separated on every platform.
let assertRepoRelativePath (what: string) (path: string) =
    Assert.That(Path.IsPathRooted path, Is.False, $"{what}: expected a repo-relative path, got an absolute one: {path}")

    Assert.That(
        path.Contains '\\',
        Is.False,
        $"{what}: expected `/` separators on every platform, got a backslash: {path}"
    )

    Assert.That(
        path.Split('/') |> Array.contains "..",
        Is.False,
        $"{what}: expected a path inside the repository, got a `..` escape: {path}"
    )

/// Assert a value is a full-length (40 hex characters) object id — the identity both backends
/// promise for `RepoSnapshot.Head` and `WorktreeInfo.Commit`.
let assertFullObjectId (what: string) (id: string) =
    Assert.That(id.Length, Is.EqualTo 40, $"{what}: expected a full 40-character object id, got '{id}'")

    Assert.That(id |> Seq.forall Uri.IsHexDigit, Is.True, $"{what}: expected a hexadecimal object id, got '{id}'")

// --- The uniform scenario steps ----------------------------------------------

/// The sandbox behind one side of a pair. A DU rather than an interface: the two sandboxes are
/// sealed TestKit types with deliberately different vocabularies (branch vs bookmark, staged
/// commit vs auto-snapshot), and the translation belongs in one visible place.
type SandboxHandle =
    | GitBox of GitSandbox
    | JjBox of JjSandbox

/// One side of a parity pair: a throwaway repository plus the scenario vocabulary both backends
/// implement. Every member is a *scenario step*, never an assertion - the facade under test is
/// driven by the fixtures, not from here.
[<Sealed>]
type ScenarioRepo private (handle: SandboxHandle) =

    /// Create and initialise the sandbox for `backend`.
    static member Create(backend: ParityBackend, tag: string) =
        match backend with
        | ParityBackend.Git -> new ScenarioRepo(GitBox(GitSandbox.Init tag))
        | ParityBackend.Jj -> new ScenarioRepo(JjBox(JjSandbox.Init tag))

    /// Which VCS this side drives.
    member _.Backend =
        match handle with
        | GitBox _ -> ParityBackend.Git
        | JjBox _ -> ParityBackend.Jj

    /// The repository root (the sandbox's working-copy path).
    member _.Path =
        match handle with
        | GitBox g -> g.Path
        | JjBox j -> j.Path

    /// The revision expression naming "the last commit" for this backend. `Repo.ShowFile`/
    /// `ShowFileBytes` take a backend-specific revision (a git commit-ish / a jj revset —
    /// explicitly NOT interchangeable, per the facade docs), so the harness supplies each
    /// backend's own spelling. On jj that is `@-`, not `@`: `@` is the working-copy change and
    /// carries uncommitted edits, where git's `HEAD` never does.
    member _.CommittedRev =
        match handle with
        | GitBox _ -> "HEAD"
        | JjBox _ -> "@-"

    /// The revision expression naming "the committed history", the analogue of `git log HEAD`:
    /// on jj that is the ancestry of the working copy's parent, minus the virtual root commit
    /// jj always carries and git has no counterpart for.
    member _.HistoryRev =
        match handle with
        | GitBox _ -> "HEAD"
        | JjBox _ -> "::@- ~ root()"

    /// Write `content` to the repo-relative `path`, creating parent directories.
    member _.Write(path: string, content: string) =
        match handle with
        | GitBox g -> g.Write(path, content)
        | JjBox j -> j.Write(path, content)

    /// Commit every current working-copy change under `message`. git stages first (`add -A` +
    /// `commit`); jj's `commit` records the working-copy change (which auto-tracks new files)
    /// and leaves a fresh empty change behind — the state git is in right after a commit.
    member _.CommitAll(message: string) =
        match handle with
        | GitBox g ->
            g.AddAll()
            g.Commit message
        | JjBox j -> j.Jj [ "commit"; "-m"; message ]

    /// Rename a tracked file. git only detects a rename through its index, so the git side
    /// stages it (`git mv`) — the equivalent of jj auto-snapshotting the moved file.
    member _.Rename(oldPath: string, newPath: string) =
        match handle with
        | GitBox g -> g.Git [ "mv"; oldPath; newPath ]
        | JjBox j -> File.Move(Path.Combine(j.Path, oldPath), Path.Combine(j.Path, newPath))

    /// Configure a remote (name + URL). No fetch happens: this is a pure configuration read on
    /// both backends, so the URL never has to resolve.
    member _.AddRemote(name: string, url: string) =
        match handle with
        | GitBox g -> g.Git [ "remote"; "add"; name; url ]
        | JjBox j -> j.Jj [ "git"; "remote"; "add"; name; url ]

    /// Give the current working-copy state the name `main` on both backends. `GitSandbox.Init`
    /// already checked out `main`, so only jj needs a step: a bookmark on `@`, which is the
    /// closest jj analogue of git's "the current branch names what the working copy is on".
    /// Call this last in a scenario — a jj bookmark does not advance with later commits.
    member _.NameDefaultBranch() =
        match handle with
        | GitBox _ -> ()
        | JjBox j -> j.Jj [ "bookmark"; "create"; "main"; "-r"; "@" ]

    /// Leave the working copy holding unresolved conflicts in `conflicts`: the basis is committed
    /// first, then two divergent edits are merged. git pauses a real merge (`MERGE_HEAD` + an
    /// unmerged index); jj records the conflicts on a merge change instead — the two models the
    /// facade unifies.
    member this.CreateConflicts(conflicts: (string * string * string * string) list) =
        if List.isEmpty conflicts then
            invalidArg (nameof conflicts) "at least one conflict is required"

        for (path, basis, _, _) in conflicts do
            this.Write(path, basis)

        this.CommitAll "conflict basis"

        match handle with
        | GitBox g ->
            g.Git [ "checkout"; "-q"; "-b"; "right" ]

            for (path, _, _, right) in conflicts do
                g.Write(path, right)

            g.AddAll()
            g.Commit "right side"
            g.Checkout "main"

            for (path, _, left, _) in conflicts do
                g.Write(path, left)

            g.AddAll()
            g.Commit "left side"

            try
                g.Git [ "merge"; "--no-edit"; "right" ]
            with _ ->
                // `git merge` exits non-zero *because* the merge conflicted, and the sandbox
                // raises on a non-zero exit — but a conflicted working copy is precisely the
                // state this step exists to produce. The expected failure is absorbed here; the
                // fixture asserts the conflict actually materialised.
                ()
        | JjBox j ->
            j.Jj [ "bookmark"; "create"; "basis"; "-r"; "@-" ]
            j.Jj [ "new"; "basis"; "-m"; "right side" ]

            for (path, _, _, right) in conflicts do
                j.Write(path, right)

            j.Jj [ "bookmark"; "create"; "right"; "-r"; "@" ]
            j.Jj [ "new"; "basis"; "-m"; "left side" ]

            for (path, _, left, _) in conflicts do
                j.Write(path, left)

            j.Jj [ "bookmark"; "create"; "left"; "-r"; "@" ]
            j.Jj [ "new"; "left"; "right"; "-m"; "merge" ]

    /// Leave the working copy holding one unresolved conflict.
    member this.CreateConflict(path: string, basis: string, left: string, right: string) =
        this.CreateConflicts [ path, basis, left, right ]

    interface IDisposable with
        member _.Dispose() =
            match handle with
            | GitBox g -> (g :> IDisposable).Dispose()
            | JjBox j -> (j :> IDisposable).Dispose()

// --- The pair ----------------------------------------------------------------

/// Open a `Repo` over `dir` through the public auto-detecting entry point — the whole suite goes
/// through `Repo.Open`, never `FromGit`/`FromJj`, so detection and the real client are part of
/// what is being compared.
let openRepo (dir: string) =
    match Repo.Open dir with
    | Ok repo -> repo
    | Error e -> failwith $"Repo.Open '{dir}' failed: {e.Message}"

/// The same scenario materialised on git and on jj, with a `Repo` handle for each.
[<Sealed>]
type ParityPair private (git: ScenarioRepo, jj: ScenarioRepo) =
    // Opened once, after seeding: `Repo.Open` captures the detected root and the bound cwd, and
    // every method under test is a read, so one handle per side serves the whole fixture.
    let gitRepo = openRepo git.Path
    let jjRepo = openRepo jj.Path

    /// Build both sides by replaying `seed` on each. Skips (or, under `REQUIRE_JJ=1`, fails)
    /// before touching the filesystem when a backend's binary is missing.
    static member Build(tag: string, seed: ScenarioRepo -> unit) =
        requireGit ()
        requireJj ()
        let git = ScenarioRepo.Create(ParityBackend.Git, tag + "g")

        try
            let jj = ScenarioRepo.Create(ParityBackend.Jj, tag + "j")

            try
                seed git
                seed jj
                new ParityPair(git, jj)
            with _ ->
                // Seeding failed after the jj sandbox was created — dispose both temp dirs so a
                // broken fixture doesn't leak them, then surface the original failure.
                (jj :> IDisposable).Dispose()
                reraise ()
        with _ ->
            (git :> IDisposable).Dispose()
            reraise ()

    /// The git sandbox (scenario steps / root path).
    member _.GitSandbox = git

    /// The jj sandbox (scenario steps / root path).
    member _.JjSandbox = jj

    /// The git-backed facade handle, bound to the repository root.
    member _.Git = gitRepo

    /// The jj-backed facade handle, bound to the repository root.
    member _.Jj = jjRepo

    interface IDisposable with
        member _.Dispose() =
            try
                (git :> IDisposable).Dispose()
            finally
                (jj :> IDisposable).Dispose()

/// Hold a `ParityPair` for a fixture's lifetime: built once in `OneTimeSetUp` (every method
/// under test is a read, so one scenario safely serves every test in the fixture) and disposed
/// in `OneTimeTearDown`.
[<Sealed>]
type ScenarioFixture() =
    let mutable pair: ParityPair option = None

    /// Build the pair. Call from `[<OneTimeSetUp>]`.
    member _.Build(tag: string, seed: ScenarioRepo -> unit) =
        pair <- Some(ParityPair.Build(tag, seed))

    /// Dispose the pair, if one was built. Call from `[<OneTimeTearDown>]`.
    member _.Release() =
        match pair with
        | Some p ->
            pair <- None
            (p :> IDisposable).Dispose()
        | None -> ()

    /// The built pair.
    member _.Pair =
        match pair with
        | Some p -> p
        | None -> failwith "the parity scenario was never built — did OneTimeSetUp run?"

// --- Shared scenarios --------------------------------------------------------

/// A repo-relative path with spaces in both the directory and the file name.
[<Literal>]
let SpacedPath = "dir with space/c file.txt"

/// A repo-relative path with non-ASCII characters in both segments. Deliberately built from
/// Cyrillic letters that have no canonical decomposition, so the name survives macOS' NFD
/// filesystem normalisation identically on both backends.
[<Literal>]
let UnicodePath = "каталог/данные.txt"

/// A repo-relative path inside a subdirectory — the anchor for the `Repo.At` cases.
[<Literal>]
let NestedPath = "sub/nested.txt"

/// The standard scenario: two commits over paths with spaces and non-ASCII characters, then one
/// uncommitted edit to a tracked file — the single working-copy change both backends model the
/// same way (git: an unstaged edit to a tracked file; jj: the `@` change). Finishes by naming
/// the working-copy state `main` on both.
let seedStandardScenario (repo: ScenarioRepo) =
    repo.Write("a.txt", "a1\na2\n")
    repo.Write(SpacedPath, "c1\nc2\n")
    repo.Write(UnicodePath, "u1\n")
    repo.Write(NestedPath, "n1\n")
    repo.CommitAll "seed the parity scenario"
    repo.Write(NestedPath, "n1\nn2\n")
    repo.CommitAll "extend the nested file"
    repo.AddRemote("origin", "https://example.invalid/parity.git")
    repo.AddRemote("upstream", "https://example.invalid/parity-upstream.git")
    repo.Write("a.txt", "a1\na2 edited\n")
    repo.NameDefaultBranch()

/// Every awkwardly-named path left modified in the working copy, so any query that *reports* a
/// path has to round-trip the spaces and the non-ASCII characters.
let seedPathShapeScenario (repo: ScenarioRepo) =
    repo.Write("a.txt", "a1\n")
    repo.Write(SpacedPath, "c1\n")
    repo.Write(UnicodePath, "u1\n")
    repo.Write(NestedPath, "n1\n")
    repo.CommitAll "seed the path-shape scenario"
    repo.Write(SpacedPath, "c1\nc2\n")
    repo.Write(UnicodePath, "u1\nu2\n")
    repo.Write(NestedPath, "n1\nn2\n")
    repo.NameDefaultBranch()

/// A commit, then a new file in an *already tracked* directory — the untracked-file case. (git's
/// porcelain status collapses a wholly-untracked directory to the directory itself, so the new
/// file has to sit next to a tracked sibling for both backends to be reporting the same thing.)
let seedUntrackedFileScenario (repo: ScenarioRepo) =
    repo.Write("a.txt", "a1\n")
    repo.Write(NestedPath, "n1\n")
    repo.CommitAll "seed the untracked-file scenario"
    repo.Write("sub/untracked.txt", "u1\n")
    repo.NameDefaultBranch()

/// A commit, then a rename of the committed file (to a name that also carries a space).
let seedRenameScenario (repo: ScenarioRepo) =
    repo.Write("old name.txt", "r1\nr2\n")
    repo.CommitAll "seed the rename scenario"
    repo.Rename("old name.txt", "new name.txt")
    repo.NameDefaultBranch()

/// A commit whose message and content both carry leading and trailing whitespace.
let seedWhitespaceScenario (repo: ScenarioRepo) =
    repo.Write("padded.txt", "  leading and trailing  \n\tindented\n")
    repo.CommitAll "  padded message  "
    repo.NameDefaultBranch()

/// The directory holding the conflicted file — the anchor for the "conflict seen from a
/// subdirectory" case.
[<Literal>]
let ConflictDirectory = "conflict dir"

/// One path the conflict scenario leaves unresolved — inside a subdirectory, and with a space,
/// so the conflicted-path *shape* is exercised too.
[<Literal>]
let ConflictPath = ConflictDirectory + "/f.txt"

/// A second unresolved path outside `ConflictDirectory`, proving a nested-cwd query does not
/// silently omit conflicts elsewhere in the workspace.
[<Literal>]
let OutsideConflictPath = "outside.txt"

/// A working copy holding unresolved conflicts both inside and outside `ConflictDirectory`.
let seedConflictScenario (repo: ScenarioRepo) =
    repo.CreateConflicts
        [ ConflictPath, "base\n", "left\n", "right\n"
          OutsideConflictPath, "outside base\n", "outside left\n", "outside right\n" ]
