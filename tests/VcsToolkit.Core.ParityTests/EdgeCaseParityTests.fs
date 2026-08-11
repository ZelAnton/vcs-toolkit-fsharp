module VcsToolkit.Core.ParityTests.EdgeCaseParityTests

open System
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open VcsToolkit.Diff
open VcsToolkit.Core
open VcsToolkit.Core.ParityTests.Harness

// The edges of the matrix in `RepoParityTests`: an empty repository, an untracked-only working
// copy, a rename, values carrying edge whitespace, an unresolved conflict, and a handle bound to
// a subdirectory (`Cwd` <> `Root`).
//
// Where the two backends genuinely model something differently, the divergence is asserted here
// rather than smoothed over — an unasserted divergence is one nobody notices changing.

/// Render a `FileChange` for element-wise comparison.
let private describeChange (c: FileChange) =
    $"%A{c.Kind} '{c.Path}' (old=%A{c.OldPath})"

// ---------------------------------------------------------------------------
// An empty repository: git has an unborn HEAD, jj a working-copy change on the
// virtual root commit.
// ---------------------------------------------------------------------------

[<TestFixture>]
type EmptyRepositoryParityTests() =

    let fixture = ScenarioFixture()

    [<OneTimeSetUp>]
    member _.BuildScenario() = fixture.Build("pempty", (fun _ -> ()))

    [<OneTimeTearDown>]
    member _.ReleaseScenario() = fixture.Release()

    [<Test>]
    member _.SharedQueriesAgreeOnAnEmptyRepository() : Task =
        task {
            let pair = fixture.Pair
            let! gitChanged = pair.Git.ChangedFiles()
            let! jjChanged = pair.Jj.ChangedFiles()

            assertSameList
                "ChangedFiles (empty repo)"
                describeChange
                (expectOk "git" "ChangedFiles" gitChanged)
                (expectOk "jj" "ChangedFiles" jjChanged)

            Assert.That(List.isEmpty (expectOk "git" "ChangedFiles" gitChanged), Is.True)

            let! gitConflicts = pair.Git.ConflictedFiles()
            let! jjConflicts = pair.Jj.ConflictedFiles()

            assertSameList
                "ConflictedFiles (empty repo)"
                id
                (expectOk "git" "ConflictedFiles" gitConflicts)
                (expectOk "jj" "ConflictedFiles" jjConflicts)

            let! gitRemotes = pair.Git.Remotes()
            let! jjRemotes = pair.Jj.Remotes()

            assertSameList
                "Remotes (empty repo)"
                (fun (r: Remote) -> $"{r.Name} -> {r.Url}")
                (expectOk "git" "Remotes" gitRemotes)
                (expectOk "jj" "Remotes" jjRemotes)

            let! gitBranches = pair.Git.LocalBranches()
            let! jjBranches = pair.Jj.LocalBranches()

            assertSameList
                "LocalBranches (empty repo)"
                id
                (expectOk "git" "LocalBranches" gitBranches)
                (expectOk "jj" "LocalBranches" jjBranches)

            Assert.That(
                List.isEmpty (expectOk "git" "LocalBranches" gitBranches),
                Is.True,
                "neither backend has a branch/bookmark before the first commit"
            )

            let! gitExists = pair.Git.BranchExists "main"
            let! jjExists = pair.Jj.BranchExists "main"

            assertSame
                "BranchExists 'main' (empty repo)"
                (expectOk "git" "BranchExists" gitExists)
                (expectOk "jj" "BranchExists" jjExists)

            let! gitTrunk = pair.Git.Trunk()
            let! jjTrunk = pair.Jj.Trunk()
            assertSame "Trunk (empty repo)" (expectOk "git" "Trunk" gitTrunk) (expectOk "jj" "Trunk" jjTrunk)
            Assert.That(Option.isNone (expectOk "git" "Trunk" gitTrunk), Is.True, "there is no trunk to resolve yet")

            let! gitDirty = pair.Git.HasUncommittedChanges()
            let! jjDirty = pair.Jj.HasUncommittedChanges()

            assertSame
                "HasUncommittedChanges (empty repo)"
                (expectOk "git" "HasUncommittedChanges" gitDirty)
                (expectOk "jj" "HasUncommittedChanges" jjDirty)

            let! gitTracked = pair.Git.HasTrackedChanges()
            let! jjTracked = pair.Jj.HasTrackedChanges()

            assertSame
                "HasTrackedChanges (empty repo)"
                (expectOk "git" "HasTrackedChanges" gitTracked)
                (expectOk "jj" "HasTrackedChanges" jjTracked)

            let! gitState = pair.Git.InProgressState()
            let! jjState = pair.Jj.InProgressState()

            assertSame
                "InProgressState (empty repo)"
                (expectOk "git" "InProgressState" gitState)
                (expectOk "jj" "InProgressState" jjState)

            let! gitStat = pair.Git.DiffStat()
            let! jjStat = pair.Jj.DiffStat()
            let gitStatValue = expectOk "git" "DiffStat" gitStat
            let jjStatValue = expectOk "jj" "DiffStat" jjStat
            assertSame "DiffStat (empty repo)" gitStatValue jjStatValue

            Assert.That(
                gitStatValue.FilesChanged + gitStatValue.Insertions + gitStatValue.Deletions,
                Is.EqualTo 0UL,
                "an empty repository has nothing to count — on git that means diffing against the empty tree"
            )

            let! gitWorktrees = pair.Git.ListWorktrees()
            let! jjWorktrees = pair.Jj.ListWorktrees()

            assertSame
                "ListWorktrees count (empty repo)"
                (List.length (expectOk "git" "ListWorktrees" gitWorktrees))
                (List.length (expectOk "jj" "ListWorktrees" jjWorktrees))

            Assert.That(List.length (expectOk "git" "ListWorktrees" gitWorktrees), Is.EqualTo 1)
        }

    [<Test>]
    member _.UnbornHeadAndRootChangeDivergeAsDocumented() : Task =
        task {
            // The one structural difference an empty repository exposes: git has no commit at
            // all (an unborn HEAD that still carries the branch name it *would* commit to),
            // while jj always has a working-copy change — on the virtual root commit — and no
            // bookmark until one is created.
            let pair = fixture.Pair
            let! gitBranch = pair.Git.CurrentBranch()
            let! jjBranch = pair.Jj.CurrentBranch()
            assertEquals "git CurrentBranch on an unborn HEAD" (Some "main") (expectOk "git" "CurrentBranch" gitBranch)
            assertEquals "jj CurrentBranch without a bookmark" None (expectOk "jj" "CurrentBranch" jjBranch)

            let! gitSnapshot = pair.Git.Snapshot()
            let! jjSnapshot = pair.Jj.Snapshot()
            let git = expectOk "git" "Snapshot" gitSnapshot
            let jj = expectOk "jj" "Snapshot" jjSnapshot

            assertEquals "git Snapshot.Head on an unborn repo" None git.Head
            Assert.That(Option.isSome jj.Head, Is.True, "jj always has a working-copy commit id")
            assertEquals "git Snapshot.Branch on an unborn repo" (Some "main") git.Branch
            assertEquals "jj Snapshot.Branch without a bookmark" None jj.Branch

            // Everything else about the snapshot still agrees.
            assertSame "Snapshot.Dirty (empty repo)" git.Dirty jj.Dirty
            assertSame "Snapshot.ChangeCount (empty repo)" git.ChangeCount jj.ChangeCount
            assertSame "Snapshot.Conflicted (empty repo)" git.Conflicted jj.Conflicted
            assertSame "Snapshot.Operation (empty repo)" git.Operation jj.Operation
            assertSame "Snapshot.Tracking (empty repo)" git.Tracking jj.Tracking
            Assert.That(git.Dirty, Is.False)
            Assert.That(git.ChangeCount, Is.EqualTo 0UL)
        }

    [<Test>]
    member _.LogDivergesOnAnEmptyRepository() : Task =
        task {
            // git refuses to log an unborn HEAD at all; jj has no unborn state, so the same
            // query is simply an empty history. A consumer that logs right after `init` has to
            // handle both — hence pinning it here rather than leaving it to be discovered.
            let pair = fixture.Pair
            let! gitLog = pair.Git.Log(pair.GitSandbox.HistoryRev, 10)
            let! jjLog = pair.Jj.Log(pair.JjSandbox.HistoryRev, 10)

            Assert.That(Result.isError gitLog, Is.True, "git cannot resolve HEAD before the first commit")
            assertListEquals "jj Log on an empty repository" [] (expectOk "jj" "Log" jjLog)
        }

