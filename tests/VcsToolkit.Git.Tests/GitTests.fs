module VcsToolkit.Git.Tests

open System
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.CliSupport
open VcsToolkit.Git

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
        Assert.That(RefName.Create "feature/x" |> Result.isOk)
        Assert.That(RefName.Create "-bad" |> Result.isError)
        Assert.That(RefName.Create "has..dots" |> Result.isError)
        Assert.That(RefName.Create "ends.lock" |> Result.isError)

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
    member _.GitAtRawRunStaysProcessCwd() : Task =
        task {
            // The raw `Run` hatch is a `bare` forwarder — it runs in the PROCESS cwd
            // (`WorkingDirectory = None`), NOT the bound dir. This asymmetry is deliberate.
            let captured, runner = capturing (Reply.Ok "abc\n")
            let git = Git.WithRunner runner

            let! _ = git.At("/bound/dir").Run [ "rev-parse"; "HEAD" ]

            match captured.Value with
            | Some cmd ->
                Assert.That(cmd.WorkingDirectory, Is.EqualTo None, "the raw Run hatch is NOT bound to dir")
                Assert.That(String.concat " " cmd.Arguments, Is.EqualTo "rev-parse HEAD")
            | None -> Assert.Fail "no command captured"
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
