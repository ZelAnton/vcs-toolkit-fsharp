module VcsToolkit.Git.Tests

open System
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.CliSupport
open VcsToolkit.Git
open VcsToolkit.TestKit

// Control bytes built explicitly so no escape has to survive a round-trip.
let private nul = string (char 0)
let private us = string (char 0x1f)
let private tab = string (char 9)

let private scripted (tokens: string list) (reply: Reply) =
    Git.WithRunner(ScriptedRunner().On(tokens, reply))

/// A runner that records the last `Command` it was asked to run (its argv + working directory),
/// always replying `reply`. For asserting the `at(dir)` view's byte-identical argv + cwd binding.
let private capturing (reply: Reply) : (Command option ref) * ScriptedRunner =
    let captured = ref (None: Command option)

    let runner =
        ScriptedRunner()
            .When(
                (fun (cmd: Command) ->
                    captured.Value <- Some cmd
                    true),
                reply
            )

    captured, runner

/// Run `f` in a fresh temp directory, removed afterwards (for on-disk marker probes).
let private withTempDir (f: string -> unit) =
    let dir =
        Path.Combine(Path.GetTempPath(), "vcs-git-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore

    try
        f dir
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            // Best-effort cleanup; a leftover temp dir is not a test failure.
            ()

/// A fresh, non-existent temp path to use as a clone destination. The `ScriptedRunner` never
/// actually clones, so nothing is created or removed on disk (`cloneDestCleanable` only probes
/// existence, and cleanup runs on the error path alone).
let private cloneDest () =
    Path.Combine(Path.GetTempPath(), "vcs-clone-" + Guid.NewGuid().ToString("N"))

[<TestFixture>]
type StatusTests() =

    [<Test>]
    member _.StatusParsesScriptedOutput() : Task =
        task {
            let git =
                scripted [ "status"; "--porcelain=v1"; "-z" ] (Reply.Ok($" M a.rs{nul}?? b.rs{nul}"))

            match! git.Status "." with
            | Ok entries ->
                Assert.That(entries.Length, Is.EqualTo 2)
                Assert.That(entries.[0].Code, Is.EqualTo " M")
                Assert.That(entries.[1].Path, Is.EqualTo "b.rs")
            | Error e -> Assert.Fail $"status failed: {e}"
        }

    [<Test>]
    member _.StatusConsumesRenameSourceInEitherColumn() : Task =
        task {
            // A rename's source path is the next NUL record — and the `R` can sit in the WORKTREE
            // (Y) column (` R`), not just the index column. Missing that left the source record as
            // a phantom entry with a garbage code/path; here it must be consumed as `OldPath`.
            let git =
                scripted [ "status"; "--porcelain=v1"; "-z" ] (Reply.Ok($" R new.rs{nul}old.rs{nul} M other.rs{nul}"))

            match! git.Status "." with
            | Ok entries ->
                Assert.That(entries.Length, Is.EqualTo 2, "the source record must be consumed, not a phantom entry")
                Assert.That(entries.[0].Code, Is.EqualTo " R")
                Assert.That(entries.[0].Path, Is.EqualTo "new.rs")
                Assert.That(entries.[0].OldPath, Is.EqualTo(Some "old.rs"))
                Assert.That(entries.[1].Code, Is.EqualTo " M")
                Assert.That(entries.[1].Path, Is.EqualTo "other.rs")
            | Error e -> Assert.Fail $"status failed: {e}"
        }

    [<Test>]
    member _.StatusTrackedExcludesUntracked() : Task =
        task {
            // The --untracked-files=no token must be present, so this only matches if it is built.
            let git =
                scripted [ "status"; "--porcelain=v1"; "-z"; "--untracked-files=no" ] (Reply.Ok($" M a.rs{nul}"))

            match! git.StatusTracked "." with
            | Ok entries -> Assert.That(entries.Length, Is.EqualTo 1)
            | Error e -> Assert.Fail $"status_tracked failed: {e}"
        }

    [<Test>]
    member _.BranchStatusParsesV2() : Task =
        task {
            let out =
                [ "# branch.oid abc"
                  "# branch.head main"
                  "# branch.upstream origin/main"
                  "# branch.ab +1 -0"
                  "1 .M N... 100644 100644 100644 1 2 a.rs"
                  "? new.txt" ]
                |> List.map (fun l -> l + nul)
                |> String.concat ""

            let git = scripted [ "status"; "--porcelain=v2"; "--branch"; "-z" ] (Reply.Ok out)

            match! git.BranchStatus "." with
            | Ok s ->
                Assert.That(s.Branch, Is.EqualTo(Some "main"))
                Assert.That(s.Upstream, Is.EqualTo(Some "origin/main"))
                Assert.That(s.Ahead, Is.EqualTo(Some 1UL))
                Assert.That(s.Behind, Is.EqualTo(Some 0UL))
                Assert.That(s.TrackedChanges, Is.EqualTo 1UL)
                Assert.That(s.Untracked, Is.EqualTo 1UL)
                Assert.That(s.IsDirty)
            | Error e -> Assert.Fail $"branch_status failed: {e}"
        }

    [<Test>]
    member _.ConflictedFilesParsesNulList() : Task =
        task {
            let git =
                scripted
                    [ "diff"; "--name-only"; "--diff-filter=U"; "-z" ]
                    (Reply.Ok($"a.rs{nul}sub/spaced name.rs{nul}"))

            match! git.ConflictedFiles "." with
            | Ok paths ->
                Assert.That(paths.Length, Is.EqualTo 2)
                Assert.That(paths.[1], Is.EqualTo "sub/spaced name.rs")
            | Error e -> Assert.Fail $"conflicted_files failed: {e}"
        }

[<TestFixture>]
type QueryTests() =

    [<Test>]
    member _.RevParseShortBuildsShortFlag() : Task =
        task {
            let git = scripted [ "rev-parse"; "--short"; "HEAD" ] (Reply.Ok "a1b2c3d\n")

            match! git.RevParseShort(".", "HEAD") with
            | Ok out -> Assert.That(out, Is.EqualTo "a1b2c3d")
            | Error e -> Assert.Fail $"rev_parse_short failed: {e}"
        }

    [<Test>]
    member _.BlameParsesLinePorcelain() : Task =
        task {
            let sha = "0123456789abcdef0123456789abcdef01234567"

            let out =
                [ sha + " 1 1 1" // <sha> <orig-line> <final-line> <group-size>
                  "author Alice Example"
                  "author-mail <alice@example.com>"
                  "author-time 1700000000"
                  "author-tz +0000"
                  "summary first commit"
                  "filename f.txt"
                  tab + "let x = 1" ] // tab-prefixed content line closes the record
                |> String.concat "\n"

            let git = scripted [ "blame"; "--line-porcelain" ] (Reply.Ok out)

            match! git.Blame(".", "f.txt", None) with
            | Ok lines ->
                Assert.That(lines.Length, Is.EqualTo 1)
                Assert.That(lines.[0].Commit, Is.EqualTo sha)
                Assert.That(lines.[0].OrigLine, Is.EqualTo 1)
                Assert.That(lines.[0].FinalLine, Is.EqualTo 1)
                Assert.That(lines.[0].Author, Is.EqualTo "Alice Example")
                Assert.That(lines.[0].AuthorTime, Is.EqualTo 1700000000L)
                Assert.That(lines.[0].AuthorTz, Is.EqualTo "+0000")
                Assert.That(lines.[0].Content, Is.EqualTo "let x = 1")
            | Error e -> Assert.Fail $"blame failed: {e}"
        }

    [<Test>]
    member _.BlameKeepsFinalBlankLine() : Task =
        task {
            let sha = "0123456789abcdef0123456789abcdef01234567"
            // A file ending in a BLANK line: its porcelain content line is a bare `\t`, and the
            // output ends there (no trailing newline). `parseBlamePorcelain` closes a record only
            // on that `\t` line, so a trimming feed would silently DROP the final entry — the
            // untrimmed feed must keep both lines.
            let out = [ sha + " 1 1"; tab + "first"; sha + " 2 2"; tab ] |> String.concat "\n"

            let git = scripted [ "blame"; "--line-porcelain" ] (Reply.Ok out)

            match! git.Blame(".", "f.txt", None) with
            | Ok lines ->
                Assert.That(lines.Length, Is.EqualTo 2, "the final blank line must not be dropped")
                Assert.That(lines.[1].FinalLine, Is.EqualTo 2)
                Assert.That(lines.[1].Content, Is.EqualTo "")
            | Error e -> Assert.Fail $"blame failed: {e}"
        }

    [<Test>]
    member _.CurrentBranchReadsNameOnExitZero() : Task =
        task {
            let git =
                scripted [ "symbolic-ref"; "--quiet"; "--short"; "HEAD" ] (Reply.Ok "main\n")

            match! git.CurrentBranch "." with
            | Ok branch -> Assert.That(branch, Is.EqualTo(Some "main"))
            | Error e -> Assert.Fail $"current_branch failed: {e}"
        }

    [<Test>]
    member _.CurrentBranchIsNoneOnDetachedExitOne() : Task =
        task {
            let git = scripted [ "symbolic-ref" ] (Reply.Exit 1)

            match! git.CurrentBranch "." with
            | Ok branch -> Assert.That(branch, Is.EqualTo None)
            | Error e -> Assert.Fail $"current_branch failed: {e}"
        }

    [<Test>]
    member _.IsUnbornMapsProbe() : Task =
        task {
            // exit 0 -> HEAD resolves -> not unborn.
            let resolves = scripted [ "rev-parse"; "--verify"; "-q"; "HEAD" ] (Reply.Ok "abc\n")

            match! resolves.IsUnborn "." with
            | Ok b -> Assert.That(b, Is.False)
            | Error e -> Assert.Fail $"{e}"

            // exit 1 -> no commit yet -> unborn.
            let unborn = scripted [ "rev-parse"; "--verify"; "-q"; "HEAD" ] (Reply.Exit 1)

            match! unborn.IsUnborn "." with
            | Ok b -> Assert.That(b)
            | Error e -> Assert.Fail $"{e}"
        }

    [<Test>]
    member _.BranchesMarkCurrentAndSkipDetached() : Task =
        task {
            let git =
                scripted [ "branch"; "--no-column" ] (Reply.Ok "* main\n  feature\n  (HEAD detached at abc)\n")

            match! git.Branches "." with
            | Ok branches ->
                Assert.That(branches.Length, Is.EqualTo 2)
                Assert.That(branches.[0].Name, Is.EqualTo "main")
                Assert.That(branches.[0].Current)
                Assert.That(branches.[1].Name, Is.EqualTo "feature")
                Assert.That(branches.[1].Current, Is.False)
            | Error e -> Assert.Fail $"branches failed: {e}"
        }

    [<Test>]
    member _.LogParsesUnitSeparatedFields() : Task =
        task {
            let out =
                $"abc123{us}abc{us}Ada{us}2026-05-31T10:00:00+00:00{us}Add feature{nul}"
                + $"def456{us}def{us}Linus{us}2026-05-30T09:00:00+00:00{us}Fix bug{nul}"

            let git = scripted [ "log"; "HEAD" ] (Reply.Ok out)

            match! git.Log(".", "HEAD", 50) with
            | Ok commits ->
                Assert.That(commits.Length, Is.EqualTo 2)
                Assert.That(commits.[0].Hash, Is.EqualTo "abc123")
                Assert.That(commits.[0].Author, Is.EqualTo "Ada")
                Assert.That(commits.[0].Subject, Is.EqualTo "Add feature")
                Assert.That(commits.[1].Subject, Is.EqualTo "Fix bug")
            | Error e -> Assert.Fail $"log failed: {e}"
        }

    [<Test>]
    member _.LogPreservesSubjectWith0x1f() : Task =
        task {
            let subject = $"Keep{us}this separator"
            let out = $"abc123{us}abc{us}Ada{us}2026-05-31T10:00:00+00:00{us}{subject}{nul}"
            let git = scripted [ "log"; "HEAD" ] (Reply.Ok out)

            match! git.Log(".", "HEAD", 50) with
            | Ok commits ->
                Assert.That(commits.Length, Is.EqualTo 1)
                Assert.That(commits.[0].Subject, Is.EqualTo subject)
            | Error e -> Assert.Fail $"log failed: {e}"
        }

    [<Test>]
    member _.DiffStatParsesShortstat() : Task =
        task {
            let git =
                scripted
                    [ "diff"; "--shortstat"; "HEAD~1..HEAD" ]
                    (Reply.Ok " 3 files changed, 12 insertions(+), 4 deletions(-)\n")

            match! git.DiffStat(".", "HEAD~1..HEAD") with
            | Ok stat ->
                Assert.That(stat.FilesChanged, Is.EqualTo 3UL)
                Assert.That(stat.Insertions, Is.EqualTo 12UL)
                Assert.That(stat.Deletions, Is.EqualTo 4UL)
            | Error e -> Assert.Fail $"diff_stat failed: {e}"
        }

    [<Test>]
    member _.RemoteBranchExistsBuildsLsRemoteHeadsRef() : Task =
        task {
            let git =
                scripted
                    [ "ls-remote"; "origin"; "refs/heads/feature/T-010_fix" ]
                    (Reply.Ok "abc123\trefs/heads/feature/T-010_fix\n")

            match! git.RemoteBranchExists(".", "feature/T-010_fix") with
            | Ok exists -> Assert.That(exists)
            | Error e -> Assert.Fail $"remote_branch_exists failed: {e}"
        }

    [<Test>]
    member _.RemoteBranchesInheritsTheCallerTimeout() : Task =
        task {
            // A slow read-only remote must receive the full budget configured by the caller.
            let budget = TimeSpan.FromMinutes 2.0
            let captured, runner = capturing (Reply.Ok "abc123\trefs/heads/feature/slow\n")
            let git = (Git.WithRunner runner).DefaultTimeout budget

            match! git.RemoteBranches(".", "origin") with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"remote_branches failed: {e}"

            match captured.Value with
            | Some cmd -> Assert.That(cmd.ConfiguredTimeout, Is.EqualTo(Some budget))
            | None -> Assert.Fail "RemoteBranches did not spawn git"
        }

    [<Test>]
    member _.RemoteBranchExistsInheritsTheCallerTimeout() : Task =
        task {
            // This must match RemoteBranches rather than restoring a hidden 10-second cap.
            let budget = TimeSpan.FromMinutes 2.0
            let captured, runner = capturing (Reply.Ok "abc123\trefs/heads/feature/slow\n")
            let git = (Git.WithRunner runner).DefaultTimeout budget

            match! git.RemoteBranchExists(".", "feature/slow") with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"remote_branch_exists failed: {e}"

            match captured.Value with
            | Some cmd -> Assert.That(cmd.ConfiguredTimeout, Is.EqualTo(Some budget))
            | None -> Assert.Fail "RemoteBranchExists did not spawn git"
        }

    [<Test>]
    member _.RemoteBranchExistsRejectsEmptyGlobAndControlNames() : Task =
        task {
            // T-002: an empty name, or one carrying a glob (`*?[:`), a space, or a control
            // character, must be refused BEFORE `ls-remote` spawns — the guard fails before any
            // spawn, so the fallback reply (which would otherwise report a false "exists") is
            // never reached.
            let git =
                Git.WithRunner(ScriptedRunner().Fallback(Reply.Ok "abc123\trefs/heads/x\n"))

            let bad =
                [ ""; "feature/*"; "feature/?"; "feature/[a]"; "a:b"; "two words"; "bad\nname" ]

            for name in bad do
                match! git.RemoteBranchExists(".", name) with
                | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "git")
                | Error e -> Assert.Fail $"expected a Spawn refusal for \"{name}\", got {e}"
                | Ok _ -> Assert.Fail $"expected \"{name}\" to be refused"
        }