// ---------------------------------------------------------------------------
// Awkward path shapes: spaces and non-ASCII characters, reported by every
// path-carrying query.
// ---------------------------------------------------------------------------

[<TestFixture>]
type PathShapeParityTests() =

    let fixture = ScenarioFixture()

    [<OneTimeSetUp>]
    member _.BuildScenario() =
        fixture.Build("ppath", seedPathShapeScenario)

    [<OneTimeTearDown>]
    member _.ReleaseScenario() = fixture.Release()

    [<Test>]
    member _.ChangedFilesReportTheSameAwkwardPaths() : Task =
        task {
            let pair = fixture.Pair
            let! gitResult = pair.Git.ChangedFiles()
            let! jjResult = pair.Jj.ChangedFiles()
            let git = expectOk "git" "ChangedFiles" gitResult
            let jj = expectOk "jj" "ChangedFiles" jjResult

            // Element-wise *and* in the same order: both backends list working-copy changes in
            // path order.
            assertSameList "ChangedFiles (awkward paths)" describeChange git jj

            assertListEquals
                "ChangedFiles paths"
                [ SpacedPath; NestedPath; UnicodePath ]
                (git |> List.map (fun c -> c.Path))

            git |> List.iter (fun c -> assertRepoRelativePath "git ChangedFiles" c.Path)
            jj |> List.iter (fun c -> assertRepoRelativePath "jj ChangedFiles" c.Path)
        }

    [<Test>]
    member _.ContentQueriesAgreeOnAwkwardPaths() : Task =
        task {
            let pair = fixture.Pair

            for path in [ SpacedPath; UnicodePath ] do
                let! gitShown = pair.Git.ShowFile(pair.GitSandbox.CommittedRev, path)
                let! jjShown = pair.Jj.ShowFile(pair.JjSandbox.CommittedRev, path)
                assertSame $"ShowFile '{path}'" (expectOk "git" "ShowFile" gitShown) (expectOk "jj" "ShowFile" jjShown)

                let! gitAnnotated = pair.Git.Annotate(path, None)
                let! jjAnnotated = pair.Jj.Annotate(path, None)

                assertSameList
                    $"Annotate '{path}'"
                    (fun (l: AnnotateLine) -> $"{l.Line}:{l.Content}")
                    (expectOk "git" "Annotate" gitAnnotated)
                    (expectOk "jj" "Annotate" jjAnnotated)

                let! gitScoped = pair.Git.LogPaths(pair.GitSandbox.HistoryRev, 10, [ path ])
                let! jjScoped = pair.Jj.LogPaths(pair.JjSandbox.HistoryRev, 10, [ path ])

                assertSameList
                    $"LogPaths '{path}'"
                    (fun (c: Commit) -> c.Description)
                    (expectOk "git" "LogPaths" gitScoped)
                    (expectOk "jj" "LogPaths" jjScoped)

                Assert.That(
                    List.length (expectOk "git" "LogPaths" gitScoped),
                    Is.EqualTo 1,
                    $"the seeding commit touched '{path}'"
                )
        }

