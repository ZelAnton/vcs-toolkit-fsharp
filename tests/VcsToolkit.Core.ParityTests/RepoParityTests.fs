module VcsToolkit.Core.ParityTests.RepoParityTests

open System
open System.IO
open System.Reflection
open System.Threading.Tasks
open NUnit.Framework
open VcsToolkit.Diff
open VcsToolkit.Core
open VcsToolkit.Core.ParityTests.Harness

// The parity matrix proper: one scenario, seeded identically on a real git repository and a real
// jj repository, with every read method of the `Repo` facade queried through `Repo.Open` on both
// sides and compared element by element. `EveryFacadeReadMethodHasAParityScenario` (bottom of
// this file) is what keeps the matrix complete: adding a member to `Repo` fails that test until
// it is either covered here or explicitly classified as a non-read operation.

/// Read methods of `Repo` — those with an implementation on *both* backends — that this matrix
/// covers. Keep in sync with the `[<Test>]` members below; the reflection guard at the bottom of
/// the file rejects a `Repo` member that appears in neither this set nor `nonReadMembers`.
let private coveredReadMethods =
    set
        [ "Annotate"
          "BranchExists"
          "ChangedFiles"
          "ConflictedFiles"
          "CurrentBranch"
          "Diff"
          "DiffStat"
          "DiffText"
          "HasTrackedChanges"
          "HasUncommittedChanges"
          "InProgressState"
          "ListWorktrees"
          "LocalBranches"
          "Log"
          "LogPaths"
          "Remotes"
          "ShowFile"
          "ShowFileBytes"
          "Snapshot"
          "Trunk" ]

/// `Repo` members that are not read queries, so they are deliberately out of this matrix's
/// scope: mutations, the merge probe (it mutates and rolls back), and the `At` re-anchoring
/// helper. Their behaviour is covered by `tests/VcsToolkit.Core.Tests`.
let private nonReadMembers =
    set
        [ "AbortInProgress"
          "At"
          "Checkout"
          "CommitPaths"
          "ContinueInProgress"
          "CreateWorktree"
          "DeleteBranch"
          "Fetch"
          "FetchBranch"
          "FetchFrom"
          "NewChild"
          "Push"
          "Rebase"
          "RemoveWorktree"
          "RenameBranch"
          "TryMerge" ]

/// Render a `FileChange` for element-wise comparison.
let private describeChange (c: FileChange) =
    $"%A{c.Kind} '{c.Path}' (old=%A{c.OldPath})"

/// Render a `Remote` for element-wise comparison.
let private describeRemote (r: Remote) = $"{r.Name} -> {r.Url}"

