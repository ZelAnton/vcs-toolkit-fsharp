module VcsToolkit.Git.StashIntegrationTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open VcsToolkit.CliSupport
open VcsToolkit.Git
open VcsToolkit.TestKit

/// Real-`git` integration coverage for `Git.StashList`/`StashApply`/`StashDrop`:
/// push a stash via the existing `StashPush`, list it, apply it (without dropping), then
/// drop it — checking the stash list, and the working tree, at each step. Skips (rather
/// than fails) when `git` isn't on PATH.
[<TestFixture>]
type StashIntegrationTests() =

    let requireGit () =
        try
            Raw.git "." [ "--version" ]
        with _ ->
            // git isn't on PATH (or failed to spawn) — a hermetic CI without it must skip,
            // not fail, this fixture.
            Assert.Ignore "git not available on PATH"

    [<Test>]
    member _.PushListApplyDropRoundTrip() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "stash-roundtrip"
            repo.CommitFile("a.txt", "base\n", "seed")
            repo.Write("a.txt", "dirty\n")

            let git = Git.Create()
            let aPath = Path.Combine(repo.Path, "a.txt")

            match! git.StashPush(repo.Path, false) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"stash push failed: {e}"

            // The dirty change is stashed away — the working tree is clean again.
            Assert.That(File.ReadAllText aPath, Is.EqualTo "base\n")

            match! git.StashList repo.Path with
            | Ok [ entry ] ->
                Assert.That(entry.Index, Is.EqualTo 0u)
                Assert.That(entry.Hash.Length, Is.EqualTo 40, "a real repo's stash commit is a 40-hex sha1")
                Assert.That(entry.Branch, Is.EqualTo(Some "main"))
            | Ok other -> Assert.Fail $"expected exactly one stash entry after push, got {other.Length}"
            | Error e -> Assert.Fail $"stash list failed: {e}"

            match! git.StashApply(repo.Path, 0u) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"stash apply failed: {e}"

            // Apply restores the working tree...
            Assert.That(File.ReadAllText aPath, Is.EqualTo "dirty\n")

            // ...but does NOT drop the entry (unlike `StashPop`).
            match! git.StashList repo.Path with
            | Ok afterApply -> Assert.That(afterApply.Length, Is.EqualTo 1, "apply must not drop the entry")
            | Error e -> Assert.Fail $"stash list after apply failed: {e}"

            match! git.StashDrop(repo.Path, 0u) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"stash drop failed: {e}"

            match! git.StashList repo.Path with
            | Ok afterDrop -> Assert.That(afterDrop, Is.Empty, "drop must remove the entry")
            | Error e -> Assert.Fail $"stash list after drop failed: {e}"
        }

    [<Test>]
    member _.SwitchWithStashRestoresItsChangesWhenCheckoutAddsForeignStash() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "stash-switch-interleaving"
            repo.CommitFile("staged.txt", "base-staged\n", "seed staged")
            repo.CommitFile("unstaged.txt", "base-unstaged\n", "seed unstaged")
            repo.Branch "feature"

            repo.Write("staged.txt", "caller-staged\n")
            repo.AddAll()
            repo.Write("unstaged.txt", "caller-unstaged\n")
            repo.Write("caller-untracked.txt", "caller-untracked\n")

            let foreignMarker = "foreign-stash-marker"
            let interleaveMarker = "interleave-attempt-marker"
            let foreignHashPath = Path.Combine(repo.Path, ".git", "foreign-stash-hash")
            let hookDirectory = Path.Combine(repo.Path, ".git", "hooks")
            let hookPath = Path.Combine(hookDirectory, "post-checkout")
            Directory.CreateDirectory hookDirectory |> ignore

            // `post-checkout` creates a foreign stash before the restore helper takes the Git
            // ref locks. The later observer attempt runs while those locks are held and must be
            // rejected by Git, proving that relist and exact cleanup cannot be interleaved.
            let hook =
                "#!/bin/sh\n"
                + "printf 'foreign-staged\\n' > foreign-staged.txt\n"
                + "git add -- foreign-staged.txt\n"
                + "printf 'foreign-untracked\\n' > foreign-untracked.txt\n"
                + $"git stash push --include-untracked --message {foreignMarker} >/dev/null\n"
                + "git rev-parse --verify refs/stash > .git/foreign-stash-hash\n"

            File.WriteAllText(hookPath, hook)

            if not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(
                    hookPath,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                )

            let observed = ResizeArray<CommandEvent>()
            let typedStashLists = ref 0
            let interleaveError = ref None
            let interleaveSucceeded = ref false

            let git =
                Git
                    .Create()
                    .WithObserver(
                        { new ICommandObserver with
                            member _.OnStarted(command) = observed.Add command

                            member _.OnFinished(command, _, _) =
                                match command.Argv with
                                | [ "stash"; "list"; "-z"; "--format=%gd%x1f%H%x1f%gs" ] when
                                    Interlocked.Increment(typedStashLists) = 2
                                    ->
                                    try
                                        File.WriteAllText(Path.Combine(repo.Path, "interleave.txt"), "interleave\n")
                                        Raw.git repo.Path [ "add"; "--"; "interleave.txt" ]

                                        Raw.git
                                            repo.Path
                                            [ "stash"; "push"; "--include-untracked"; "--message"; interleaveMarker ]

                                        interleaveSucceeded.Value <- true
                                    with e ->
                                        interleaveError.Value <- Some e.Message
                                | _ -> () }
                    )

            match! git.SwitchWithStash(repo.Path, "feature") with
            | Ok() -> ()
            | Error e ->
                let seen =
                    observed
                    |> Seq.map (fun command -> String.concat " " command.Argv)
                    |> String.concat " | "

                Assert.Fail $"switch must succeed through the real Git interleaving: {e}; seen={seen}"

            let expectedForeignHash = File.ReadAllText(foreignHashPath).Trim()

            Assert.That(
                interleaveSucceeded.Value,
                Is.False,
                "a concurrent stash writer must not enter the locked window"
            )

            match interleaveError.Value with
            | Some error ->
                Assert.That(
                    error,
                    Does
                        .Contain("lock")
                        .Or.Contain("File exists")
                        .Or.Contain("Unable to create")
                        .Or.Contain("exited with 1"),
                    "the rejected concurrent writer must report ref-lock contention"
                )
            | None -> Assert.Fail "the locked-window concurrent stash attempt was not executed"

            match! git.CurrentBranch repo.Path with
            | Ok(Some branch) -> Assert.That(branch, Is.EqualTo "feature")
            | Ok None -> Assert.Fail "current branch should be available after checkout"
            | Error e -> Assert.Fail $"current branch check failed: {e}"

            match! git.Status repo.Path with
            | Error e -> Assert.Fail $"restored status failed: {e}"
            | Ok entries ->
                let status path =
                    entries |> List.tryFind (fun entry -> entry.Path = path)

                match status "staged.txt" with
                | Some entry -> Assert.That(entry.Code, Is.EqualTo "M ")
                | None -> Assert.Fail "the caller's staged change was not restored"

                match status "unstaged.txt" with
                | Some entry -> Assert.That(entry.Code, Is.EqualTo " M")
                | None -> Assert.Fail "the caller's unstaged change was not restored"

                match status "caller-untracked.txt" with
                | Some entry -> Assert.That(entry.Code, Is.EqualTo "??")
                | None -> Assert.Fail "the caller's untracked change was not restored"

            Assert.That(File.ReadAllText(Path.Combine(repo.Path, "staged.txt")), Is.EqualTo "caller-staged\n")
            Assert.That(File.ReadAllText(Path.Combine(repo.Path, "unstaged.txt")), Is.EqualTo "caller-unstaged\n")

            Assert.That(
                File.ReadAllText(Path.Combine(repo.Path, "caller-untracked.txt")),
                Is.EqualTo "caller-untracked\n"
            )

            match! git.StashList repo.Path with
            | Ok [ foreign ] ->
                let seen =
                    observed
                    |> Seq.map (fun command -> String.concat " " command.Argv)
                    |> String.concat " | "

                Assert.That(
                    foreign.Hash,
                    Is.EqualTo expectedForeignHash,
                    $"foreign stash mismatch: actual={foreign.Hash}, expected={expectedForeignHash}, message={foreign.Message}; seen={seen}"
                )

                Assert.That(foreign.Message, Is.EqualTo $"On feature: {foreignMarker}")
                Assert.That(foreign.Branch, Is.EqualTo(Some "feature"))

                match! git.Run(repo.Path, [ "show"; foreign.Hash + ":foreign-staged.txt" ]) with
                | Ok content -> Assert.That(content, Is.EqualTo "foreign-staged")
                | Error e -> Assert.Fail $"foreign staged content was not preserved: {e}"

                match! git.Run(repo.Path, [ "show"; foreign.Hash + "^3:foreign-untracked.txt" ]) with
                | Ok content -> Assert.That(content, Is.EqualTo "foreign-untracked")
                | Error e -> Assert.Fail $"foreign untracked content was not preserved: {e}"
            | Ok entries ->
                let details =
                    entries
                    |> List.map (fun entry -> $"{entry.Hash} {entry.Message}")
                    |> String.concat " | "

                Assert.Fail $"expected exactly one untouched foreign stash, got {entries.Length}: {details}"
            | Error e -> Assert.Fail $"foreign stash list failed: {e}"

            let callerApply =
                observed
                |> Seq.tryFind (fun command ->
                    match command.Argv with
                    | [ "stash"; "apply"; "--index"; _ ] -> true
                    | _ -> false)

            match callerApply with
            | Some command ->
                match command.Argv with
                | [ "stash"; "apply"; "--index"; hash ] ->
                    Assert.That(
                        (hash.Length = 40 || hash.Length = 64)
                        && (hash |> Seq.forall Char.IsAsciiHexDigit),
                        Is.True,
                        "the apply must target the caller's exact stash object"
                    )
                | _ -> Assert.Fail "unexpected exact stash apply argv"
            | None -> Assert.Fail "the exact stash apply command was not observed"
        }