[<TestFixture>]
type MutationTests() =

    [<Test>]
    member _.PushBuildsUpstreamFlag() : Task =
        task {
            // Matches only if -u, origin and feature are all in the argv.
            let git = scripted [ "push"; "-u"; "origin"; "feature" ] (Reply.Ok "")

            match! git.Push(".", GitPush.Branch("feature").WithUpstream()) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"push failed: {e}"
        }

    [<Test>]
    member _.PushRejectsForceAndMultiRefRefspecs() : Task =
        task {
            // M16: a `+`-leading (force) or extra `:` (multi-ref) refspec must be refused BEFORE
            // spawning — a UI/bot smuggling `+main` through a branch name must not silently
            // force-push over the remote's non-fast-forward history.
            let git = Git.WithRunner(ScriptedRunner().Fallback(Reply.Ok ""))

            match! git.Push(".", GitPush.Branch "+main") with
            | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "git")
            | Error e -> Assert.Fail $"expected a Spawn refusal, got {e}"
            | Ok() -> Assert.Fail "a `+`-leading (force) refspec must be refused"

            match!
                git.Push(
                    ".",
                    { Remote = "origin"
                      Refspec = "a:b:c"
                      SetUpstream = false }
                )
            with
            | Error(ProcessError.Spawn _) -> ()
            | Error e -> Assert.Fail $"expected a Spawn refusal, got {e}"
            | Ok() -> Assert.Fail "a multi-`:` refspec must be refused"

            // A legitimate single-`:` `local:remote` still passes the guard.
            let ok = scripted [ "push"; "origin"; "local:remote" ] (Reply.Ok "")

            match!
                ok.Push(
                    ".",
                    { Remote = "origin"
                      Refspec = "local:remote"
                      SetUpstream = false }
                )
            with
            | Ok() -> ()
            | Error e -> Assert.Fail $"a single-colon refspec must pass: {e}"
        }

    [<Test>]
    member _.PushRejectsEmptySideRefspecs() : Task =
        task {
            // M16: an empty side of the `:` must be refused BEFORE spawning — `:branch` deletes
            // the remote branch, `:` pushes all matching branches, and `local:` pushes to an
            // empty remote ref; all are destructive fan-out/deletion the typed `Push` API
            // claims impossible (only the raw `Run` escape hatch may do this deliberately).
            let git = Git.WithRunner(ScriptedRunner().Fallback(Reply.Ok ""))

            let rejected = [ ":branch"; ":"; "local:" ]

            for refspec in rejected do
                match!
                    git.Push(
                        ".",
                        { Remote = "origin"
                          Refspec = refspec
                          SetUpstream = false }
                    )
                with
                | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "git")
                | Error e -> Assert.Fail $"expected a Spawn refusal for \"{refspec}\", got {e}"
                | Ok() -> Assert.Fail $"an empty-side refspec \"{refspec}\" must be refused"

            // The valid forms still pass the guard.
            let plain = scripted [ "push"; "origin"; "branch" ] (Reply.Ok "")

            match! plain.Push(".", GitPush.Branch "branch") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"a plain branch refspec must pass: {e}"

            let localRemote = scripted [ "push"; "origin"; "local:remote" ] (Reply.Ok "")

            match!
                localRemote.Push(
                    ".",
                    { Remote = "origin"
                      Refspec = "local:remote"
                      SetUpstream = false }
                )
            with
            | Ok() -> ()
            | Error e -> Assert.Fail $"a local:remote refspec must pass: {e}"
        }

    [<Test>]
    member _.FetchBranchBuildsRefspec() : Task =
        task {
            let git =
                scripted
                    [ "fetch"
                      "--quiet"
                      "origin"
                      "refs/heads/feature/T-010_fix:refs/remotes/origin/feature/T-010_fix" ]
                    (Reply.Ok "")

            match! git.FetchBranch(".", "feature/T-010_fix") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"fetch_branch failed: {e}"
        }

    [<Test>]
    member _.FetchBranchRejectsEmptyGlobAndControlNames() : Task =
        task {
            // T-002: an empty branch name, or one carrying a glob (`*?[:`), a space, or a control
            // character, would turn the fetch refspec into a glob (fan-out across every matching
            // ref) or otherwise break it — refused BEFORE `fetch` spawns.
            let git = Git.WithRunner(ScriptedRunner().Fallback(Reply.Ok ""))

            let bad =
                [ ""; "feature/*"; "feature/?"; "feature/[a]"; "a:b"; "two words"; "bad\nname" ]

            for name in bad do
                match! git.FetchBranch(".", name) with
                | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "git")
                | Error e -> Assert.Fail $"expected a Spawn refusal for \"{name}\", got {e}"
                | Ok() -> Assert.Fail $"expected \"{name}\" to be refused"
        }

    [<Test>]
    member _.SwitchWithStashSkipsPopWhenNothingSaved() : Task =
        task {
            // Data-loss guard: `stash push` can exit 0 having saved NOTHING (e.g. a submodule-only
            // dirty tree that `status` still reports). If the stash depth is unchanged, the switch
            // must NOT pop — a bare pop would splat an UNRELATED pre-existing stash. The `Fallback`
            // fails on any stray `stash pop`, so an errant pop becomes a test failure.
            let runner =
                ScriptedRunner()
                    .On([ "status"; "--porcelain=v1"; "-z" ], Reply.Ok(" M sub" + nul)) // dirty
                    .On([ "stash"; "list" ], Reply.Ok "stash@{0}: WIP on main\n") // depth 1, both calls
                    .On([ "stash"; "push" ], Reply.Ok "") // exits 0 having saved nothing
                    .On([ "checkout" ], Reply.Ok "")
                    .Fallback(Reply.Fail(1, "unexpected command — a stray stash pop would lose data"))

            let git = Git.WithRunner runner

            match! git.SwitchWithStash(".", "feature") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"switch must succeed without popping when nothing was stashed: {e}"
        }

    [<Test>]
    member _.AmInProgressIsDistinctFromRebase() =
        // M20: `git am` and an apply-backend rebase share the `rebase-apply/` dir, but am marks
        // it with an `applying` file. `IsAmInProgress` must fire only on that marker and
        // `IsRebaseInProgress` must NOT — an am aborts with `am --abort`, not `rebase --abort`,
        // so mislabelling it a rebase would reset HEAD with the wrong safety ref.
        let boolOf (t: Task<Result<bool, ProcessError>>) =
            match t.GetAwaiter().GetResult() with
            | Ok v -> v
            | Error e -> failwithf "unexpected error: %A" e

        // A `git am` in progress: `rebase-apply/applying` present.
        withTempDir (fun dir ->
            let gitDir = Path.Combine(dir, ".git")
            Directory.CreateDirectory(Path.Combine(gitDir, "rebase-apply")) |> ignore
            File.WriteAllText(Path.Combine(gitDir, "rebase-apply", "applying"), "")
            let git = scripted [ "rev-parse"; "--git-dir" ] (Reply.Ok(gitDir + "\n"))
            Assert.That(boolOf (git.IsAmInProgress dir), Is.True, "an `applying` marker is a git am")
            Assert.That(boolOf (git.IsRebaseInProgress dir), Is.False, "an am must NOT report as a rebase"))

        // An apply-backend rebase: same dir, NO `applying` marker.
        withTempDir (fun dir ->
            let gitDir = Path.Combine(dir, ".git")
            Directory.CreateDirectory(Path.Combine(gitDir, "rebase-apply")) |> ignore
            let git = scripted [ "rev-parse"; "--git-dir" ] (Reply.Ok(gitDir + "\n"))
            Assert.That(boolOf (git.IsAmInProgress dir), Is.False, "no marker → not an am")
            Assert.That(boolOf (git.IsRebaseInProgress dir), Is.True, "a bare rebase-apply is a rebase"))

    [<Test>]
    member _.MergeCommitBuildsNoFf() : Task =
        task {
            let git = scripted [ "merge"; "--no-ff"; "--no-edit"; "feat" ] (Reply.Ok "")

            match! git.MergeCommit(".", MergeCommit.ForBranch("feat").WithNoFf()) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"merge_commit failed: {e}"
        }