// ---------------------------------------------------------------------------
// An untracked-only working copy — where the tracked-changes query is
// documented to diverge.
// ---------------------------------------------------------------------------

[<TestFixture>]
type UntrackedFileParityTests() =

    let fixture = ScenarioFixture()

    [<OneTimeSetUp>]
    member _.BuildScenario() =
        fixture.Build("puntr", seedUntrackedFileScenario)

    [<OneTimeTearDown>]
    member _.ReleaseScenario() = fixture.Release()

    [<Test>]
    member _.AnUntrackedFileIsReportedIdentically() : Task =
        task {
            let pair = fixture.Pair
            let! gitResult = pair.Git.ChangedFiles()
            let! jjResult = pair.Jj.ChangedFiles()
            let git = expectOk "git" "ChangedFiles" gitResult
            let jj = expectOk "jj" "ChangedFiles" jjResult

            assertSameList "ChangedFiles (untracked file)" describeChange git jj
            assertListEquals "ChangedFiles paths" [ "sub/untracked.txt" ] (git |> List.map (fun c -> c.Path))
            Assert.That((List.head git).Kind, Is.EqualTo ChangeKind.Added)

            let! gitDirty = pair.Git.HasUncommittedChanges()
            let! jjDirty = pair.Jj.HasUncommittedChanges()

            assertSame
                "HasUncommittedChanges (untracked file)"
                (expectOk "git" "HasUncommittedChanges" gitDirty)
                (expectOk "jj" "HasUncommittedChanges" jjDirty)

            Assert.That(expectOk "git" "HasUncommittedChanges" gitDirty, Is.True)

            let! gitSnapshot = pair.Git.Snapshot()
            let! jjSnapshot = pair.Jj.Snapshot()

            assertSame
                "Snapshot.ChangeCount (untracked file)"
                (expectOk "git" "Snapshot" gitSnapshot).ChangeCount
                (expectOk "jj" "Snapshot" jjSnapshot).ChangeCount
        }

    [<Test>]
    member _.TrackedOnlyQueriesDivergeAsDocumented() : Task =
        task {
            // The documented nuance: git's tracked-only query ignores untracked files, while jj
            // auto-tracks new files, so there `HasTrackedChanges` *is* `HasUncommittedChanges`.
            let pair = fixture.Pair
            let! gitTracked = pair.Git.HasTrackedChanges()
            let! jjTracked = pair.Jj.HasTrackedChanges()

            assertEquals
                "git HasTrackedChanges with only an untracked file"
                false
                (expectOk "git" "HasTrackedChanges" gitTracked)

            assertEquals "jj HasTrackedChanges with only a new file" true (expectOk "jj" "HasTrackedChanges" jjTracked)

            // Same root cause on the stat: git counts the working tree against `HEAD` (untracked
            // files excluded), jj counts the `@` change (which includes them).
            let! gitStat = pair.Git.DiffStat()
            let! jjStat = pair.Jj.DiffStat()

            assertEquals
                "git DiffStat.FilesChanged with only an untracked file"
                0UL
                (expectOk "git" "DiffStat" gitStat).FilesChanged

            assertEquals
                "jj DiffStat.FilesChanged with only a new file"
                1UL
                (expectOk "jj" "DiffStat" jjStat).FilesChanged
        }

