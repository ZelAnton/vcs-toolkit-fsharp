module VcsToolkit.Git.Tests

open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.Git

// Control bytes built explicitly so no escape has to survive a round-trip.
let private nul = string (char 0)
let private us = string (char 0x1f)

let private scripted (tokens: string list) (reply: Reply) =
    Git.WithRunner(ScriptedRunner().On(tokens, reply))

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
                Assert.That(s.Ahead, Is.EqualTo(Some 1))
                Assert.That(s.Behind, Is.EqualTo(Some 0))
                Assert.That(s.TrackedChanges, Is.EqualTo 1)
                Assert.That(s.Untracked, Is.EqualTo 1)
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
                Assert.That(stat.FilesChanged, Is.EqualTo 3)
                Assert.That(stat.Insertions, Is.EqualTo 12)
                Assert.That(stat.Deletions, Is.EqualTo 4)
            | Error e -> Assert.Fail $"diff_stat failed: {e}"
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
    member _.MergeCommitBuildsNoFf() : Task =
        task {
            let git = scripted [ "merge"; "--no-ff"; "--no-edit"; "feat" ] (Reply.Ok "")

            match! git.MergeCommit(".", MergeCommit.ForBranch("feat").WithNoFf()) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"merge_commit failed: {e}"
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
    member _.RefNameValidates() =
        Assert.That(RefName.Create "feature/x" |> Result.isOk)
        Assert.That(RefName.Create "-bad" |> Result.isError)
        Assert.That(RefName.Create "has..dots" |> Result.isError)
        Assert.That(RefName.Create "ends.lock" |> Result.isError)