/// T-008: `--literal-pathspecs` on `Add`/`CommitPaths`, and the NUL-safe
/// `--pathspec-from-file=- --pathspec-file-nul` stdin transport for a path set whose combined
/// argv length would approach the OS command-line limit.
[<TestFixture>]
type PathTransportTests() =

    /// A path list whose combined argv length (`ArgvPathBudget` accounting: length + 1 per
    /// path) safely exceeds both the wrapper's own budget (30000) and Windows' hard
    /// ~32767-character `CreateProcess` command-line ceiling — for asserting the stdin-transport
    /// branch is chosen over an inline argv. 600 paths * 64 chars (63 + the counted separator) =
    /// 38400.
    let overBudgetPaths () : string list =
        [ for i in 1..600 -> sprintf "dir/file-%050d.txt" i ]

    [<Test>]
    member _.AddAppliesLiteralPathspecsToSmallSetUnchangedOtherwise() : Task =
        task {
            let captured, runner = capturing (Reply.Ok "")
            let git = Git.WithRunner runner

            match! git.Add(".", [ "a.txt"; "b.txt" ]) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Add failed: {e}"

            match captured.Value with
            | Some cmd ->
                Assert.That(
                    String.concat " " cmd.Arguments,
                    Is.EqualTo "--literal-pathspecs add -- a.txt b.txt",
                    "small-set argv must be unchanged apart from the added --literal-pathspecs"
                )
            | None -> Assert.Fail "no command captured"
        }

    [<Test>]
    member _.CommitPathsAppliesLiteralPathspecsToSmallSetUnchangedOtherwise() : Task =
        task {
            let captured, runner = capturing (Reply.Ok "")
            let git = Git.WithRunner runner

            match! git.CommitPaths(".", CommitPaths.Create([ "a.txt" ], "msg")) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"CommitPaths failed: {e}"

            match captured.Value with
            | Some cmd ->
                Assert.That(
                    String.concat " " cmd.Arguments,
                    Is.EqualTo "--literal-pathspecs commit -m msg --only -- a.txt",
                    "small-set argv must be unchanged apart from the added --literal-pathspecs"
                )
            | None -> Assert.Fail "no command captured"
        }

    [<Test>]
    member _.CommitPathsWithAmendKeepsAmendBeforeMessage() : Task =
        task {
            let captured, runner = capturing (Reply.Ok "")
            let git = Git.WithRunner runner

            match! git.CommitPaths(".", CommitPaths.Create([ "a.txt" ], "msg").WithAmend()) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"CommitPaths failed: {e}"

            match captured.Value with
            | Some cmd ->
                Assert.That(
                    String.concat " " cmd.Arguments,
                    Is.EqualTo "--literal-pathspecs commit --amend -m msg --only -- a.txt"
                )
            | None -> Assert.Fail "no command captured"
        }

    [<Test>]
    member _.AddRoutesOverBudgetPathSetThroughStdinTransport() : Task =
        task {
            let captured, runner = capturing (Reply.Ok "")
            let git = Git.WithRunner runner
            let paths = overBudgetPaths ()

            match! git.Add(".", paths) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Add failed: {e}"

            match captured.Value with
            | Some cmd ->
                Assert.That(
                    String.concat " " cmd.Arguments,
                    Is.EqualTo "--literal-pathspecs add --pathspec-from-file=- --pathspec-file-nul",
                    "an over-budget path set must route through the stdin transport, not inline argv"
                )
            | None -> Assert.Fail "no command captured"
        }

    [<Test>]
    member _.CommitPathsRoutesOverBudgetPathSetThroughStdinTransport() : Task =
        task {
            let captured, runner = capturing (Reply.Ok "")
            let git = Git.WithRunner runner
            let paths = overBudgetPaths ()

            match! git.CommitPaths(".", CommitPaths.Create(paths, "big commit")) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"CommitPaths failed: {e}"

            match captured.Value with
            | Some cmd ->
                Assert.That(
                    String.concat " " cmd.Arguments,
                    Is.EqualTo
                        "--literal-pathspecs commit -m big commit --only --pathspec-from-file=- --pathspec-file-nul",
                    "an over-budget path set must route through the stdin transport, not inline argv"
                )
            | None -> Assert.Fail "no command captured"
        }

    [<Test>]
    member _.AddRejectsEmbeddedNulPathBeforeSpawning() : Task =
        task {
            // The guard fires before any spawn, so a fallback that would fail loudly is never
            // reached — proving atomicity (no partial add).
            let git =
                Git.WithRunner(ScriptedRunner().Fallback(Reply.Fail(1, "must not spawn — refusal must precede it")))

            match! git.Add(".", [ "a.txt"; "b" + nul + ".txt" ]) with
            | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "git")
            | Error e -> Assert.Fail $"expected a Spawn refusal, got {e}"
            | Ok() -> Assert.Fail "a path with an embedded NUL must be refused before spawning"
        }

    [<Test>]
    member _.CommitPathsRejectsEmbeddedNulPathBeforeSpawning() : Task =
        task {
            // Same atomicity guarantee as `Add`: the refusal precedes any spawn, so a rejected
            // input never leaves a partial commit behind.
            let git =
                Git.WithRunner(ScriptedRunner().Fallback(Reply.Fail(1, "must not spawn — refusal must precede it")))

            match! git.CommitPaths(".", CommitPaths.Create([ "b" + nul + ".txt" ], "msg")) with
            | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "git")
            | Error e -> Assert.Fail $"expected a Spawn refusal, got {e}"
            | Ok() -> Assert.Fail "a path with an embedded NUL must be refused before spawning"
        }