// ---------------------------------------------------------------------------
// A rename — the one `FileChange` shape that carries an old path.
// ---------------------------------------------------------------------------

[<TestFixture>]
type RenameParityTests() =

    let fixture = ScenarioFixture()

    [<OneTimeSetUp>]
    member _.BuildScenario() =
        fixture.Build("pren", seedRenameScenario)

    [<OneTimeTearDown>]
    member _.ReleaseScenario() = fixture.Release()

    [<Test>]
    member _.RenamesCarryTheOldPathOnBothBackends() : Task =
        task {
            let pair = fixture.Pair
            let! gitResult = pair.Git.ChangedFiles()
            let! jjResult = pair.Jj.ChangedFiles()
            let git = expectOk "git" "ChangedFiles" gitResult
            let jj = expectOk "jj" "ChangedFiles" jjResult

            assertSameList "ChangedFiles (rename)" describeChange git jj

            Assert.That(List.length git, Is.EqualTo 1, "a rename is one entry, not a delete plus an add")
            let change = List.head git
            Assert.That(change.Path, Is.EqualTo "new name.txt")
            assertEquals "FileChange.OldPath" (Some "old name.txt") change.OldPath
            Assert.That(change.Kind, Is.EqualTo ChangeKind.Renamed)

            let! gitStat = pair.Git.DiffStat()
            let! jjStat = pair.Jj.DiffStat()
            assertSame "DiffStat (rename)" (expectOk "git" "DiffStat" gitStat) (expectOk "jj" "DiffStat" jjStat)
        }

// ---------------------------------------------------------------------------
// Values carrying leading/trailing whitespace: file content and a commit
// message.
// ---------------------------------------------------------------------------

