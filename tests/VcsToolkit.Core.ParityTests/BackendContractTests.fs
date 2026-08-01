module VcsToolkit.Core.ParityTests.BackendContractTests

open System
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open VcsToolkit.Core
open VcsToolkit.Core.ParityTests.Harness

// The other half of the matrix: the contract each backend has to satisfy *on its own*,
// parameterised by backend so one fixture body runs against git and against jj rather than being
// written twice. A cross-backend comparison (`RepoParityTests`) only proves the two agree — if
// both drifted the same way it would still pass, which is what these per-backend invariants
// (path shape, internal consistency of `Snapshot` against the individual queries, awkward names
// surviving a round trip) are here to catch.

[<TestFixture("git")>]
[<TestFixture("jj")>]
type BackendContractTests(backendName: string) =

    let backend =
        match backendName with
        | "git" -> ParityBackend.Git
        | "jj" -> ParityBackend.Jj
        | other -> failwith $"unknown parity backend '{other}'"

    let mutable sandbox: ScenarioRepo option = None
    let mutable handle: Repo option = None

    let repo () =
        match handle with
        | Some r -> r
        | None -> failwith "the scenario was never built — did OneTimeSetUp run?"

    let sandboxPath () =
        match sandbox with
        | Some s -> s.Path
        | None -> failwith "the scenario was never built — did OneTimeSetUp run?"

    let committedRev () =
        match sandbox with
        | Some s -> s.CommittedRev
        | None -> failwith "the scenario was never built — did OneTimeSetUp run?"

    let historyRev () =
        match sandbox with
        | Some s -> s.HistoryRev
        | None -> failwith "the scenario was never built — did OneTimeSetUp run?"

    [<OneTimeSetUp>]
    member _.BuildScenario() =
        match backend with
        | ParityBackend.Git -> requireGit ()
        | ParityBackend.Jj -> requireJj ()

        let created = ScenarioRepo.Create(backend, "pc" + backendName)

        try
            seedPathShapeScenario created
            sandbox <- Some created
            handle <- Some(openRepo created.Path)
        with _ ->
            // Seeding failed after the sandbox was created — dispose it so a broken fixture
            // doesn't leak a temp dir, then surface the original failure.
            (created :> IDisposable).Dispose()
            reraise ()

    [<OneTimeTearDown>]
    member _.ReleaseScenario() =
        match sandbox with
        | Some s ->
            sandbox <- None
            handle <- None
            (s :> IDisposable).Dispose()
        | None -> ()

    [<Test>]
    member _.OpenDetectsThisBackend() =
        let expected =
            match backend with
            | ParityBackend.Git -> BackendKind.Git
            | ParityBackend.Jj -> BackendKind.Jj

        assertEquals $"{backendName}: Repo.Open detected backend" expected (repo ()).Kind

        assertEquals
            $"{backendName}: Repo.Open root"
            (Path.GetFullPath(sandboxPath ()))
            (Path.GetFullPath (repo ()).Root)

        assertEquals $"{backendName}: Repo.Open cwd" (repo ()).Root (repo ()).Cwd

    [<Test>]
    member _.ReportedPathsAreRepoRelativeAndSlashSeparated() : Task =
        task {
            // The shape the facade documents for every path it reports, on every platform —
            // including Windows, where the underlying tools do not agree on a separator.
            let! changed = (repo ()).ChangedFiles()

            for change in expectOk backendName "ChangedFiles" changed do
                assertRepoRelativePath $"{backendName} ChangedFiles" change.Path

                match change.OldPath with
                | Some old -> assertRepoRelativePath $"{backendName} ChangedFiles.OldPath" old
                | None -> ()

            let! conflicted = (repo ()).ConflictedFiles()

            for path in expectOk backendName "ConflictedFiles" conflicted do
                assertRepoRelativePath $"{backendName} ConflictedFiles" path
        }

    [<Test>]
    member _.SnapshotAgreesWithTheIndividualQueries() : Task =
        task {
            // `Snapshot` is a batched query, not a separate source of truth: every field it
            // carries must answer the same as the dedicated method for that field.
            let! snapshotResult = (repo ()).Snapshot()
            let snapshot = expectOk backendName "Snapshot" snapshotResult

            let! branch = (repo ()).CurrentBranch()

            assertEquals
                $"{backendName}: Snapshot.Branch vs CurrentBranch"
                (expectOk backendName "CurrentBranch" branch)
                snapshot.Branch

            let! dirty = (repo ()).HasUncommittedChanges()

            assertEquals
                $"{backendName}: Snapshot.Dirty vs HasUncommittedChanges"
                (expectOk backendName "HasUncommittedChanges" dirty)
                snapshot.Dirty

            let! changed = (repo ()).ChangedFiles()

            assertEquals
                $"{backendName}: Snapshot.ChangeCount vs ChangedFiles"
                (uint64 (List.length (expectOk backendName "ChangedFiles" changed)))
                snapshot.ChangeCount

            let! conflicted = (repo ()).ConflictedFiles()

            assertEquals
                $"{backendName}: Snapshot.Conflicted vs ConflictedFiles"
                (not (List.isEmpty (expectOk backendName "ConflictedFiles" conflicted)))
                snapshot.Conflicted

            let! state = (repo ()).InProgressState()

            assertEquals
                $"{backendName}: Snapshot.Operation vs InProgressState"
                (expectOk backendName "InProgressState" state)
                snapshot.Operation

            let! worktrees = (repo ()).ListWorktrees()
            let entries = expectOk backendName "ListWorktrees" worktrees
            Assert.That(List.length entries, Is.EqualTo 1, $"{backendName}: the scenario attaches no extra worktree")

            assertEquals $"{backendName}: WorktreeInfo.Commit vs Snapshot.Head" snapshot.Head (List.head entries).Commit

            match snapshot.Head with
            | Some head -> assertFullObjectId $"{backendName} Snapshot.Head" head
            | None -> Assert.Fail $"{backendName}: Snapshot.Head must be set once the repository has a commit"
        }

    [<Test>]
    member _.AwkwardPathsRoundTripThroughEveryQuery() : Task =
        task {
            // A path with spaces / non-ASCII characters must come back byte-identical to the one
            // that was written, and must be accepted as input by the path-taking queries.
            let! changed = (repo ()).ChangedFiles()

            assertListEquals
                $"{backendName}: ChangedFiles reports the seeded paths verbatim"
                [ SpacedPath; NestedPath; UnicodePath ]
                (expectOk backendName "ChangedFiles" changed |> List.map (fun c -> c.Path))

            for path in [ SpacedPath; UnicodePath; NestedPath ] do
                let! shown = (repo ()).ShowFile(committedRev (), path)

                Assert.That(
                    (expectOk backendName "ShowFile" shown).Length,
                    Is.GreaterThan 0,
                    $"{backendName}: ShowFile '{path}' returned nothing"
                )

                let! annotated = (repo ()).Annotate(path, None)

                Assert.That(
                    List.isEmpty (expectOk backendName "Annotate" annotated),
                    Is.False,
                    $"{backendName}: Annotate '{path}' returned no lines"
                )

                let! scoped = (repo ()).LogPaths(historyRev (), 10, [ path ])

                Assert.That(
                    List.length (expectOk backendName "LogPaths" scoped),
                    Is.EqualTo 1,
                    $"{backendName}: LogPaths '{path}' did not find the seeding commit"
                )
        }