/// T-023: path-scoped `LogPaths` — `--literal-pathspecs` on the direct and the chunked path
/// (glob metacharacters matched literally), the argv-budget guards, and the large-path-set
/// fallback that chunks the pathspecs across several `git log` calls and restores git's own
/// order via a pathless `--format=%H` oracle.
[<TestFixture>]
type LogPathsTests() =

    // A commit record row in `parseLog`'s unit-separated framing.
    let commitRow (hash: string) (subject: string) =
        $"{hash}{us}{hash.Substring(0, 3)}{us}Auth{us}2026-01-01T00:00:00+00:00{us}{subject}{nul}"

    // A runner that records every command it is asked to run (in call order), then serves the
    // first matching rule added by `rules`. The recording predicate returns `false` so it never
    // matches — it only observes — and real `On`/`When` rules downstream reply.
    let recording (rules: ScriptedRunner -> ScriptedRunner) : ResizeArray<Command> * ScriptedRunner =
        let calls = ResizeArray<Command>()

        let runner =
            rules (
                ScriptedRunner()
                    .When(
                        (fun (cmd: Command) ->
                            calls.Add cmd
                            false),
                        Reply.Ok ""
                    )
            )

        calls, runner

    [<Test>]
    member _.LogPathsScopesToPathsWithLiteralPathspecsOnTheDirectPath() : Task =
        task {
            // A glob metacharacter in the path proves `--literal-pathspecs` is applied on the
            // single-call (direct) path: it must appear verbatim after `--`, not expand as a glob.
            let captured, runner = capturing (Reply.Ok(commitRow "abc123" "Add feature"))
            let git = Git.WithRunner runner

            match! git.LogPaths(".", "HEAD", 50, [ "src/*.fs" ]) with
            | Ok commits ->
                Assert.That(commits.Length, Is.EqualTo 1)
                Assert.That(commits.[0].Hash, Is.EqualTo "abc123")
                Assert.That(commits.[0].Subject, Is.EqualTo "Add feature")

                match captured.Value with
                | Some cmd ->
                    Assert.That(
                        String.concat " " cmd.Arguments,
                        Is.EqualTo
                            "--literal-pathspecs log HEAD -n50 -z --format=%H%x1f%h%x1f%an%x1f%aI%x1f%s -- src/*.fs",
                        "the direct path must carry --literal-pathspecs and pass the glob path literally after --"
                    )
                | None -> Assert.Fail "no command captured"
            | Error e -> Assert.Fail $"LogPaths failed: {e}"
        }

    [<Test>]
    member _.LogPathsRefusesEmptyPathSetBeforeSpawning() : Task =
        task {
            // A fileset-less scope would degrade to an unrestricted log; the refusal precedes any
            // spawn, so the loud fallback is never reached.
            let git =
                Git.WithRunner(ScriptedRunner().Fallback(Reply.Fail(1, "must not spawn — refusal must precede it")))

            match! git.LogPaths(".", "HEAD", 5, []) with
            | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "git")
            | Error e -> Assert.Fail $"expected a Spawn refusal, got {e}"
            | Ok _ -> Assert.Fail "an empty path set must be refused before spawning"
        }

    [<Test>]
    member _.LogPathsRefusesIndividuallyOversizedPathBeforeSpawning() : Task =
        task {
            // `git log` has no NUL-safe transport, so a single path that alone exceeds the argv
            // budget can never be transmitted — reject it up front rather than as a doomed chunk.
            let git =
                Git.WithRunner(ScriptedRunner().Fallback(Reply.Fail(1, "must not spawn — refusal must precede it")))

            let huge = String('z', 30000)

            match! git.LogPaths(".", "HEAD", 5, [ huge ]) with
            | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "git")
            | Error e -> Assert.Fail $"expected a Spawn refusal, got {e}"
            | Ok _ -> Assert.Fail "an individually oversized path must be refused before spawning"
        }

    [<Test>]
    member _.LogPathsChunksDedupesAndReordersByOracleOrderWithLiteralPathspecs() : Task =
        task {
            // Two paths, each ~20000 chars, so their combined argv length exceeds the 30000-char
            // budget and `chunkPathspecs` puts each in its own singleton chunk. One carries a glob
            // metacharacter to prove --literal-pathspecs is applied on the chunked path too.
            let pathA = "a" + String('*', 19999) // 20000 chars, with a glob metacharacter
            let pathB = String('b', 20000)

            // Chunk over pathA returns aaa1 (newest) then the shared ccc1; chunk over pathB returns
            // bbb1 then the same shared ccc1 (which must be deduped). The pathless oracle dictates
            // the true single-call order: bbb1, aaa1, ccc1.
            let chunkAOut = commitRow "aaa1" "newest-a" + commitRow "ccc1" "shared-c"
            let chunkBOut = commitRow "bbb1" "mid-b" + commitRow "ccc1" "shared-c"
            let oracleOut = $"bbb1{nul}aaa1{nul}ccc1{nul}"

            let calls, runner =
                recording (fun r ->
                    r
                        .On([ "rev-parse" ], Reply.Ok "resolvedhead\n")
                        .When((fun cmd -> Seq.contains pathA cmd.Arguments), Reply.Ok chunkAOut)
                        .When((fun cmd -> Seq.contains pathB cmd.Arguments), Reply.Ok chunkBOut)
                        .On([ "--format=%H" ], Reply.Ok oracleOut)
                        .Fallback(Reply.Fail(99, "unmatched command")))

            let git = Git.WithRunner runner

            match! git.LogPaths(".", "HEAD", 5, [ pathA; pathB ]) with
            | Ok commits ->
                Assert.That(
                    String.concat "," (commits |> List.map (fun c -> c.Hash)),
                    Is.EqualTo "bbb1,aaa1,ccc1",
                    "merged chunks must be deduped and reordered into git's native (oracle) order"
                )

                // Every pathspec-bearing chunk call must carry --literal-pathspecs (glob literalness
                // on the chunked path), and the revspec must be the once-resolved id, not "HEAD".
                let chunkCalls =
                    calls
                    |> Seq.filter (fun cmd -> Seq.contains "--literal-pathspecs" cmd.Arguments)
                    |> Seq.toList

                Assert.That(chunkCalls.Length, Is.EqualTo 2, "each of the two singleton chunks is its own call")

                for cmd in chunkCalls do
                    Assert.That(
                        Seq.contains "resolvedhead" cmd.Arguments,
                        Is.True,
                        "each chunk must reuse the once-resolved revspec, not re-resolve HEAD"
                    )

                    Assert.That(
                        Seq.contains "HEAD" cmd.Arguments,
                        Is.False,
                        "the symbolic revspec must not leak into a chunk call"
                    )
            | Error e -> Assert.Fail $"LogPaths failed: {e}"
        }

    [<Test>]
    member _.LogPathsSingleCallAndChunkedCallAgreeOnOrder() : Task =
        task {
            // The single-call fast path returns git's order directly; the chunked path must
            // reconstruct that same order from the oracle. Drive both to the same three commits and
            // assert identical output — a regression guard on the reorder logic.
            let expected = "bbb1,aaa1,ccc1"

            let hashes (commits: Commit list) =
                String.concat "," (commits |> List.map (fun c -> c.Hash))

            // Single call: one path, one invocation, git's own order verbatim.
            let singleOut =
                commitRow "bbb1" "newest" + commitRow "aaa1" "mid" + commitRow "ccc1" "oldest"

            let singleGit = Git.WithRunner(ScriptedRunner().Fallback(Reply.Ok singleOut))

            match! singleGit.LogPaths(".", "HEAD", 5, [ "one.fs" ]) with
            | Ok commits -> Assert.That(hashes commits, Is.EqualTo expected)
            | Error e -> Assert.Fail $"single-call LogPaths failed: {e}"

            // Chunked call: two over-budget paths, merged and reordered by the oracle to the same order.
            let pathA = String('a', 20000)
            let pathB = String('b', 20000)
            let chunkAOut = commitRow "aaa1" "mid"
            let chunkBOut = commitRow "bbb1" "newest" + commitRow "ccc1" "oldest"
            let oracleOut = $"bbb1{nul}aaa1{nul}ccc1{nul}"

            let _, runner =
                recording (fun r ->
                    r
                        .On([ "rev-parse" ], Reply.Ok "resolvedhead\n")
                        .When((fun cmd -> Seq.contains pathA cmd.Arguments), Reply.Ok chunkAOut)
                        .When((fun cmd -> Seq.contains pathB cmd.Arguments), Reply.Ok chunkBOut)
                        .On([ "--format=%H" ], Reply.Ok oracleOut)
                        .Fallback(Reply.Fail(99, "unmatched command")))

            let chunkedGit = Git.WithRunner runner

            match! chunkedGit.LogPaths(".", "HEAD", 5, [ pathA; pathB ]) with
            | Ok commits ->
                Assert.That(
                    hashes commits,
                    Is.EqualTo expected,
                    "the chunked path must reproduce the single-call order"
                )
            | Error e -> Assert.Fail $"chunked LogPaths failed: {e}"
        }