[<TestFixture>]
type EdgeWhitespaceParityTests() =

    let fixture = ScenarioFixture()

    [<OneTimeSetUp>]
    member _.BuildScenario() =
        fixture.Build("pwsp", seedWhitespaceScenario)

    [<OneTimeTearDown>]
    member _.ReleaseScenario() = fixture.Release()

    [<Test>]
    member _.ContentWhitespaceSurvivesIdenticallyOnBothBackends() : Task =
        task {
            let pair = fixture.Pair
            let! gitShown = pair.Git.ShowFile(pair.GitSandbox.CommittedRev, "padded.txt")
            let! jjShown = pair.Jj.ShowFile(pair.JjSandbox.CommittedRev, "padded.txt")
            let git = expectOk "git" "ShowFile" gitShown
            assertSame "ShowFile (padded content)" git (expectOk "jj" "ShowFile" jjShown)

            assertEquals
                "ShowFile is byte-faithful, including edge whitespace"
                "  leading and trailing  \n\tindented\n"
                git

            // The same content seen line by line: annotate must not trim either edge.
            let! gitAnnotated = pair.Git.Annotate("padded.txt", None)
            let! jjAnnotated = pair.Jj.Annotate("padded.txt", None)

            assertSameList
                "Annotate (padded content)"
                (fun (l: AnnotateLine) -> $"{l.Line}:[{l.Content}]")
                (expectOk "git" "Annotate" gitAnnotated)
                (expectOk "jj" "Annotate" jjAnnotated)

            assertListEquals
                "Annotate content keeps leading and trailing whitespace"
                [ "  leading and trailing  "; "\tindented" ]
                (expectOk "git" "Annotate" gitAnnotated |> List.map (fun l -> l.Content))
        }

    [<Test>]
    member _.CommitMessageWhitespaceMatchesExceptGitsOwnCleanup() : Task =
        task {
            let pair = fixture.Pair
            let! gitLog = pair.Git.Log(pair.GitSandbox.HistoryRev, 10)
            let! jjLog = pair.Jj.Log(pair.JjSandbox.HistoryRev, 10)
            let gitDescription = (expectOk "git" "Log" gitLog |> List.head).Description
            let jjDescription = (expectOk "jj" "Log" jjLog |> List.head).Description

            // Leading whitespace is preserved verbatim by both.
            Assert.That(
                gitDescription.StartsWith("  padded", StringComparison.Ordinal),
                Is.True,
                $"git: '{gitDescription}'"
            )

            Assert.That(
                jjDescription.StartsWith("  padded", StringComparison.Ordinal),
                Is.True,
                $"jj: '{jjDescription}'"
            )

            // Trailing whitespace is the one part that cannot be promised byte-for-byte: `git
            // commit -m` runs git's own message cleanup and strips it before the message is ever
            // stored, while jj records the description as given. What can be asserted across the
            // two is therefore the message modulo that cleanup — plus git's stripping, pinned
            // explicitly below so the cleanup itself stays visible.
            assertSame
                "Log description (modulo git's message cleanup)"
                (gitDescription.TrimEnd())
                (jjDescription.TrimEnd())

            assertEquals "git strips trailing whitespace from a commit message" "  padded message" gitDescription
        }

// ---------------------------------------------------------------------------
// An unresolved conflict: git pauses a merge, jj records the conflict on a
// merge change.
// ---------------------------------------------------------------------------

