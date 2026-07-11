module VcsToolkit.Git.PathTransportIntegrationTests

open System.Threading.Tasks
open NUnit.Framework
open VcsToolkit.Git
open VcsToolkit.TestKit

/// Real-`git` integration coverage for T-008 (`--literal-pathspecs` + the NUL-safe
/// `--pathspec-from-file=- --pathspec-file-nul` stdin transport on `Add`/`CommitPaths`): the
/// scripted-runner unit tests in `GitTests.fs` (`PathTransportTests`) lock down the argv
/// shape, but only a real `git` proves the two behaviours that actually matter — that a glob
/// metacharacter in a path is matched LITERALLY (not expanded), and that a path set routed
/// through the stdin transport is genuinely staged/committed (the transport actually reaches
/// git, not just an argv shape that happens to look right). Skips (rather than fails) when
/// `git` isn't on PATH.
[<TestFixture>]
type PathTransportTests() =

    let requireGit () =
        try
            Raw.git "." [ "--version" ]
        with _ ->
            // git isn't on PATH (or failed to spawn) — a hermetic CI without it must skip,
            // not fail, this fixture.
            Assert.Ignore "git not available on PATH"

    /// A path list whose combined argv length exceeds the wrapper's `ArgvPathBudget` (30000)
    /// comfortably — enough to force the stdin transport on a real `git`, not just prove the
    /// scripted argv shape.
    let overBudgetPaths () : string list =
        [ for i in 1..600 -> sprintf "dir/file-%050d.txt" i ]

    // --- --literal-pathspecs: a glob metacharacter in a path must match literally -----------

    [<Test>]
    member _.AddMatchesABracketGlobPathLiterallyNotAsACharacterClass() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "literal-pathspecs-add"
            // Without `--literal-pathspecs`, the pathspec `notes[1].txt` is a glob whose `[1]`
            // is a one-character CLASS — it matches `notes1.txt`, NOT the literally-named file.
            repo.Write("notes[1].txt", "literal\n")
            repo.Write("notes1.txt", "glob-matched\n")

            let git = Git.Create()

            match! git.Add(repo.Path, [ "notes[1].txt" ]) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Add failed: {e}"

            match! git.Status repo.Path with
            | Ok entries ->
                match entries |> List.tryFind (fun e -> e.Path = "notes[1].txt") with
                | Some e -> Assert.That(e.Code, Is.EqualTo "A ", "the literally-named file must be staged")
                | None -> Assert.Fail "notes[1].txt must be reported by status"

                match entries |> List.tryFind (fun e -> e.Path = "notes1.txt") with
                | Some e ->
                    Assert.That(
                        e.Code,
                        Is.EqualTo "??",
                        "the glob-matched-but-not-literally-named sibling must stay untracked"
                    )
                | None -> Assert.Fail "notes1.txt must still be reported (untracked)"
            | Error e -> Assert.Fail $"Status failed: {e}"
        }

    [<Test>]
    member _.CommitPathsMatchesABracketGlobPathLiterallyNotAsACharacterClass() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "literal-pathspecs-commit"
            // `commit --only` requires the paths to already be known to git (tracked), so seed
            // both as an ordinary commit first, then dirty both and commit only the
            // literally-bracket-named one via `CommitPaths`.
            repo.Write("notes[1].txt", "literal-v1\n")
            repo.Write("notes1.txt", "glob-matched-v1\n")
            repo.AddAll()
            repo.Commit "seed"

            repo.Write("notes[1].txt", "literal-v2\n")
            repo.Write("notes1.txt", "glob-matched-v2\n")

            let git = Git.Create()

            match! git.CommitPaths(repo.Path, CommitPaths.Create([ "notes[1].txt" ], "literal commit")) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"CommitPaths failed: {e}"

            match! git.LastCommitMessage repo.Path with
            | Ok msg -> Assert.That(msg.Trim(), Is.EqualTo "literal commit")
            | Error e -> Assert.Fail $"LastCommitMessage failed: {e}"

            match! git.Status repo.Path with
            | Ok entries ->
                Assert.That(
                    entries |> List.exists (fun e -> e.Path = "notes[1].txt"),
                    Is.False,
                    "the committed literal file's new content must be clean (no longer pending)"
                )

                match entries |> List.tryFind (fun e -> e.Path = "notes1.txt") with
                | Some e ->
                    Assert.That(
                        e.Code,
                        Is.EqualTo " M",
                        "the glob-matched-but-not-literally-named sibling's edit must stay UNCOMMITTED — proving the pathspec targeted the literal file, not a `[1]`-class glob match of `notes1.txt`"
                    )
                | None -> Assert.Fail "notes1.txt's pending edit must still be reported"
            | Error e -> Assert.Fail $"Status failed: {e}"
        }

    // --- Stdin transport: an over-budget path set really is staged/committed ---------------

    [<Test>]
    member _.AddRoutesLargePathSetThroughStdinTransportAndStagesEveryFile() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "stdin-transport-add"
            let paths = overBudgetPaths ()

            for p in paths do
                repo.Write(p, "x\n")

            let git = Git.Create()

            match! git.Add(repo.Path, paths) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Add failed: {e}"

            match! git.Status repo.Path with
            | Ok entries ->
                Assert.That(entries.Length, Is.EqualTo paths.Length, "every generated file must be reported")

                Assert.That(
                    entries |> List.forall (fun e -> e.Code = "A "),
                    Is.True,
                    "every file must be staged (added) via the stdin transport, not left untracked"
                )
            | Error e -> Assert.Fail $"Status failed: {e}"
        }

    [<Test>]
    member _.CommitPathsRoutesLargePathSetThroughStdinTransportAndCommitsEveryFile() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "stdin-transport-commit"
            let paths = overBudgetPaths ()

            // `commit --only` requires the paths to already be known to git — seed them as an
            // ordinary commit first, then dirty every file and commit the update via `CommitPaths`.
            for p in paths do
                repo.Write(p, "x\n")

            repo.AddAll()
            repo.Commit "seed"

            for p in paths do
                repo.Write(p, "y\n")

            let git = Git.Create()

            match! git.CommitPaths(repo.Path, CommitPaths.Create(paths, "large commit")) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"CommitPaths failed: {e}"

            match! git.LastCommitMessage repo.Path with
            | Ok msg -> Assert.That(msg.Trim(), Is.EqualTo "large commit")
            | Error e -> Assert.Fail $"LastCommitMessage failed: {e}"

            match! git.Status repo.Path with
            | Ok entries ->
                Assert.That(entries, Is.Empty, "every generated file must have been committed — none pending")
            | Error e -> Assert.Fail $"Status failed: {e}"
        }