[<TestFixture>]
type GuardTests() =

    [<Test>]
    member _.CheckoutRejectsFlagLikeReference() : Task =
        task {
            // The guard fails before any spawn, so the fallback reply is never reached.
            let git = Git.WithRunner(ScriptedRunner().Fallback(Reply.Ok ""))

            match! git.Checkout(".", "-evil") with
            | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "git")
            | Error e -> Assert.Fail $"expected Spawn, got {e}"
            | Ok() -> Assert.Fail "expected the flag-like reference to be refused"
        }

    [<Test>]
    member _.CheckoutAppendsDoubleDash() : Task =
        task {
            // The trailing `--` is a data-loss guard: without it a `reference` naming a tracked
            // PATH (not a ref) silently restores that path, discarding unstaged edits. The rule is
            // scripted WITH `--` and there is no fallback, so a missing `--` yields no match (an
            // error) rather than a false pass.
            let git = scripted [ "checkout"; "main"; "--" ] (Reply.Ok "")

            match! git.Checkout(".", "main") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"checkout must append `--`: {e}"
        }

    [<Test>]
    member _.RefNameValidates() =
        let rejects (name: string) =
            Assert.That(RefName.Create name |> Result.isError, $"expected '{name}' to be rejected")

        let accepts (name: string) =
            Assert.That(RefName.Create name |> Result.isOk, $"expected '{name}' to be accepted")

        // Valid names must keep passing — one-level (`main`), multi-level, and dots
        // that are neither leading-in-a-component nor a `.lock` suffix / trailing dot.
        accepts "feature/x"
        accepts "main"
        accepts "a.b"
        accepts "v1.2.3"

        // Existing rules (regression guard): empty, leading `-`, whole-name leading
        // `.`, trailing `/`, whole-name `.lock` suffix, `..`, and forbidden/control
        // characters all stay rejected.
        rejects ""
        rejects "-bad"
        rejects ".hidden"
        rejects "feature/"
        rejects "ends.lock"
        rejects "has..dots"
        rejects "a b" // space
        rejects "a~b"
        rejects "a^b"
        rejects "a:b"
        rejects "a?b"
        rejects "a*b"
        rejects "a[b"
        rejects (sprintf "a%cb" (char 92)) // backslash
        rejects (sprintf "a%cb" (char 0x1f)) // ASCII control
        rejects (sprintf "a%cb" (char 0x7f)) // DEL

        // New rules — one case per tightened check.
        rejects "feature/.hidden" // component with a leading dot
        rejects "foo.lock/bar" // component with a `.lock` suffix
        rejects "feature/x." // name ending with a dot
        rejects "main@{u}" // reflog/upstream `@{` sequence
        rejects "@" // the single character `@`
        rejects "feature//x" // empty component (`//`)
        rejects "/leading" // empty component (leading `/`)