[<TestFixture>]
type RepoFacadeParityTests() =

    let fixture = ScenarioFixture()

    [<OneTimeSetUp>]
    member _.BuildScenario() =
        fixture.Build("pmx", seedStandardScenario)

    [<OneTimeTearDown>]
    member _.ReleaseScenario() = fixture.Release()

    // --- Status ---------------------------------------------------------------

    [<Test>]
    member _.SnapshotMatchesAcrossBackends() : Task =
        task {
            let pair = fixture.Pair
            let! gitResult = pair.Git.Snapshot()
            let! jjResult = pair.Jj.Snapshot()
            let git = expectOk "git" "Snapshot" gitResult
            let jj = expectOk "jj" "Snapshot" jjResult

            assertSame "Snapshot.Branch" git.Branch jj.Branch
            assertSame "Snapshot.Dirty" git.Dirty jj.Dirty
            assertSame "Snapshot.ChangeCount" git.ChangeCount jj.ChangeCount
            assertSame "Snapshot.Conflicted" git.Conflicted jj.Conflicted
            assertSame "Snapshot.Operation" git.Operation jj.Operation
            // `Tracking` is `None` on both here: no upstream is configured on the git side, and
            // jj has no git-style upstream tracking at all (documented on the DTO).
            assertSame "Snapshot.Tracking" git.Tracking jj.Tracking

            // `Head` identifies a commit in its own repository, so the two values cannot be
            // equal — the parity is in the shape both backends promise: a full object id.
            match git.Head, jj.Head with
            | Some gitHead, Some jjHead ->
                assertFullObjectId "git Snapshot.Head" gitHead
                assertFullObjectId "jj Snapshot.Head" jjHead
            | _ -> Assert.Fail $"Snapshot.Head must be present on both backends: git=%A{git.Head}, jj=%A{jj.Head}"

            // Anchor the shared values, so "both backends are wrong the same way" still fails.
            Assert.That(git.Branch, Is.EqualTo(Some "main"))
            Assert.That(git.Dirty, Is.True, "the scenario leaves one uncommitted edit")
            Assert.That(git.ChangeCount, Is.EqualTo 1UL)
            Assert.That(git.Operation, Is.EqualTo OperationState.Clear)
        }

    [<Test>]
    member _.ChangedFilesMatchAcrossBackends() : Task =
        task {
            let pair = fixture.Pair
            let! gitResult = pair.Git.ChangedFiles()
            let! jjResult = pair.Jj.ChangedFiles()
            let git = expectOk "git" "ChangedFiles" gitResult
            let jj = expectOk "jj" "ChangedFiles" jjResult

            assertSameList "ChangedFiles" describeChange git jj

            Assert.That(List.length git, Is.EqualTo 1)
            let change = List.head git
            Assert.That(change.Path, Is.EqualTo "a.txt")
            Assert.That(change.Kind, Is.EqualTo ChangeKind.Modified)
            Assert.That(Option.isNone change.OldPath, Is.True, "an edit is not a rename")
        }

    [<Test>]
    member _.DiffStatMatchesAcrossBackends() : Task =
        task {
            // Comparable by construction: the only working-copy change is an edit to a *tracked*
            // file. (The backends genuinely differ on untracked files — git counts the working
            // tree against `HEAD`, jj counts the `@` change, which auto-tracks new files — so an
            // untracked file would compare two different questions.)
            let pair = fixture.Pair
            let! gitResult = pair.Git.DiffStat()
            let! jjResult = pair.Jj.DiffStat()
            let git = expectOk "git" "DiffStat" gitResult
            let jj = expectOk "jj" "DiffStat" jjResult

            assertSame "DiffStat.FilesChanged" git.FilesChanged jj.FilesChanged
            assertSame "DiffStat.Insertions" git.Insertions jj.Insertions
            assertSame "DiffStat.Deletions" git.Deletions jj.Deletions

            Assert.That(git.FilesChanged, Is.EqualTo 1UL)
            Assert.That(git.Insertions, Is.EqualTo 1UL)
            Assert.That(git.Deletions, Is.EqualTo 1UL)
        }

    [<Test>]
    member _.DiffTextMatchesAcrossBackends() : Task =
        task {
            let pair = fixture.Pair
            let! gitResult = pair.Git.DiffText()
            let! jjResult = pair.Jj.DiffText()
            let git = expectOk "git" "DiffText" gitResult
            let jj = expectOk "jj" "DiffText" jjResult

            // Both backends should produce non-empty diff text for the modified file.
            // Exact formatting may differ (git vs jj hunk headers, context lines, etc.),
            // so we verify presence of content rather than exact equality.
            Assert.That(git.Length, Is.GreaterThan 0, "git DiffText should have content")
            Assert.That(jj.Length, Is.GreaterThan 0, "jj DiffText should have content")
            // Both should contain diff markers indicating changes.
            Assert.That(git, Does.Contain "@@", "git diff should contain hunk headers")
            Assert.That(jj, Does.Contain "@@", "jj diff should contain hunk headers")
        }

    [<Test>]
    member _.DiffMatchesAcrossBackends() : Task =
        task {
            let pair = fixture.Pair
            let! gitResult = pair.Git.Diff()
            let! jjResult = pair.Jj.Diff()
            let git = expectOk "git" "Diff" gitResult
            let jj = expectOk "jj" "Diff" jjResult

            // Parsed diff should have matching file changes.
            Assert.That(git.Length, Is.EqualTo jj.Length, "Diff result list lengths should match")

            // Both should report at least one changed file.
            Assert.That(git.Length, Is.GreaterThan 0, "Should have at least one file in the diff")

            // Check that the files and change kinds match across backends.
            for i in 0 .. (git.Length - 1) do
                let gitFile = git[i]
                let jjFile = jj[i]
                assertSame $"Diff[{i}].Path" gitFile.Path jjFile.Path
                assertSame $"Diff[{i}].Change" gitFile.Change jjFile.Change
        }

    [<Test>]
    member _.ConflictedFilesMatchAcrossBackends() : Task =
        task {
            // The conflicted case lives in `ConflictParityTests`; here both backends must agree
            // that a healthy working copy has nothing unresolved.
            let pair = fixture.Pair
            let! gitResult = pair.Git.ConflictedFiles()
            let! jjResult = pair.Jj.ConflictedFiles()
            let git = expectOk "git" "ConflictedFiles" gitResult
            let jj = expectOk "jj" "ConflictedFiles" jjResult

            assertSameList "ConflictedFiles" id git jj
            Assert.That(List.isEmpty git, Is.True, "a clean working copy has no conflicted paths")
        }

    [<Test>]
    member _.DirtinessQueriesMatchAcrossBackends() : Task =
        task {
            let pair = fixture.Pair
            let! gitUncommitted = pair.Git.HasUncommittedChanges()
            let! jjUncommitted = pair.Jj.HasUncommittedChanges()
            let! gitTracked = pair.Git.HasTrackedChanges()
            let! jjTracked = pair.Jj.HasTrackedChanges()

            let git = expectOk "git" "HasUncommittedChanges" gitUncommitted
            let jj = expectOk "jj" "HasUncommittedChanges" jjUncommitted
            assertSame "HasUncommittedChanges" git jj
            Assert.That(git, Is.True, "the scenario leaves one uncommitted edit")

            // The edit is to a *tracked* file, so the tracked-only query agrees too. (Their
            // documented divergence — git ignores untracked files where jj auto-tracks them —
            // is asserted by `UntrackedFileParityTests`.)
            let gitOnlyTracked = expectOk "git" "HasTrackedChanges" gitTracked
            let jjOnlyTracked = expectOk "jj" "HasTrackedChanges" jjTracked
            assertSame "HasTrackedChanges" gitOnlyTracked jjOnlyTracked
            Assert.That(gitOnlyTracked, Is.True)
        }

    [<Test>]
    member _.InProgressStateMatchesAcrossBackends() : Task =
        task {
            let pair = fixture.Pair
            let! gitResult = pair.Git.InProgressState()
            let! jjResult = pair.Jj.InProgressState()
            let git = expectOk "git" "InProgressState" gitResult
            let jj = expectOk "jj" "InProgressState" jjResult

            assertSame "InProgressState" git jj
            Assert.That(git, Is.EqualTo OperationState.Clear)
        }

    // --- Refs -----------------------------------------------------------------

    [<Test>]
    member _.BranchQueriesMatchAcrossBackends() : Task =
        task {
            let pair = fixture.Pair
            let! gitCurrent = pair.Git.CurrentBranch()
            let! jjCurrent = pair.Jj.CurrentBranch()
            let! gitLocal = pair.Git.LocalBranches()
            let! jjLocal = pair.Jj.LocalBranches()
            let! gitExists = pair.Git.BranchExists "main"
            let! jjExists = pair.Jj.BranchExists "main"
            let! gitMissing = pair.Git.BranchExists "no-such-branch"
            let! jjMissing = pair.Jj.BranchExists "no-such-branch"

            assertSame
                "CurrentBranch"
                (expectOk "git" "CurrentBranch" gitCurrent)
                (expectOk "jj" "CurrentBranch" jjCurrent)

            assertSameList
                "LocalBranches"
                id
                (expectOk "git" "LocalBranches" gitLocal)
                (expectOk "jj" "LocalBranches" jjLocal)

            assertSame
                "BranchExists 'main'"
                (expectOk "git" "BranchExists" gitExists)
                (expectOk "jj" "BranchExists" jjExists)

            assertSame
                "BranchExists 'no-such-branch'"
                (expectOk "git" "BranchExists" gitMissing)
                (expectOk "jj" "BranchExists" jjMissing)

            Assert.That(expectOk "git" "CurrentBranch" gitCurrent, Is.EqualTo(Some "main"))
            Assert.That(expectOk "git" "BranchExists" gitExists, Is.True)
            Assert.That(expectOk "git" "BranchExists" gitMissing, Is.False)
        }

    [<Test>]
    member _.TrunkMatchesAcrossBackends() : Task =
        task {
            // Neither sandbox has a fetched `origin/HEAD` (git) or remote bookmark (jj), so both
            // backends fall through their native notion of a trunk to the local `main`.
            let pair = fixture.Pair
            let! gitResult = pair.Git.Trunk()
            let! jjResult = pair.Jj.Trunk()
            let git = expectOk "git" "Trunk" gitResult
            let jj = expectOk "jj" "Trunk" jjResult

            assertSame "Trunk" git jj
            Assert.That(git, Is.EqualTo(Some "main"))
        }

    [<Test>]
    member _.RemotesMatchAcrossBackends() : Task =
        task {
            let pair = fixture.Pair
            let! gitResult = pair.Git.Remotes()
            let! jjResult = pair.Jj.Remotes()
            let git = expectOk "git" "Remotes" gitResult
            let jj = expectOk "jj" "Remotes" jjResult

            assertSameList "Remotes" describeRemote git jj

            Assert.That(List.length git, Is.EqualTo 2)
            Assert.That((List.head git).Name, Is.EqualTo "origin")
            Assert.That((List.head git).Url, Is.EqualTo "https://example.invalid/parity.git")
        }

    // --- History --------------------------------------------------------------

    [<Test>]
    member _.LogMatchesAcrossBackends() : Task =
        task {
            let pair = fixture.Pair
            let! gitResult = pair.Git.Log(pair.GitSandbox.HistoryRev, 10)
            let! jjResult = pair.Jj.Log(pair.JjSandbox.HistoryRev, 10)
            let git = expectOk "git" "Log" gitResult
            let jj = expectOk "jj" "Log" jjResult

            assertSameList "Log descriptions" (fun (c: Commit) -> c.Description) git jj

            assertListEquals
                "Log descriptions (most-recent-first on both backends)"
                [ "extend the nested file"; "seed the parity scenario" ]
                (git |> List.map (fun c -> c.Description))

            // Documented divergence: jj's typed log surfaces no authorship or timestamp, so the
            // facade leaves both `None` there rather than fabricating a value.
            Assert.That(
                git |> List.forall (fun c -> Option.isSome c.Author && Option.isSome c.Date),
                Is.True,
                "git populates Commit.Author/Date"
            )

            Assert.That(
                jj |> List.forall (fun c -> Option.isNone c.Author && Option.isNone c.Date),
                Is.True,
                "jj leaves Commit.Author/Date unset — the documented backend nuance"
            )

            // Ids identify a commit in their own repository, so only their shape is comparable:
            // git's full object id, jj's already-short commit id (documented on the DTO).
            git |> List.iter (fun c -> assertFullObjectId "git Commit.Id" c.Id)

            Assert.That(
                jj
                |> List.forall (fun c -> c.Id.Length > 0 && c.Id |> Seq.forall Uri.IsHexDigit),
                Is.True,
                "jj Commit.Id is a (short) hexadecimal commit id"
            )
        }

    [<Test>]
    member _.LogPathsMatchAcrossBackends() : Task =
        task {
            let pair = fixture.Pair
            // A path touched by both commits...
            let! gitNested = pair.Git.LogPaths(pair.GitSandbox.HistoryRev, 10, [ NestedPath ])
            let! jjNested = pair.Jj.LogPaths(pair.JjSandbox.HistoryRev, 10, [ NestedPath ])

            assertSameList
                "LogPaths (nested)"
                (fun (c: Commit) -> c.Description)
                (expectOk "git" "LogPaths" gitNested)
                (expectOk "jj" "LogPaths" jjNested)

            Assert.That(List.length (expectOk "git" "LogPaths" gitNested), Is.EqualTo 2)

            // ...and one touched only by the first, whose name carries spaces (a git pathspec and
            // a jj fileset both have to quote it).
            let! gitSpaced = pair.Git.LogPaths(pair.GitSandbox.HistoryRev, 10, [ SpacedPath ])
            let! jjSpaced = pair.Jj.LogPaths(pair.JjSandbox.HistoryRev, 10, [ SpacedPath ])

            assertSameList
                "LogPaths (spaced path)"
                (fun (c: Commit) -> c.Description)
                (expectOk "git" "LogPaths" gitSpaced)
                (expectOk "jj" "LogPaths" jjSpaced)

            assertListEquals
                "LogPaths (spaced path) descriptions"
                [ "seed the parity scenario" ]
                (expectOk "git" "LogPaths" gitSpaced |> List.map (fun c -> c.Description))

            // An empty path set is refused up front on both — the facade's own guard, before any
            // backend is reached.
            let! gitEmpty = pair.Git.LogPaths(pair.GitSandbox.HistoryRev, 10, [])
            let! jjEmpty = pair.Jj.LogPaths(pair.JjSandbox.HistoryRev, 10, [])
            assertSame "LogPaths []" (Result.isError gitEmpty) (Result.isError jjEmpty)
            Assert.That(Result.isError gitEmpty, Is.True, "an empty path set is refused, not silently unrestricted")
        }

    // --- File content ---------------------------------------------------------

    [<Test>]
    member _.ShowFileMatchesAcrossBackends() : Task =
        task {
            let pair = fixture.Pair
            // A non-ASCII path, read at each backend's own spelling of "the current content".
            let! gitResult = pair.Git.ShowFile(pair.GitSandbox.CommittedRev, UnicodePath)
            let! jjResult = pair.Jj.ShowFile(pair.JjSandbox.CommittedRev, UnicodePath)
            let git = expectOk "git" "ShowFile" gitResult
            let jj = expectOk "jj" "ShowFile" jjResult

            assertSame "ShowFile content" git jj
            Assert.That(git, Is.EqualTo "u1\n", "the trailing newline survives on both backends")

            // A path with spaces, and a missing path (an error on both).
            let! gitSpaced = pair.Git.ShowFile(pair.GitSandbox.CommittedRev, SpacedPath)
            let! jjSpaced = pair.Jj.ShowFile(pair.JjSandbox.CommittedRev, SpacedPath)

            assertSame
                "ShowFile content (spaced path)"
                (expectOk "git" "ShowFile" gitSpaced)
                (expectOk "jj" "ShowFile" jjSpaced)

            let! gitMissing = pair.Git.ShowFile(pair.GitSandbox.CommittedRev, "no/such/file.txt")
            let! jjMissing = pair.Jj.ShowFile(pair.JjSandbox.CommittedRev, "no/such/file.txt")
            assertSame "ShowFile (missing path) is an error" (Result.isError gitMissing) (Result.isError jjMissing)
            Assert.That(Result.isError gitMissing, Is.True)
        }

    [<Test>]
    member _.ShowFileBytesMatchAcrossBackends() : Task =
        task {
            let pair = fixture.Pair
            let! gitResult = pair.Git.ShowFileBytes(pair.GitSandbox.CommittedRev, SpacedPath)
            let! jjResult = pair.Jj.ShowFileBytes(pair.JjSandbox.CommittedRev, SpacedPath)
            let git = expectOk "git" "ShowFileBytes" gitResult
            let jj = expectOk "jj" "ShowFileBytes" jjResult

            // Structural `=` on the arrays, not `Is.EqualTo` (KB K-017).
            assertSame "ShowFileBytes content" git jj
            Assert.That(Text.Encoding.UTF8.GetString git, Is.EqualTo "c1\nc2\n")
        }

    [<Test>]
    member _.AnnotateMatchesAcrossBackends() : Task =
        task {
            let pair = fixture.Pair
            // A committed, unmodified file: `rev = None` annotates the working copy on git and
            // `@` on jj, and with no local edit both resolve to the same committed content.
            let! gitResult = pair.Git.Annotate(SpacedPath, None)
            let! jjResult = pair.Jj.Annotate(SpacedPath, None)
            let git = expectOk "git" "Annotate" gitResult
            let jj = expectOk "jj" "Annotate" jjResult

            assertSameList "Annotate lines" (fun (l: AnnotateLine) -> $"{l.Line}:{l.Content}") git jj

            assertListEquals "Annotate content" [ "c1"; "c2" ] (git |> List.map (fun l -> l.Content))
            assertListEquals "Annotate line numbers (1-based)" [ 1; 2 ] (git |> List.map (fun l -> l.Line))

            // Both backends populate author and date here (unlike `Commit`, whose author/date are
            // git-only). The names differ only in case: TestKit stamps git's `user.name` as
            // `Test` and jj's identity through `JJ_USER`, which it records verbatim.
            List.iter2
                (fun (g: AnnotateLine) (j: AnnotateLine) ->
                    match g.Author, j.Author with
                    | Some gitAuthor, Some jjAuthor ->
                        Assert.That(
                            gitAuthor,
                            Is.EqualTo(jjAuthor).IgnoreCase,
                            $"Annotate line {g.Line}: author differs beyond casing"
                        )
                    | _ ->
                        Assert.Fail
                            $"Annotate line {g.Line}: author must be set on both, git=%A{g.Author}, jj=%A{j.Author}"

                    match g.Date, j.Date with
                    | Some gitDate, Some jjDate ->
                        // Timestamps belong to their own repository, so only the shape is
                        // comparable: a strict ISO-8601 instant with an offset on both.
                        Assert.That(
                            DateTimeOffset.TryParse(
                                gitDate,
                                Globalization.CultureInfo.InvariantCulture,
                                Globalization.DateTimeStyles.None
                            )
                            |> fst,
                            Is.True,
                            $"git Annotate date is not ISO-8601: {gitDate}"
                        )

                        Assert.That(
                            DateTimeOffset.TryParse(
                                jjDate,
                                Globalization.CultureInfo.InvariantCulture,
                                Globalization.DateTimeStyles.None
                            )
                            |> fst,
                            Is.True,
                            $"jj Annotate date is not ISO-8601: {jjDate}"
                        )
                    | _ ->
                        Assert.Fail $"Annotate line {g.Line}: date must be set on both, git=%A{g.Date}, jj=%A{j.Date}"

                    Assert.That(g.Id.Length, Is.GreaterThan 0, "git Annotate id is set")
                    Assert.That(j.Id.Length, Is.GreaterThan 0, "jj Annotate id is set"))
                git
                jj
        }

    // --- Worktrees ------------------------------------------------------------

    [<Test>]
    member _.ListWorktreesMatchAcrossBackends() : Task =
        task {
            let pair = fixture.Pair
            let! gitResult = pair.Git.ListWorktrees()
            let! jjResult = pair.Jj.ListWorktrees()
            let git = expectOk "git" "ListWorktrees" gitResult
            let jj = expectOk "jj" "ListWorktrees" jjResult

            assertSame "ListWorktrees count" (List.length git) (List.length jj)
            Assert.That(List.length git, Is.EqualTo 1, "the scenario attaches no extra worktree")

            let gitEntry = List.head git
            let jjEntry = List.head jj
            assertSame "WorktreeInfo.Branch" gitEntry.Branch jjEntry.Branch
            assertSame "WorktreeInfo.IsBare" gitEntry.IsBare jjEntry.IsBare
            Assert.That(gitEntry.Branch, Is.EqualTo(Some "main"))
            Assert.That(gitEntry.IsBare, Is.False)

            // The commit is the same identity `RepoSnapshot.Head` carries — per backend, since
            // the two sandboxes are different repositories.
            let! gitSnapshot = pair.Git.Snapshot()
            let! jjSnapshot = pair.Jj.Snapshot()

            assertEquals
                "git WorktreeInfo.Commit vs Snapshot.Head"
                (expectOk "git" "Snapshot" gitSnapshot).Head
                gitEntry.Commit

            assertEquals
                "jj WorktreeInfo.Commit vs Snapshot.Head"
                (expectOk "jj" "Snapshot" jjSnapshot).Head
                jjEntry.Commit

            // The path is absolute and points at that backend's own sandbox. Compared by leaf
            // name rather than in full: a temp path can be reached through a symlinked ancestor
            // (macOS `/var` -> `/private/var`), and only one of the two forms comes back here.
            // The separator is normalised first, because this is the one path the two backends
            // do NOT report in the same shape — git prints `/` everywhere, jj hands back the
            // OS-native form (a backslash on Windows). Every *repo-relative* path either backend
            // reports is `/`-separated (asserted throughout the suite); an absolute worktree path
            // is not, so a consumer comparing these has to normalise.
            let leafOf (path: string) =
                Path.GetFileName((toSlash path).TrimEnd '/')

            Assert.That(Path.IsPathRooted gitEntry.Path, Is.True, "git worktree path is absolute")
            Assert.That(Path.IsPathRooted jjEntry.Path, Is.True, "jj workspace path is absolute")
            Assert.That(leafOf gitEntry.Path, Is.EqualTo(leafOf pair.GitSandbox.Path))
            Assert.That(leafOf jjEntry.Path, Is.EqualTo(leafOf pair.JjSandbox.Path))
        }

    // --- The matrix's own completeness ----------------------------------------

    [<Test>]
    member _.EveryFacadeReadMethodHasAParityScenario() =
        // The guard behind the `docs/extending.md` rule: a new `Repo` member must either be
        // covered by a scenario in this matrix or be classified as a non-read operation. Neither
        // list may name a member that no longer exists, so a rename cannot leave a hole.
        let declared =
            typeof<Repo>.GetMethods(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly)
            |> Array.map (fun m -> m.Name)
            // Property getters (`Kind`, `Root`, `Cwd`, and the `Git`/`Jj`/`GitAt`/`JjAt` escape
            // hatches) query no backend, so they are not part of the matrix.
            |> Array.filter (fun name ->
                not (name.StartsWith("get_", StringComparison.Ordinal))
                && not (name.StartsWith("set_", StringComparison.Ordinal)))
            |> Set.ofArray

        let classified = Set.union coveredReadMethods nonReadMembers
        let unclassified = Set.difference declared classified
        let stale = Set.difference classified declared

        Assert.That(
            Set.isEmpty unclassified,
            Is.True,
            $"`Repo` members with no parity scenario: %A{Set.toList unclassified}. A new READ method needs a "
            + "scenario in this matrix (see docs/extending.md); a mutation belongs in `nonReadMembers`."
        )

        Assert.That(
            Set.isEmpty stale,
            Is.True,
            $"these names no longer exist on `Repo`: %A{Set.toList stale} — update the coverage lists."
        )