[<TestFixture>]
type ConflictParityTests() =

    let fixture = ScenarioFixture()

    [<OneTimeSetUp>]
    member _.BuildScenario() =
        fixture.Build("pconf", seedConflictScenario)

    [<OneTimeTearDown>]
    member _.ReleaseScenario() = fixture.Release()

    [<Test>]
    member _.ConflictedFilesMatchAcrossBackends() : Task =
        task {
            let pair = fixture.Pair
            let! gitResult = pair.Git.ConflictedFiles()
            let! jjResult = pair.Jj.ConflictedFiles()
            let git = expectOk "git" "ConflictedFiles" gitResult
            let jj = expectOk "jj" "ConflictedFiles" jjResult

            let expected = [ ConflictPath; OutsideConflictPath ] |> List.sort
            let git = List.sort git
            let jj = List.sort jj

            assertSameList "ConflictedFiles" id git jj
            assertListEquals "ConflictedFiles paths" expected git
            git |> List.iter (assertRepoRelativePath "git ConflictedFiles")
            jj |> List.iter (assertRepoRelativePath "jj ConflictedFiles")

            let! gitDirty = pair.Git.HasUncommittedChanges()
            let! jjDirty = pair.Jj.HasUncommittedChanges()

            assertSame
                "HasUncommittedChanges (conflicted)"
                (expectOk "git" "HasUncommittedChanges" gitDirty)
                (expectOk "jj" "HasUncommittedChanges" jjDirty)

            Assert.That(
                expectOk "jj" "HasUncommittedChanges" jjDirty,
                Is.True,
                "a conflicted change counts as uncommitted state on jj even though it is empty against its parents"
            )

            let! gitSnapshot = pair.Git.Snapshot()
            let! jjSnapshot = pair.Jj.Snapshot()

            assertSame
                "Snapshot.Conflicted"
                (expectOk "git" "Snapshot" gitSnapshot).Conflicted
                (expectOk "jj" "Snapshot" jjSnapshot).Conflicted

            Assert.That((expectOk "git" "Snapshot" gitSnapshot).Conflicted, Is.True)
        }

    [<Test>]
    member _.OperationStateDivergesAsDocumented() : Task =
        task {
            // The asymmetry the facade documents: on git a conflict *is* the paused merge, so
            // the operation reads `Merge`; jj has no paused operation and records the conflict
            // on the working-copy change, so it reads `Conflict`.
            let pair = fixture.Pair
            let! gitState = pair.Git.InProgressState()
            let! jjState = pair.Jj.InProgressState()

            assertEquals
                "git InProgressState during a conflicted merge"
                OperationState.Merge
                (expectOk "git" "InProgressState" gitState)

            assertEquals
                "jj InProgressState with a conflicted change"
                OperationState.Conflict
                (expectOk "jj" "InProgressState" jjState)

            let! gitSnapshot = pair.Git.Snapshot()
            let! jjSnapshot = pair.Jj.Snapshot()
            assertEquals "git Snapshot.Operation" OperationState.Merge (expectOk "git" "Snapshot" gitSnapshot).Operation
            assertEquals "jj Snapshot.Operation" OperationState.Conflict (expectOk "jj" "Snapshot" jjSnapshot).Operation
        }

    [<Test>]
    member _.ConflictedFilesFromASubdirectoryRemainRepoRelative() : Task =
        task {
            // `Repo.ConflictedFiles` promises workspace-root-relative paths even when the
            // handle is bound to a subdirectory. The scenario includes one conflict outside
            // that directory to prove the query is not narrowed to the caller's cwd.
            let pair = fixture.Pair
            let! gitResult = pair.Git.At(Path.Combine(pair.GitSandbox.Path, ConflictDirectory)).ConflictedFiles()
            let! jjResult = pair.Jj.At(Path.Combine(pair.JjSandbox.Path, ConflictDirectory)).ConflictedFiles()

            let expected = [ ConflictPath; OutsideConflictPath ] |> List.sort

            assertListEquals
                "git ConflictedFiles from a subdirectory handle stays repo-relative"
                expected
                (expectOk "git" "ConflictedFiles" gitResult |> List.sort)

            assertListEquals
                "jj ConflictedFiles from a subdirectory handle stays repo-relative and complete"
                expected
                (expectOk "jj" "ConflictedFiles" jjResult |> List.sort)

            expectOk "git" "ConflictedFiles" gitResult
            |> List.iter (assertRepoRelativePath "git ConflictedFiles from subdirectory")

            expectOk "jj" "ConflictedFiles" jjResult
            |> List.iter (assertRepoRelativePath "jj ConflictedFiles from subdirectory")
        }

// ---------------------------------------------------------------------------
// A handle bound to a subdirectory (`Cwd` <> `Root`).
// ---------------------------------------------------------------------------