[<TestFixture>]
type AtViewTests() =

    [<Test>]
    member _.GitAtBindsDirWithByteIdenticalArgv() : Task =
        task {
            // A modelled method through the `at(dir)` view must produce byte-identical argv to the
            // dir-taking form AND bind `dir` as the command's working directory.
            let captured, runner = capturing (Reply.Ok($" M a.rs{nul}"))
            let git = Git.WithRunner runner

            match! git.At("/bound/dir").Status() with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"GitAt.Status failed: {e}"

            match captured.Value with
            | Some cmd ->
                Assert.That(cmd.WorkingDirectory, Is.EqualTo(Some "/bound/dir"), "the view binds dir as cwd")
                Assert.That(String.concat " " cmd.Arguments, Is.EqualTo "status --porcelain=v1 -z")
            | None -> Assert.Fail "no command captured"
        }

    [<Test>]
    member _.GitAtRawRunBindsDir() : Task =
        task {
            // The raw `Run`/`RunRaw` hatches on the bound view run in the bound `dir`
            // (`WorkingDirectory = Some dir`), like the modelled methods — not the process cwd.
            let captured, runner = capturing (Reply.Ok "abc\n")
            let git = Git.WithRunner runner

            let! _ = git.At("/bound/dir").Run [ "rev-parse"; "HEAD" ]

            match captured.Value with
            | Some cmd ->
                Assert.That(cmd.WorkingDirectory, Is.EqualTo(Some "/bound/dir"), "the raw Run hatch binds dir")
                Assert.That(String.concat " " cmd.Arguments, Is.EqualTo "rev-parse HEAD")
            | None -> Assert.Fail "no command captured"

            let capturedRaw, runnerRaw = capturing (Reply.Ok "")
            let gitRaw = Git.WithRunner runnerRaw

            let! _ = gitRaw.At("/bound/dir").RunRaw [ "status" ]

            match capturedRaw.Value with
            | Some cmd ->
                Assert.That(cmd.WorkingDirectory, Is.EqualTo(Some "/bound/dir"), "the raw RunRaw hatch binds dir")
            | None -> Assert.Fail "no command captured for RunRaw"
        }

    [<Test>]
    member _.GitUnboundRawRunStaysProcessCwd() : Task =
        task {
            // The unbound client's raw `Run` still runs in the process cwd (`WorkingDirectory =
            // None`) — the `dir`-bound form lives only on the `at(dir)` view / the `Run(dir, …)`
            // overload.
            let captured, runner = capturing (Reply.Ok "abc\n")
            let git = Git.WithRunner runner

            let! _ = git.Run [ "rev-parse"; "HEAD" ]

            match captured.Value with
            | Some cmd -> Assert.That(cmd.WorkingDirectory, Is.EqualTo None, "the unbound raw Run is NOT bound to dir")
            | None -> Assert.Fail "no command captured"
        }

[<TestFixture>]
type SequencerStateTests() =

    [<Test>]
    member _.SequencerStateProbesKeyOffTheirOwnMarker() =
        // Each sequencer probe must fire ONLY on its own git-dir marker — and crucially a
        // cherry-pick/revert must NOT read as a merge: a conflicted cherry-pick/revert writes its
        // own head file (`CHERRY_PICK_HEAD`/`REVERT_HEAD`), never `MERGE_HEAD`, so mislabelling it
        // a merge would abort with the wrong command. Bisect keys off `BISECT_LOG`.
        let boolOf (t: Task<Result<bool, ProcessError>>) =
            match t.GetAwaiter().GetResult() with
            | Ok v -> v
            | Error e -> failwithf "unexpected error: %A" e

        withTempDir (fun dir ->
            let gitDir = Path.Combine(dir, ".git")
            Directory.CreateDirectory gitDir |> ignore
            let git = scripted [ "rev-parse"; "--git-dir" ] (Reply.Ok(gitDir + "\n"))

            let touch name =
                File.WriteAllText(Path.Combine(gitDir, name), "x\n")

            let rm name = File.Delete(Path.Combine(gitDir, name))

            // A cherry-pick.
            touch "CHERRY_PICK_HEAD"
            Assert.That(boolOf (git.IsCherryPickInProgress dir), Is.True, "CHERRY_PICK_HEAD is a cherry-pick")
            Assert.That(boolOf (git.IsMergeInProgress dir), Is.False, "a cherry-pick must NOT read as a merge")
            Assert.That(boolOf (git.IsRevertInProgress dir), Is.False)
            Assert.That(boolOf (git.IsBisectInProgress dir), Is.False)
            rm "CHERRY_PICK_HEAD"

            // A revert.
            touch "REVERT_HEAD"
            Assert.That(boolOf (git.IsRevertInProgress dir), Is.True, "REVERT_HEAD is a revert")
            Assert.That(boolOf (git.IsCherryPickInProgress dir), Is.False)
            Assert.That(boolOf (git.IsMergeInProgress dir), Is.False, "a revert must NOT read as a merge")
            Assert.That(boolOf (git.IsBisectInProgress dir), Is.False)
            rm "REVERT_HEAD"

            // A bisect (keyed off BISECT_LOG).
            touch "BISECT_LOG"
            Assert.That(boolOf (git.IsBisectInProgress dir), Is.True, "BISECT_LOG is a bisect")
            Assert.That(boolOf (git.IsCherryPickInProgress dir), Is.False)
            Assert.That(boolOf (git.IsRevertInProgress dir), Is.False)
            rm "BISECT_LOG"

            // Clean git dir → none fire.
            Assert.That(boolOf (git.IsCherryPickInProgress dir), Is.False)
            Assert.That(boolOf (git.IsRevertInProgress dir), Is.False)
            Assert.That(boolOf (git.IsBisectInProgress dir), Is.False))

    [<Test>]
    member _.SequencerAbortContinueCommandsDispatchCorrectArgv() : Task =
        task {
            // The abort/continue command wrappers must emit exactly git's own sub-commands; a
            // continue routed to the wrong one (e.g. `rebase --continue` for a cherry-pick) would
            // corrupt the sequencer. `--continue` carries no argv flag for the editor (that is an
            // env var), so the argv is the bare sub-command.
            let captured, runner = capturing (Reply.Ok "")
            let git = Git.WithRunner runner

            let argv () =
                match captured.Value with
                | Some c -> String.concat " " c.Arguments
                | None -> "<no command captured>"

            let! _ = git.CherryPickAbort "."
            Assert.That(argv (), Is.EqualTo "cherry-pick --abort")
            let! _ = git.CherryPickContinue "."
            Assert.That(argv (), Is.EqualTo "cherry-pick --continue")
            let! _ = git.RevertAbort "."
            Assert.That(argv (), Is.EqualTo "revert --abort")
            let! _ = git.RevertContinue "."
            Assert.That(argv (), Is.EqualTo "revert --continue")
            let! _ = git.BisectReset "."
            Assert.That(argv (), Is.EqualTo "bisect reset")
        }

/// T-018: the known target host of a remote op reaches BOTH the provider lookup (host-keyed
/// secret selection) and the credential helper (host gating), and the fallback policy —
/// no provider / `Ok None` / empty secret → ambient auth; provider `Error` → fail-closed —
/// holds for reads (fetch/clone) and writes (push) alike.
[<TestFixture>]
type RemoteCredentialTests() =

    /// Records into `seen` the host each `CredentialRequest` carried, always yielding `token`.
    let capturingProvider (seen: string option option ref) (token: string) : ICredentialProvider =
        Credentials.providerFn (fun r ->
            seen.Value <- Some r.Host
            Ok(Some(Credential.Token token)))

    let hasCredentialHelper (cmd: Command) =
        cmd.Arguments |> Seq.exists (fun a -> a.Contains "credential.helper=")

    [<Test>]
    member _.CloneScopesCredentialLookupToUrlHost() : Task =
        task {
            // The clone URL's host must reach the CredentialRequest (it was hard-coded `None`
            // before this fix) so a host-keyed provider serves the secret for THIS host, and the
            // resolved credential installs the git credential helper.
            let seen = ref (None: string option option)
            let captured, runner = capturing (Reply.Ok "")

            let git =
                (Git.WithRunner runner).WithCredentials(capturingProvider seen "gh-secret")

            match! git.CloneRepo("https://github.com/o/r.git", cloneDest (), CloneSpec.Create()) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"clone failed: {e}"

            Assert.That(
                seen.Value,
                Is.EqualTo(Some(Some "github.com")),
                "the clone URL host reaches the CredentialRequest"
            )

            match captured.Value with
            | Some cmd ->
                Assert.That(hasCredentialHelper cmd, "a resolved credential installs the git credential helper")
            | None -> Assert.Fail "no clone command captured"
        }

    [<Test>]
    member _.FetchAndPushResolveWithoutAKnownHost() : Task =
        task {
            // Fetch/push target the already-configured remote, so the layer knows no host: the
            // request host is `None` (unscoped) — the credential still resolves and is injected.
            for isPush in [ false; true ] do
                let seen = ref (None: string option option)
                let captured, runner = capturing (Reply.Ok "")

                let git = (Git.WithRunner runner).WithCredentials(capturingProvider seen "secret")

                let! outcome =
                    if isPush then
                        git.Push(".", GitPush.Branch "feature")
                    else
                        git.Fetch "."

                match outcome with
                | Ok() -> ()
                | Error e -> Assert.Fail $"[push={isPush}] remote op failed: {e}"

                Assert.That(seen.Value, Is.EqualTo(Some(None: string option)), $"[push={isPush}] host is None")

                match captured.Value with
                | Some cmd -> Assert.That(hasCredentialHelper cmd, $"[push={isPush}] credential helper injected")
                | None -> Assert.Fail $"[push={isPush}] no command captured"
        }

    [<Test>]
    member _.DeferringProviderIsAmbientForReadAndWrite() : Task =
        task {
            // No provider, `Ok None`, and an empty/whitespace secret all defer to ambient auth:
            // the read (fetch) and the write (push) still run, with NO credential helper injected.
            let cases: (string * ICredentialProvider option) list =
                [ "no provider", None
                  "Ok None", Some(Credentials.providerFn (fun _ -> Ok None))
                  "empty secret", Some(Credentials.providerFn (fun _ -> Ok(Some(Credential.Token "  ")))) ]

            for label, provider in cases do
                for isPush in [ false; true ] do
                    let captured, runner = capturing (Reply.Ok "")

                    let git =
                        match provider with
                        | Some p -> (Git.WithRunner runner).WithCredentials p
                        | None -> Git.WithRunner runner

                    let! outcome =
                        if isPush then
                            git.Push(".", GitPush.Branch "feature")
                        else
                            git.Fetch "."

                    match outcome with
                    | Ok() -> ()
                    | Error e -> Assert.Fail $"[{label}, push={isPush}] ambient op must run: {e}"

                    match captured.Value with
                    | Some cmd ->
                        Assert.That(
                            hasCredentialHelper cmd,
                            Is.False,
                            $"[{label}, push={isPush}] ambient op must not install a credential helper"
                        )
                    | None -> Assert.Fail $"[{label}, push={isPush}] no command captured"
        }

    [<Test>]
    member _.ProviderErrorFailsClosedForReadAndWrite() : Task =
        task {
            // A provider `Error` aborts the remote op up front (fail-closed) for both read and
            // write — git must never be spawned to run unauthenticated behind the caller's back.
            for isPush in [ false; true ] do
                let spawned = ref false

                let runner =
                    ScriptedRunner()
                        .When(
                            (fun _ ->
                                spawned.Value <- true
                                true),
                            Reply.Ok ""
                        )

                let boom =
                    Credentials.providerFn (fun _ -> Error(ProcessError.Exit("git", 1, "", "vault unreachable")))

                let git = (Git.WithRunner runner).WithCredentials boom

                let! outcome =
                    if isPush then
                        git.Push(".", GitPush.Branch "feature")
                    else
                        git.Fetch "."

                match outcome with
                | Error(ProcessError.Exit(_, _, _, stderr)) -> Assert.That(stderr, Is.EqualTo "vault unreachable")
                | other -> Assert.Fail $"[push={isPush}] must fail closed on a provider error: {other}"

                Assert.That(spawned.Value, Is.False, $"[push={isPush}] a fail-closed op must not spawn git")
        }

/// T-030 investigation finding: `Status`/`StatusText`/`StatusTracked`/`BranchStatus`/
/// `ConflictedFiles` all read through `ManagedClient.Run`/`Parse`, which decode `git`'s
/// `-z`-delimited stdout via ProcessKit's `CaptureStringAsync` — `Encoding.UTF8`, non-throwing
/// replacement fallback (invalid byte sequences become U+FFFD rather than raising). A filename
/// this library (or any ordinary git/`.NET` tooling) can itself create is always a .NET
/// `string` — a valid Unicode scalar sequence — and a valid Unicode string round-trips through
/// UTF-8 byte-for-byte, so the decode never loses information for it: `-z` NUL-delimited
/// framing (`Parse.fs`'s `parsePorcelain`/`parsePorcelainV2`/`parseNulPaths`) already bypasses
/// `core.quotepath` octal-escaping, and splits/substrings on the DECODED `string` by character
/// index, never by raw byte offset, so a multi-byte UTF-8 character never gets sliced in half.
/// A filename containing a raw, genuinely-INVALID UTF-8 byte sequence would decode lossily —
/// but that byte sequence can only originate from a non-.NET, non-Unicode-aware tool: .NET's
/// own path APIs can only ever emit valid-UTF-8-encoded names on POSIX and valid UTF-16 names
/// on Windows, so no code path in this library (or in any of the three CI platforms) can
/// produce, let alone portably test, that edge here. Conclusion: LOSSLESS for the reachable
/// domain — no `Git.fs`/`Parse.fs` change. (The jj side raises the identical question for
/// `VcsToolkit.Jj`'s own `-z` readers; jj/Jj.fs and Jj.Tests are reserved for another task in
/// this cohort (T-014/T-023) and are not touched here — flagged as a follow-up for a separate
/// task to apply the same investigation there.)
[<TestFixture>]
type NonUtf8PathIntegrationTests() =

    let requireGit () =
        try
            Raw.git "." [ "--version" ]
        with _ ->
            // git isn't on PATH (or failed to spawn) — a hermetic CI without it must skip, not
            // fail, this fixture.
            Assert.Ignore "git not available on PATH"

    // Cyrillic + CJK + a surrogate-pair emoji: multi-byte UTF-8 on every segment, exercising
    // the `-z` NUL-delimited decode/split boundary with real non-ASCII bytes end to end.
    let exoticName = "файл-文件-📁.txt"

    [<Test>]
    member _.StatusReportsAMultiByteUnicodeFilenameUnmangled() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "non-utf8-status"
            repo.Write(exoticName, "content\n")

            let git = Git.Create()

            match! git.Status repo.Path with
            | Ok entries ->
                match entries |> List.tryFind (fun e -> e.Path = exoticName) with
                | Some e -> Assert.That(e.Code, Is.EqualTo "??")
                | None ->
                    Assert.Fail(
                        sprintf
                            "expected an untracked entry for %s, got paths: %A"
                            exoticName
                            (entries |> List.map (fun e -> e.Path))
                    )
            | Error e -> Assert.Fail $"Status failed: {e}"
        }

    [<Test>]
    member _.ConflictedFilesReportsAMultiByteUnicodeFilenameUnmangled() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "non-utf8-conflict"
            repo.Write(exoticName, "base\n")
            repo.AddAll()
            repo.Commit "seed"

            repo.Branch "feature"
            repo.Checkout "feature"
            repo.Write(exoticName, "feature change\n")
            repo.AddAll()
            repo.Commit "feature change"

            repo.Checkout "main"
            repo.Write(exoticName, "main change\n")
            repo.AddAll()
            repo.Commit "main change"

            // Both sides edited the same line — the merge conflicts by construction. A
            // non-zero exit here is the expected outcome, not a fixture failure.
            try
                repo.Git [ "merge"; "-q"; "--no-edit"; "feature" ]
            with _ ->
                ()

            let git = Git.Create()

            match! git.ConflictedFiles repo.Path with
            | Ok paths ->
                Assert.That(paths.Length, Is.EqualTo 1)
                Assert.That(paths.[0], Is.EqualTo exoticName)
            | Error e -> Assert.Fail $"ConflictedFiles failed: {e}"
        }