[<TestFixture>]
type SubdirectoryParityTests() =

    let fixture = ScenarioFixture()

    [<OneTimeSetUp>]
    member _.BuildScenario() =
        fixture.Build("psub", seedStandardScenario)

    [<OneTimeTearDown>]
    member _.ReleaseScenario() = fixture.Release()

    [<Test>]
    member _.OpeningASubdirectoryFindsTheSameRootOnBothBackends() =
        let pair = fixture.Pair

        for repo, sandbox in [ pair.Git, pair.GitSandbox; pair.Jj, pair.JjSandbox ] do
            let subdirectory = Path.Combine(sandbox.Path, "sub")
            let opened = openRepo subdirectory
            let backend = sandbox.Backend.Name

            assertEquals $"{backend}: Repo.Open(subdirectory).Root" repo.Root opened.Root
            assertEquals $"{backend}: Repo.Open(subdirectory).Kind" repo.Kind opened.Kind

            assertEquals
                $"{backend}: Repo.Open(subdirectory).Cwd"
                (Path.GetFullPath subdirectory)
                (Path.GetFullPath opened.Cwd)

            let reanchored = repo.At subdirectory
            assertEquals $"{backend}: Repo.At(subdirectory).Root" repo.Root reanchored.Root

            assertEquals
                $"{backend}: Repo.At(subdirectory).Cwd"
                (Path.GetFullPath subdirectory)
                (Path.GetFullPath reanchored.Cwd)

    [<Test>]
    member _.PathQueriesStayRepoRelativeFromASubdirectory() : Task =
        task {
            // The repo-relative contract is what makes a path from one query usable as input to
            // another, whichever directory the handle happens to be bound to.
            let pair = fixture.Pair
            let gitSub = pair.Git.At(Path.Combine(pair.GitSandbox.Path, "sub"))
            let jjSub = pair.Jj.At(Path.Combine(pair.JjSandbox.Path, "sub"))

            let! gitChanged = gitSub.ChangedFiles()
            let! jjChanged = jjSub.ChangedFiles()

            assertSameList
                "ChangedFiles (from a subdirectory)"
                describeChange
                (expectOk "git" "ChangedFiles" gitChanged)
                (expectOk "jj" "ChangedFiles" jjChanged)

            assertListEquals
                "ChangedFiles paths stay anchored at the repository root"
                [ "a.txt" ]
                (expectOk "git" "ChangedFiles" gitChanged |> List.map (fun c -> c.Path))

            // A root-relative path argument resolves against `Root`, not `Cwd`, on both.
            let! gitScoped = gitSub.LogPaths(pair.GitSandbox.HistoryRev, 10, [ NestedPath ])
            let! jjScoped = jjSub.LogPaths(pair.JjSandbox.HistoryRev, 10, [ NestedPath ])

            assertSameList
                "LogPaths (from a subdirectory)"
                (fun (c: Commit) -> c.Description)
                (expectOk "git" "LogPaths" gitScoped)
                (expectOk "jj" "LogPaths" jjScoped)

            Assert.That(List.length (expectOk "git" "LogPaths" gitScoped), Is.EqualTo 2)

            let! gitAnnotated = gitSub.Annotate(NestedPath, None)
            let! jjAnnotated = jjSub.Annotate(NestedPath, None)

            assertSameList
                "Annotate (from a subdirectory)"
                (fun (l: AnnotateLine) -> $"{l.Line}:{l.Content}")
                (expectOk "git" "Annotate" gitAnnotated)
                (expectOk "jj" "Annotate" jjAnnotated)

            let! gitShown = gitSub.ShowFile(pair.GitSandbox.CommittedRev, NestedPath)
            let! jjShown = jjSub.ShowFile(pair.JjSandbox.CommittedRev, NestedPath)

            assertSame
                "ShowFile (from a subdirectory)"
                (expectOk "git" "ShowFile" gitShown)
                (expectOk "jj" "ShowFile" jjShown)
        }

    [<Test>]
    member _.SnapshotIsUnaffectedByTheBoundDirectory() : Task =
        task {
            let pair = fixture.Pair

            let! gitRoot = pair.Git.Snapshot()
            let! gitSub = pair.Git.At(Path.Combine(pair.GitSandbox.Path, "sub")).Snapshot()
            let! jjRoot = pair.Jj.Snapshot()
            let! jjSub = pair.Jj.At(Path.Combine(pair.JjSandbox.Path, "sub")).Snapshot()

            assertEquals
                "git Snapshot from a subdirectory"
                (expectOk "git" "Snapshot" gitRoot)
                (expectOk "git" "Snapshot" gitSub)

            assertEquals
                "jj Snapshot from a subdirectory"
                (expectOk "jj" "Snapshot" jjRoot)
                (expectOk "jj" "Snapshot" jjSub)

            assertSame
                "Snapshot.ChangeCount (from a subdirectory)"
                (expectOk "git" "Snapshot" gitSub).ChangeCount
                (expectOk "jj" "Snapshot" jjSub).ChangeCount
        }