/// T-036: bare positional argv slots that were previously unguarded — a clone destination, the
/// worktree add/remove/move paths, and the config value. Each now either refuses a leading-`-`
/// value BEFORE any spawn (proven by the runner never being invoked — `captured` stays `None`,
/// not merely by the returned error code) or, for the config value (which may legitimately begin
/// with `-`, e.g. `-1`), routes through an end-of-options `--` separator so the value is taken
/// verbatim. Valid calls' argv is asserted verbatim to show the guard doesn't perturb them.
[<TestFixture>]
type PositionalArgvGuardTests() =

    // The argv (program excluded) of a captured command, space-joined for a byte-exact assertion
    // (matching the rest of this file's argv checks). The values asserted here are space-free.
    let argv (cmd: Command) = String.concat " " cmd.Arguments

    [<Test>]
    member _.CloneRepoRefusesLeadingDashDestinationBeforeSpawning() : Task =
        task {
            // git accepts options after positionals, so a `dest` like `--upload-pack=<cmd>` is a
            // command-execution vector. `captured` staying `None` proves the runner was never hit.
            let captured, runner = capturing (Reply.Ok "")
            let git = Git.WithRunner runner

            match! git.CloneRepo("https://github.com/o/r.git", "--upload-pack=touch pwned", CloneSpec.Create()) with
            | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "git")
            | Error e -> Assert.Fail $"expected a Spawn refusal, got {e}"
            | Ok() -> Assert.Fail "a leading-dash clone destination must be refused"

            Assert.That(captured.Value.IsNone, "the guard must refuse before any spawn")
        }

    [<Test>]
    member _.CloneRepoAcceptsValidDestinationUnchanged() : Task =
        task {
            // A legitimate url + dest still build exactly `clone <url> <dest>`: the guard leaves a
            // valid call's argv byte-for-byte unchanged.
            let captured, runner = capturing (Reply.Ok "")
            let git = Git.WithRunner runner
            let dest = cloneDest ()

            match! git.CloneRepo("https://github.com/o/r.git", dest, CloneSpec.Create()) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"a valid clone must pass: {e}"

            match captured.Value with
            | Some cmd -> Assert.That(argv cmd, Is.EqualTo $"clone https://github.com/o/r.git {dest}")
            | None -> Assert.Fail "no clone command captured"
        }

    [<Test>]
    member _.WorktreeAddRefusesLeadingDashPathBeforeSpawning() : Task =
        task {
            let captured, runner = capturing (Reply.Ok "")
            let git = Git.WithRunner runner

            match! git.WorktreeAdd(".", WorktreeAdd.Checkout("--no-checkout", "main")) with
            | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "git")
            | Error e -> Assert.Fail $"expected a Spawn refusal, got {e}"
            | Ok() -> Assert.Fail "a leading-dash worktree path must be refused"

            Assert.That(captured.Value.IsNone, "the guard must refuse before any spawn")
        }

    [<Test>]
    member _.WorktreeAddAcceptsValidPathUnchanged() : Task =
        task {
            let captured, runner = capturing (Reply.Ok "")
            let git = Git.WithRunner runner

            match! git.WorktreeAdd(".", WorktreeAdd.Checkout("/tmp/wt", "main")) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"a valid worktree add must pass: {e}"

            match captured.Value with
            | Some cmd -> Assert.That(argv cmd, Is.EqualTo "worktree add /tmp/wt main")
            | None -> Assert.Fail "no worktree add command captured"
        }

    [<Test>]
    member _.WorktreeRemoveRefusesLeadingDashPathBeforeSpawning() : Task =
        task {
            let captured, runner = capturing (Reply.Ok "")
            let git = Git.WithRunner runner

            match! git.WorktreeRemove(".", "--force", false) with
            | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "git")
            | Error e -> Assert.Fail $"expected a Spawn refusal, got {e}"
            | Ok() -> Assert.Fail "a leading-dash worktree path must be refused"

            Assert.That(captured.Value.IsNone, "the guard must refuse before any spawn")
        }

    [<Test>]
    member _.WorktreeRemoveAcceptsValidPathUnchanged() : Task =
        task {
            let captured, runner = capturing (Reply.Ok "")
            let git = Git.WithRunner runner

            match! git.WorktreeRemove(".", "/tmp/wt", true) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"a valid worktree remove must pass: {e}"

            match captured.Value with
            | Some cmd -> Assert.That(argv cmd, Is.EqualTo "worktree remove --force /tmp/wt")
            | None -> Assert.Fail "no worktree remove command captured"
        }

    [<Test>]
    member _.WorktreeMoveRefusesLeadingDashSourceOrDestinationBeforeSpawning() : Task =
        task {
            // A leading-dash SOURCE and a leading-dash DESTINATION are each refused before spawn.
            let cap1, runner1 = capturing (Reply.Ok "")

            match! (Git.WithRunner runner1).WorktreeMove(".", "--force", "/tmp/to") with
            | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "git")
            | Error e -> Assert.Fail $"expected a Spawn refusal for a bad source, got {e}"
            | Ok() -> Assert.Fail "a leading-dash source path must be refused"

            Assert.That(cap1.Value.IsNone, "a bad source must refuse before any spawn")

            let cap2, runner2 = capturing (Reply.Ok "")

            match! (Git.WithRunner runner2).WorktreeMove(".", "/tmp/from", "--force") with
            | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "git")
            | Error e -> Assert.Fail $"expected a Spawn refusal for a bad destination, got {e}"
            | Ok() -> Assert.Fail "a leading-dash destination path must be refused"

            Assert.That(cap2.Value.IsNone, "a bad destination must refuse before any spawn")
        }

    [<Test>]
    member _.WorktreeMoveAcceptsValidPathsUnchanged() : Task =
        task {
            let captured, runner = capturing (Reply.Ok "")
            let git = Git.WithRunner runner

            match! git.WorktreeMove(".", "/tmp/from", "/tmp/to") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"a valid worktree move must pass: {e}"

            match captured.Value with
            | Some cmd -> Assert.That(argv cmd, Is.EqualTo "worktree move /tmp/from /tmp/to")
            | None -> Assert.Fail "no worktree move command captured"
        }

    [<Test>]
    member _.ConfigSetProtectsDashLeadingValueWithSeparator() : Task =
        task {
            // A config value may legitimately begin with `-` (e.g. `-1`). Instead of refusing it, an
            // end-of-options `--` separator makes git take the value verbatim rather than parse it
            // as a flag — the value reaches git AND the argv carries the `--`.
            let captured, runner = capturing (Reply.Ok "")
            let git = Git.WithRunner runner

            match! git.ConfigSet(".", "core.abbrev", "-1") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"a dash-leading config value must pass via the separator: {e}"

            match captured.Value with
            | Some cmd -> Assert.That(argv cmd, Is.EqualTo "config -- core.abbrev -1")
            | None -> Assert.Fail "no config command captured"
        }

    [<Test>]
    member _.ConfigSetKeepsSeparatorForOrdinaryValue() : Task =
        task {
            // The `--` separator is unconditional: an attacker-supplied `--global`/`--unset` value
            // must never redirect or subvert the write. An ordinary value builds `config -- <k> <v>`.
            let captured, runner = capturing (Reply.Ok "")
            let git = Git.WithRunner runner

            match! git.ConfigSet(".", "user.name", "Ada") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"config set failed: {e}"

            match captured.Value with
            | Some cmd -> Assert.That(argv cmd, Is.EqualTo "config -- user.name Ada")
            | None -> Assert.Fail "no config command captured"
        }

    [<Test>]
    member _.ConfigSetStillRefusesDashLeadingKeyBeforeSpawning() : Task =
        task {
            // The key stays guarded — a config key never legitimately begins with `-`, and the `--`
            // separator only protects the value slot.
            let captured, runner = capturing (Reply.Ok "")
            let git = Git.WithRunner runner

            match! git.ConfigSet(".", "--global", "x") with
            | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "git")
            | Error e -> Assert.Fail $"expected a Spawn refusal, got {e}"
            | Ok() -> Assert.Fail "a leading-dash config key must be refused"

            Assert.That(captured.Value.IsNone, "the key guard must refuse before any spawn")
        }
