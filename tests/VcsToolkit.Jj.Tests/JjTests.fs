module VcsToolkit.Jj.Tests

open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.Diff
open VcsToolkit.Jj

// Control bytes built explicitly so no escape has to survive a round-trip.
let private tab = string (char 9)
let private cr = string (char 13)

let private scripted (tokens: string list) (reply: Reply) =
    Jj.WithRunner(ScriptedRunner().On(tokens, reply))

/// A runner that records the last `Command` it ran (argv + working directory), always replying
/// `reply`. For asserting the `at(dir)` view's byte-identical argv + cwd binding.
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

// A runner that answers any command with Ok "" — for verifying that a guard
// refuses BEFORE anything spawns (a refusal returns Error; a leak returns Ok).
let private permissive () =
    Jj.WithRunner(ScriptedRunner().Fallback(Reply.Ok ""))

// ---------------------------------------------------------------------------
// Pure parsers
// ---------------------------------------------------------------------------

[<TestFixture>]
type ParseTests() =

    [<Test>]
    member _.ChangesSplitTabFields() =
        let got =
            JjParse.parseChanges
                $"kztuxlro{tab}38e00654{tab}false{tab}feat: stuff\nqpvuntsm{tab}6ecf997f{tab}true{tab}\n"

        Assert.That(got.Length, Is.EqualTo 2)
        Assert.That(got.[0].ChangeId, Is.EqualTo "kztuxlro")
        Assert.That(got.[0].CommitId, Is.EqualTo "38e00654")
        Assert.That(got.[0].Empty, Is.False)
        Assert.That(got.[0].Description, Is.EqualTo "feat: stuff")
        Assert.That(got.[1].Empty, Is.True)
        Assert.That(got.[1].Description, Is.EqualTo "")

    [<Test>]
    member _.ChangesKeepTabInDescription() =
        let got =
            JjParse.parseChanges $"kztuxlro{tab}38e00654{tab}false{tab}col1{tab}col2\n"

        Assert.That(got.Length, Is.EqualTo 1)
        Assert.That(got.[0].Description, Is.EqualTo $"col1{tab}col2")

    [<Test>]
    member _.AnnotateRowsCarryLineNumbers() =
        let got =
            JjParse.parseAnnotate $"kxoyzabc{tab}fn main() {{\nkxoyzabc{tab}}}\nqlmnopqr{tab}// added later"

        Assert.That(got.Length, Is.EqualTo 3)
        Assert.That(got.[0].ChangeId, Is.EqualTo "kxoyzabc")
        Assert.That(got.[0].Line, Is.EqualTo 1)
        Assert.That(got.[0].Content, Is.EqualTo "fn main() {")
        Assert.That(got.[2].Line, Is.EqualTo 3)
        Assert.That(JjParse.parseAnnotate("").Length, Is.EqualTo 0)

    [<Test>]
    member _.AnnotatePreservesCrAndIgnoresTrailingNewline() =
        let got =
            JjParse.parseAnnotate $"kxoyzabc{tab}fn main() {{{cr}\nkxoyzabc{tab}}}{cr}\n"

        Assert.That(got.Length, Is.EqualTo 2, "no phantom row from the trailing newline")
        Assert.That(got.[0].Content, Is.EqualTo $"fn main() {{{cr}", "CR preserved")
        Assert.That(got.[1].Line, Is.EqualTo 2)

    [<Test>]
    member _.OperationsSplitTabFields() =
        let out =
            $"abc123{tab}user@host{tab}2026-06-05T10:00:00+02:00{tab}new empty commit\n"
            + $"def456{tab}user@host{tab}2026-06-05T09:59:00+02:00{tab}describe commit{tab}with tab\n"

        let ops = JjParse.parseOperations out
        Assert.That(ops.Length, Is.EqualTo 2)
        Assert.That(ops.[0].Id, Is.EqualTo "abc123")
        Assert.That(ops.[0].User, Is.EqualTo "user@host")
        Assert.That(ops.[0].Description, Is.EqualTo "new empty commit")
        // A literal tab in the description survives (split-into-4 keeps the tail).
        Assert.That(ops.[1].Description, Is.EqualTo $"describe commit{tab}with tab")

    [<Test>]
    member _.BookmarksParseNameAndCommit() =
        let got = JjParse.parseBookmarks $"main{tab}f5d07685\nfeature{tab}deadbeef\n"
        Assert.That(got.Length, Is.EqualTo 2)
        Assert.That(got.[0].Name, Is.EqualTo "main")
        Assert.That(got.[0].Target, Is.EqualTo "f5d07685")
        // A bookmark with no normal target keeps an empty commit; an empty name drops.
        let got2 = JjParse.parseBookmarks $"conflicted{tab}\n{tab}stray\n"
        Assert.That(got2.Length, Is.EqualTo 1)
        Assert.That(got2.[0].Name, Is.EqualTo "conflicted")
        Assert.That(got2.[0].Target, Is.EqualTo "")

    [<Test>]
    member _.BookmarksAllParsesLocalAndRemoteDropsEmptyName() =
        let input =
            $"main{tab}{tab}1{tab}f5d07685\n{tab}origin{tab}1{tab}deadbeef\nfeat{tab}origin{tab}0{tab}cafef00d\n"

        let got = JjParse.parseBookmarksAll input
        Assert.That(got.Length, Is.EqualTo 2, "the empty-name row must contribute nothing")
        Assert.That(got.[0].Name, Is.EqualTo "main")
        Assert.That(got.[0].Remote, Is.EqualTo None)
        Assert.That(got.[0].Tracked, Is.True)
        Assert.That(got.[1].Name, Is.EqualTo "feat")
        Assert.That(got.[1].Remote, Is.EqualTo(Some "origin"))
        Assert.That(got.[1].Tracked, Is.False)

    [<Test>]
    member _.ReachableBookmarksFanOutPerName() =
        let got = JjParse.parseReachableBookmarks $"main feat{tab}abc123\n{tab}def456\n"
        Assert.That(got.Length, Is.EqualTo 2)
        Assert.That(got.[0].Name, Is.EqualTo "main")
        Assert.That(got.[0].Target, Is.EqualTo "abc123")
        Assert.That(got.[1].Name, Is.EqualTo "feat")
        Assert.That(got.[1].Target, Is.EqualTo "abc123")

    [<Test>]
    member _.ResolveListExtractsPathsAndNormalises() =
        let got =
            JjParse.parseResolveList "src/a.rs    2-sided conflict\nb.txt    2-sided conflict including 1 deletion\n"

        Assert.That(got.Length, Is.EqualTo 2)
        Assert.That(got.[0], Is.EqualTo "src/a.rs")
        Assert.That(got.[1], Is.EqualTo "b.txt")
        Assert.That(JjParse.parseResolveList("").Length, Is.EqualTo 0)
        // OS-native backslash separators (Windows) are normalised to `/`.
        let win =
            JjParse.parseResolveList $"sub{string (char 92)}c.txt    2-sided conflict\n"

        Assert.That(win.[0], Is.EqualTo "sub/c.txt")

    [<Test>]
    member _.WorkspacesSplitTabFieldsAndBookmarks() =
        let got =
            JjParse.parseWorkspaces $"default{tab}e2aa3420{tab}main,feature\nws1{tab}12345678{tab}\n"

        Assert.That(got.Length, Is.EqualTo 2)
        Assert.That(got.[0].Name, Is.EqualTo "default")
        Assert.That(got.[0].Commit, Is.EqualTo "e2aa3420")
        Assert.That(got.[0].Bookmarks.Length, Is.EqualTo 2)
        Assert.That(got.[1].Bookmarks.Length, Is.EqualTo 0, "no bookmarks → empty list, not [\"\"]")

    [<Test>]
    member _.DiffSummarySplitsStatusAndPath() =
        let got = JjParse.parseDiffSummary "M src/lib.rs\nA new file.txt\nD gone.rs\n"
        Assert.That(got.Length, Is.EqualTo 3)
        Assert.That(got.[0].Status = 'M')
        Assert.That(got.[1].Path, Is.EqualTo "new file.txt")
        Assert.That(got.[1].OldPath, Is.EqualTo None)
        Assert.That(got.[2].Status = 'D')

    [<Test>]
    member _.DiffSummaryExpandsRenameAndCopy() =
        let got =
            JjParse.parseDiffSummary "R {old.rs => new.rs}\nC sub/{a.rs => b.rs}\nM lit{eral}.rs\n"

        Assert.That(got.[0].Status = 'R')
        Assert.That(got.[0].Path, Is.EqualTo "new.rs")
        Assert.That(got.[0].OldPath, Is.EqualTo(Some "old.rs"))
        Assert.That(got.[1].Path, Is.EqualTo "sub/b.rs")
        Assert.That(got.[1].OldPath, Is.EqualTo(Some "sub/a.rs"))
        // A literal `{...}` in a non-rename path (no ` => `) is not mis-expanded.
        Assert.That(got.[2].Path, Is.EqualTo "lit{eral}.rs")
        Assert.That(got.[2].OldPath, Is.EqualTo None)

    [<Test>]
    member _.DiffSummaryNormalisesBackslashSeparators() =
        let bs = string (char 92)

        let got =
            JjParse.parseDiffSummary $"M deep{bs}nested{bs}f.rs\nR win{bs}{{a.rs => b.rs}}\n"

        Assert.That(got.[0].Path, Is.EqualTo "deep/nested/f.rs")
        Assert.That(got.[1].Path, Is.EqualTo "win/b.rs")
        Assert.That(got.[1].OldPath, Is.EqualTo(Some "win/a.rs"))

    [<Test>]
    member _.DiffStatParsesFooter() =
        let input =
            "README.md | 10 +++---\nsrc/lib.rs | 4 +-\n4 files changed, 157 insertions(+), 137 deletions(-)\n"

        let stat = JjParse.parseDiffStat input
        Assert.That(stat.FilesChanged, Is.EqualTo 4UL)
        Assert.That(stat.Insertions, Is.EqualTo 157UL)
        Assert.That(stat.Deletions, Is.EqualTo 137UL)
        let empty = JjParse.parseDiffStat ""
        Assert.That(empty.FilesChanged, Is.EqualTo 0UL)

    [<Test>]
    member _.JjVersionParsesRealWorldShapes() =
        let v1 = JjParse.parseJjVersion "jj 0.38.0"
        Assert.That(v1.IsSome)
        Assert.That(v1.Value.Major, Is.EqualTo 0UL)
        Assert.That(v1.Value.Minor, Is.EqualTo 38UL)
        let v2 = JjParse.parseJjVersion "jj 0.39.0-dev+abc123"
        Assert.That(v2.Value.Minor, Is.EqualTo 39UL)
        Assert.That(JjParse.parseJjVersion("jj").IsNone)

    [<Test>]
    member _.ExpandRenameIsIdentityWithoutBraces() =
        Assert.That(JjParse.expandRename "src/plain.rs", Is.EqualTo(("src/plain.rs", "src/plain.rs")))

    [<Test>]
    member _.FilesetQuotesMetacharacters() =
        Assert.That(JjFileset.Path("src/a(b).rs").Value, Is.EqualTo "file:\"src/a(b).rs\"")
        // A Windows backslash separator is normalised to `/`.
        Assert.That(JjFileset.Path($"src{string (char 92)}a.rs").Value, Is.EqualTo "file:\"src/a.rs\"")
        // A literal quote is escaped for the `file:"…"` string literal.
        Assert.That(JjFileset.Path("a\"b").Value, Is.EqualTo "file:\"a\\\"b\"")

// ---------------------------------------------------------------------------
// Client: hermetic argv-building + parsing via ScriptedRunner
// ---------------------------------------------------------------------------

[<TestFixture>]
type ClientTests() =

    [<Test>]
    member _.RepoScopedCommandsForceColorNever() : Task =
        task {
            // The rule requires --color and never present; if cmdIn stopped forcing
            // color off, no rule would match and the runner would raise.
            let jj =
                scripted [ "status"; "--color"; "never" ] (Reply.Ok "Working copy changes:\n")

            match! jj.StatusText "." with
            | Ok text -> Assert.That(text.Contains "Working copy changes")
            | Error e -> Assert.Fail $"status_text failed: {e}"
        }

    [<Test>]
    member _.StatusParsesDiffSummary() : Task =
        task {
            let jj = scripted [ "diff"; "-r"; "@"; "--summary" ] (Reply.Ok "M a.rs\nA b.rs\n")

            match! jj.Status "." with
            | Ok entries ->
                Assert.That(entries.Length, Is.EqualTo 2)
                Assert.That(entries.[0].Status = 'M')
                Assert.That(entries.[1].Path, Is.EqualTo "b.rs")
            | Error e -> Assert.Fail $"status failed: {e}"
        }

    [<Test>]
    member _.CurrentChangeParsesScriptedOutput() : Task =
        task {
            let jj =
                scripted [ "log" ] (Reply.Ok $"kztuxlro{tab}38e00654{tab}false{tab}hello jj\n")

            match! jj.CurrentChange "." with
            | Ok change ->
                Assert.That(change.ChangeId, Is.EqualTo "kztuxlro")
                Assert.That(change.Empty, Is.False)
                Assert.That(change.Description, Is.EqualTo "hello jj")
            | Error e -> Assert.Fail $"current_change failed: {e}"
        }

    [<Test>]
    member _.BookmarksParsesRows() : Task =
        task {
            let jj =
                scripted [ "bookmark"; "list" ] (Reply.Ok $"main{tab}abc123\nfeature{tab}def456\n")

            match! jj.Bookmarks "." with
            | Ok marks ->
                Assert.That(marks.Length, Is.EqualTo 2)
                Assert.That(marks.[0].Name, Is.EqualTo "main")
                Assert.That(marks.[0].Target, Is.EqualTo "abc123")
            | Error e -> Assert.Fail $"bookmarks failed: {e}"
        }

    [<Test>]
    member _.BookmarksAllParsesLocalAndRemote() : Task =
        task {
            let jj =
                scripted
                    [ "bookmark"; "list"; "-a" ]
                    (Reply.Ok $"main{tab}{tab}0{tab}abc123\nmain{tab}origin{tab}1{tab}abc123\n")

            match! jj.BookmarksAll "." with
            | Ok refs ->
                Assert.That(refs.Length, Is.EqualTo 2)
                Assert.That(refs.[0].Remote, Is.EqualTo None)
                Assert.That(refs.[1].Remote, Is.EqualTo(Some "origin"))
                Assert.That(refs.[1].Tracked, Is.True)
            | Error e -> Assert.Fail $"bookmarks_all failed: {e}"
        }

    [<Test>]
    member _.WorkspaceListParsesRows() : Task =
        task {
            let jj =
                scripted [ "workspace"; "list" ] (Reply.Ok $"default{tab}e2aa3420{tab}main\nws1{tab}12345678{tab}\n")

            match! jj.WorkspaceList "." with
            | Ok got ->
                Assert.That(got.Length, Is.EqualTo 2)
                Assert.That(got.[0].Name, Is.EqualTo "default")
                Assert.That(got.[0].Bookmarks.[0], Is.EqualTo "main")
                Assert.That(got.[1].Bookmarks.Length, Is.EqualTo 0)
            | Error e -> Assert.Fail $"workspace_list failed: {e}"
        }

    [<Test>]
    member _.BookmarkMoveAppendsAllowBackwards() : Task =
        task {
            // No fallback: the rule only matches if --allow-backwards is built. The name is
            // wrapped in `exact:` (a `*` would otherwise move every bookmark).
            let jj =
                scripted [ "bookmark"; "move"; "exact:main"; "--to"; "@"; "--allow-backwards" ] (Reply.Ok "")

            match! jj.BookmarkMove(".", "main", "@", true) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"bookmark_move failed: {e}"
        }

    [<Test>]
    member _.DestructiveOpsWrapNamesInExact() : Task =
        task {
            // jj glob-matches `<NAMES>`/`--remote`/`-b`, so a `*` would fan the op across every
            // matching ref (`bookmark delete '*'` deletes them all). Each typed method wraps its
            // name in `exact:` to force a literal, one-ref match. Assert the exact wire arg.
            let del = scripted [ "bookmark"; "delete"; "exact:main" ] (Reply.Ok "")

            match! del.BookmarkDelete(".", "main") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"bookmark_delete failed: {e}"

            let fetchFrom =
                scripted [ "git"; "fetch"; "--remote"; "exact:upstream" ] (Reply.Ok "")

            match! fetchFrom.GitFetchFrom(".", "upstream") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"git_fetch_from failed: {e}"

            let fetchBranch =
                scripted [ "git"; "fetch"; "--remote"; "origin"; "-b"; "exact:feat" ] (Reply.Ok "")

            match! fetchBranch.GitFetchBranch(".", "feat") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"git_fetch_branch failed: {e}"
        }

    [<Test>]
    member _.NewMergeAppendsParents() : Task =
        task {
            let jj = scripted [ "new"; "-m"; "m"; "p1"; "p2" ] (Reply.Ok "")

            match! jj.NewMerge(".", "m", [ "p1"; "p2" ]) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"new_merge failed: {e}"
        }

    [<Test>]
    member _.IsConflictedReadsTemplateFlag() : Task =
        task {
            let yes = scripted [ "log" ] (Reply.Ok "1\n")

            match! yes.IsConflicted(".", "@") with
            | Ok v -> Assert.That(v, Is.True)
            | Error e -> Assert.Fail $"is_conflicted failed: {e}"

            let no = scripted [ "log" ] (Reply.Ok "0\n")

            match! no.IsConflicted(".", "@") with
            | Ok v -> Assert.That(v, Is.False)
            | Error e -> Assert.Fail $"is_conflicted failed: {e}"
        }

    [<Test>]
    member _.CommitCountCountsTemplateLines() : Task =
        task {
            let jj = scripted [ "log" ] (Reply.Ok "a\nb\nc\n")

            match! jj.CommitCount(".", "::@") with
            | Ok n -> Assert.That(n, Is.EqualTo 3)
            | Error e -> Assert.Fail $"commit_count failed: {e}"
        }

    [<Test>]
    member _.CommitPathsBuildsFilesets() : Task =
        task {
            let jj =
                scripted [ "commit"; "-m"; "msg"; "file:\"x|y.rs\""; "file:\"z.rs\"" ] (Reply.Ok "")

            match! jj.CommitPaths(".", [ JjFileset.Path "x|y.rs"; JjFileset.Path "z.rs" ], "msg") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"commit_paths failed: {e}"
        }

    [<Test>]
    member _.SquashPathsBuildsArgs() : Task =
        task {
            let jj =
                scripted [ "squash"; "--from"; "@"; "--into"; "feat"; "file:\"a.rs\"" ] (Reply.Ok "")

            let spec = SquashPaths.Create("@", "feat").WithFilesets [ JjFileset.Path "a.rs" ]

            match! jj.SquashPaths(".", spec) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"squash_paths failed: {e}"
        }

    [<Test>]
    member _.SquashPathsKeepsDestinationMessage() : Task =
        task {
            let jj =
                scripted [ "squash"; "--use-destination-message"; "file:\"a.rs\"" ] (Reply.Ok "")

            let spec =
                SquashPaths.Create("@", "feat").WithFilesets([ JjFileset.Path "a.rs" ]).WithUseDestinationMessage()

            match! jj.SquashPaths(".", spec) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"squash_paths failed: {e}"
        }

    [<Test>]
    member _.SparseSetClearsThenAdds() : Task =
        task {
            let jj =
                scripted [ "sparse"; "set"; "--clear"; "--add"; "README.md"; "lib" ] (Reply.Ok "")

            match! jj.SparseSet(".", [ "README.md"; "lib" ]) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"sparse_set failed: {e}"
        }

    [<Test>]
    member _.GitPushWithAndWithoutBookmark() : Task =
        task {
            // `-b` is glob-matched by jj, so the bookmark is wrapped in `exact:` to push only it.
            let withBm = scripted [ "git"; "push"; "-b"; "exact:feature" ] (Reply.Ok "")

            match! withBm.GitPush(".", Some "feature") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"git_push -b failed: {e}"

            let bare = scripted [ "git"; "push" ] (Reply.Ok "")

            match! bare.GitPush(".", None) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"bare git_push failed: {e}"
        }

    [<Test>]
    member _.GitCloneBuildsDirlessArgs() : Task =
        task {
            let jj =
                scripted [ "git"; "clone"; "https://x/r.git"; "/dest"; "--colocate" ] (Reply.Ok "")

            match! jj.GitClone("https://x/r.git", "/dest", true) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"git_clone failed: {e}"

            let plain = scripted [ "git"; "clone"; "u"; "/d"; "--no-colocate" ] (Reply.Ok "")

            match! plain.GitClone("u", "/d", false) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"git_clone --no-colocate failed: {e}"
        }

    [<Test>]
    member _.AbsorbBuildsArgs() : Task =
        task {
            let jj = scripted [ "absorb"; "--from"; "@-"; "file:\"src/a.rs\"" ] (Reply.Ok "")

            match! jj.Absorb(".", Some "@-", [ JjFileset.Path "src/a.rs" ]) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"absorb failed: {e}"
        }

    [<Test>]
    member _.SplitPathsRefusesEmptyFilesetsWithoutSpawning() : Task =
        task {
            // Permissive runner: a leak would return Ok. A refusal must return Error.
            let jj = permissive ()

            match! jj.SplitPaths(".", [], "msg") with
            | Error _ -> ()
            | Ok() -> Assert.Fail "empty filesets must be refused before spawning"
        }

    [<Test>]
    member _.FileAnnotateAndShowBuildArgs() : Task =
        task {
            let annotate =
                scripted
                    [ "file"; "annotate"; "-r"; "@-"; "--"; "src/a.rs" ]
                    (Reply.Ok $"kz{tab}line one\nkz{tab}line two")

            match! annotate.FileAnnotate(".", "src/a.rs", Some "@-") with
            | Ok lines ->
                Assert.That(lines.Length, Is.EqualTo 2)
                Assert.That(lines.[0].ChangeId, Is.EqualTo "kz")
                Assert.That(lines.[1].Line, Is.EqualTo 2)
            | Error e -> Assert.Fail $"file_annotate failed: {e}"

            // file_show wraps the path as an exact-path fileset; the blob's trailing newline is
            // PRESERVED (untrimmed) so a read-modify-write stays byte-exact.
            let show =
                scripted [ "file"; "show"; "-r"; "@-"; "file:\"src/a.rs\"" ] (Reply.Ok "content\n")

            match! show.FileShow(".", "@-", "src/a.rs") with
            | Ok content -> Assert.That(content, Is.EqualTo "content\n")
            | Error e -> Assert.Fail $"file_show failed: {e}"
        }

    [<Test>]
    member _.FileAnnotatePreservesCrThroughByteCapture() : Task =
        task {
            // A CRLF-terminated source line: the `\r` in each annotate row's content must survive
            // end-to-end. FileAnnotate captures raw bytes, not the line-normalizing string verb —
            // which strips every `\r` and would silently corrupt a CRLF file's annotation.
            let annotate =
                scripted
                    [ "file"; "annotate"; "-r"; "@-"; "--"; "src/a.rs" ]
                    (Reply.Ok $"kz{tab}fn main() {{{cr}\nkz{tab}}}{cr}\n")

            match! annotate.FileAnnotate(".", "src/a.rs", Some "@-") with
            | Ok lines ->
                Assert.That(lines.Length, Is.EqualTo 2)
                Assert.That(lines.[0].Content, Is.EqualTo $"fn main() {{{cr}", "the CRLF `\\r` survives byte capture")
            | Error e -> Assert.Fail $"file_annotate failed: {e}"
        }

    [<Test>]
    member _.DescriptionBuildsSingleCommitQuery() : Task =
        task {
            let jj =
                scripted
                    [ "log"; "-r"; "abc123"; "--limit"; "1"; "-T"; "description" ]
                    (Reply.Ok "feat: parser\n\nbody\n")

            match! jj.Description(".", "abc123") with
            | Ok text -> Assert.That(text, Is.EqualTo "feat: parser\n\nbody")
            | Error e -> Assert.Fail $"description failed: {e}"
        }

    [<Test>]
    member _.DiffTextBuildsWorkingCopyArgs() : Task =
        task {
            let jj = scripted [ "diff"; "-r"; "@"; "--git" ] (Reply.Ok "")

            match! jj.DiffText(".", DiffSpec.WorkingTree) with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"diff_text failed: {e}"
        }

    [<Test>]
    member _.DiffParsesScriptedOutput() : Task =
        task {
            let out = "diff --git a/m b/m\n--- a/m\n+++ b/m\n@@ -1 +1 @@\n-a\n+b\n"
            let jj = scripted [ "diff" ] (Reply.Ok out)

            match! jj.Diff(".", DiffSpec.Rev "@-") with
            | Ok files ->
                Assert.That(files.Length, Is.EqualTo 1)
                Assert.That(files.[0].Path, Is.EqualTo "m")
                Assert.That(files.[0].Change, Is.EqualTo ChangeKind.Modified)
            | Error e -> Assert.Fail $"diff failed: {e}"
        }

    [<Test>]
    member _.BookmarkTrackBuildsNameAtRemote() : Task =
        task {
            // The whole `name@remote` token is `exact:`-prefixed (a `*` name would otherwise
            // track every remote bookmark).
            let jj = scripted [ "bookmark"; "track"; "exact:feat@origin" ] (Reply.Ok "")

            match! jj.BookmarkTrack(".", "feat", "origin") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"bookmark_track failed: {e}"
        }

    [<Test>]
    member _.BookmarkTrackRejectsGlobLikeRemote() : Task =
        task {
            // The positional `exact:<name>@<remote>` target `exact:`-prefixes the whole token,
            // but jj still splits on `@` and glob-matches the remote segment positionally — a
            // glob metacharacter in `remote` must be refused before spawning, not passed through.
            let jj = permissive ()

            for badRemote in [ "up*"; "up?stream"; "[up]stream"; "upstream]" ] do
                match! jj.BookmarkTrack(".", "feat", badRemote) with
                | Ok() -> Assert.Fail $"expected bookmark_track to reject glob-like remote \"{badRemote}\""
                | Error(ProcessError.Spawn _) -> ()
                | Error e -> Assert.Fail $"expected a Spawn error for remote \"{badRemote}\", got: {e}"
        }

    [<Test>]
    member _.WorkspaceAddBuildsNameBasePath() : Task =
        task {
            let jj =
                scripted [ "workspace"; "add"; "--name"; "ws1"; "-r"; "main"; "/wt" ] (Reply.Ok "")

            match! jj.WorkspaceAdd(".", WorkspaceAdd.Create("ws1", "main", "/wt")) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"workspace_add failed: {e}"
        }

    [<Test>]
    member _.WorkspaceAddWithSparseMode() : Task =
        task {
            // The --sparse-patterns value must land between -r <base> and the path.
            let jj =
                scripted [ "workspace"; "add"; "--sparse-patterns"; "empty"; "/wt" ] (Reply.Ok "")

            let spec = WorkspaceAdd.Create("ws1", "main", "/wt").WithSparse(SparseMode.Empty)

            match! jj.WorkspaceAdd(".", spec) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"workspace_add sparse failed: {e}"
        }

    [<Test>]
    member _.WorkspaceRootsBatchesPerNameAndMapsErrors() : Task =
        task {
            // One `workspace root --name <n>` per name; a non-zero exit maps to Error
            // for that slot, and results come back in input order regardless of which
            // command the runner happened to answer first.
            let runner =
                ScriptedRunner()
                    .On([ "workspace"; "root"; "--name"; "default" ], Reply.Ok "/repo\n")
                    .On([ "workspace"; "root"; "--name"; "ws1" ], Reply.Ok "/repo/ws1\n")
                    .On([ "workspace"; "root"; "--name"; "gone" ], Reply.Fail(1, "Error: No such workspace"))

            let jj = Jj.WithRunner runner
            let! roots = jj.WorkspaceRoots(".", [ "default"; "gone"; "ws1" ])
            Assert.That(roots.Length, Is.EqualTo 3)

            match roots.[0] with
            | Ok p -> Assert.That(p, Is.EqualTo "/repo")
            | Error e -> Assert.Fail $"default root failed: {e}"

            Assert.That(Result.isError roots.[1], Is.True, "a non-zero `workspace root` is Error")

            match roots.[2] with
            | Ok p -> Assert.That(p, Is.EqualTo "/repo/ws1")
            | Error e -> Assert.Fail $"ws1 root failed: {e}"
        }

    [<Test>]
    member _.OpLogParsesRows() : Task =
        task {
            let jj =
                scripted [ "op"; "log" ] (Reply.Ok $"abc{tab}u@h{tab}2026-06-05T10:00:00+02:00{tab}new empty commit\n")

            match! jj.OpLog(".", 5) with
            | Ok ops ->
                Assert.That(ops.Length, Is.EqualTo 1)
                Assert.That(ops.[0].Id, Is.EqualTo "abc")
                Assert.That(ops.[0].Description, Is.EqualTo "new empty commit")
            | Error e -> Assert.Fail $"op_log failed: {e}"
        }

    [<Test>]
    member _.CurrentBookmarkTakesFirstOrNone() : Task =
        task {
            let some = scripted [ "log" ] (Reply.Ok "main\n")

            match! some.CurrentBookmark "." with
            | Ok v -> Assert.That(v, Is.EqualTo(Some "main"))
            | Error e -> Assert.Fail $"current_bookmark failed: {e}"

            let none = scripted [ "log" ] (Reply.Ok "\n")

            match! none.CurrentBookmark "." with
            | Ok v -> Assert.That(v, Is.EqualTo None)
            | Error e -> Assert.Fail $"current_bookmark failed: {e}"
        }

// ---------------------------------------------------------------------------
// resolve_list, capabilities, transactions, and the injection guard
// ---------------------------------------------------------------------------

[<TestFixture>]
type SemanticsTests() =

    [<Test>]
    member _.ResolveListDistinguishesNoConflictsFromErrors() : Task =
        task {
            // Benign "no conflicts" non-zero exit → empty list.
            let none =
                scripted [ "resolve" ] (Reply.Fail(2, "Error: No conflicts found at this revision"))

            match! none.ResolveList(".", "@") with
            | Ok xs -> Assert.That(xs, Is.Empty)
            | Error e -> Assert.Fail $"resolve_list (none) failed: {e}"

            // A real failure must surface, not read as "no conflicts".
            let bad =
                scripted [ "resolve" ] (Reply.Fail(1, "Error: Revision `bogus` doesn't exist"))

            let! r2 = bad.ResolveList(".", "bogus")
            Assert.That(Result.isError r2, Is.True)
            // Success with conflicts → parsed paths.
            let some = scripted [ "resolve" ] (Reply.Ok "a.rs    2-sided conflict\n")

            match! some.ResolveList(".", "@") with
            | Ok xs ->
                Assert.That(xs.Length, Is.EqualTo 1)
                Assert.That(xs.[0], Is.EqualTo "a.rs")
            | Error e -> Assert.Fail $"resolve_list (some) failed: {e}"
        }

    [<Test>]
    member _.CapabilitiesParseAndGate() : Task =
        task {
            let jj = scripted [ "--version" ] (Reply.Ok "jj 0.38.0\n")

            match! jj.Capabilities() with
            | Ok caps ->
                Assert.That(caps.IsSupported, Is.True)
                Assert.That(Result.isOk (caps.EnsureSupported()), Is.True)
            | Error e -> Assert.Fail $"capabilities failed: {e}"

            // A dev-build suffix parses; an older release fails the precise gate.
            let dev = scripted [ "--version" ] (Reply.Ok "jj 0.39.0-dev+abc123\n")
            let! devCaps = dev.Capabilities()

            Assert.That(
                (match devCaps with
                 | Ok c -> c.IsSupported
                 | Error _ -> false),
                Is.True
            )

            let old = scripted [ "--version" ] (Reply.Ok "jj 0.35.0\n")

            match! old.Capabilities() with
            | Ok caps ->
                Assert.That(caps.IsSupported, Is.False)
                Assert.That(Result.isError (caps.EnsureSupported()), Is.True)
            | Error e -> Assert.Fail $"capabilities failed: {e}"

            // An unrecognisable version string is a parse error.
            let garbage = scripted [ "--version" ] (Reply.Ok "nope")
            let! g = garbage.Capabilities()
            Assert.That(Result.isError g, Is.True)
        }

    [<Test>]
    member _.TransactionRestoresOpHeadOnError() : Task =
        task {
            // op log returns the captured id; the mutation fails; the restore must
            // run with that exact id (the `abc123` token gates the restore rule).
            let runner =
                ScriptedRunner()
                    .On([ "op"; "log" ], Reply.Ok "abc123\n")
                    .On([ "op"; "restore"; "abc123" ], Reply.Ok "")
                    .On([ "describe" ], Reply.Fail(1, "boom"))

            let jj = Jj.WithRunner runner

            let! res = jj.Transaction("/r", (fun tx -> tx.Describe("/r", "wip")))
            Assert.That(Result.isError res, Is.True, "the closure error must surface")
        }

    [<Test>]
    member _.TransactionKeepsChangesOnSuccess() : Task =
        task {
            // No `op restore` rule scripted — if the transaction restored on success,
            // the unscripted restore command would raise. Success must NOT restore.
            let runner =
                ScriptedRunner().On([ "op"; "log" ], Reply.Ok "abc123\n").On([ "describe" ], Reply.Ok "")

            let jj = Jj.WithRunner runner

            match! jj.Transaction("/r", (fun tx -> tx.Describe("/r", "wip"))) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"transaction should succeed: {e}"
        }

    [<Test>]
    member _.FlagLikePositionalsAreRejectedBeforeSpawning() : Task =
        task {
            let jj = permissive ()

            let isErr (t: Task<Result<unit, ProcessError>>) =
                task {
                    let! r = t
                    return Result.isError r
                }

            let! a = isErr (jj.BookmarkCreate(".", "-evil", "@"))
            let! b = isErr (jj.BookmarkRename(".", "ok", "-bad"))
            let! c = isErr (jj.BookmarkDelete(".", "--all"))
            let! d = isErr (jj.BookmarkMove(".", "-evil", "@", false))
            let! e = isErr (jj.Edit(".", "-evil"))
            let! f = isErr (jj.Duplicate(".", "-r"))
            let! g = isErr (jj.Abandon(".", "-evil"))
            let! h = isErr (jj.BookmarkTrack(".", "--config=x", "origin"))
            let! i = isErr (jj.BookmarkSet(".", "-evil", "@"))
            let! j = isErr (jj.OpRestore(".", "--help"))
            let! k = isErr (jj.WorkspaceForget(".", "-evil"))
            let! l = isErr (jj.NewMerge(".", "m", [ "@"; "--ignore-working-copy" ]))
            let! m = isErr (jj.GitClone("-evil", "/d", false))
            let! n = isErr (jj.Edit(".", ""))

            for flag, name in
                [ a, "bookmark_create"
                  b, "bookmark_rename"
                  c, "bookmark_delete"
                  d, "bookmark_move"
                  e, "edit"
                  f, "duplicate"
                  g, "abandon"
                  h, "bookmark_track"
                  i, "bookmark_set"
                  j, "op_restore"
                  k, "workspace_forget"
                  l, "new_merge parent"
                  m, "git_clone"
                  n, "empty edit" ] do
                Assert.That(flag, Is.True, $"{name} must be refused")

            // …and a legitimate value still passes through.
            match! jj.Edit(".", "abc123") with
            | Ok() -> ()
            | Error err -> Assert.Fail $"a valid revset must pass: {err}"
        }

    [<Test>]
    member _.RevsetExprValidates() =
        Assert.That(Result.isOk (RevsetExpr.Create "heads(::@ & bookmarks())"), Is.True)

        Assert.That(
            (RevsetExpr.Create "@-"
             |> function
                 | Ok r -> r.Value
                 | Error _ -> ""),
            Is.EqualTo "@-"
        )

        Assert.That(Result.isError (RevsetExpr.Create "-evil"), Is.True)
        Assert.That(Result.isError (RevsetExpr.Create ""), Is.True)

[<TestFixture>]
type AtViewTests() =

    [<Test>]
    member _.JjAtBindsDirWithByteIdenticalArgv() : Task =
        task {
            // A modelled method through the `at(dir)` view produces byte-identical argv to the
            // dir-taking form (incl. the forced `--color never`) and binds `dir` as the cwd.
            let captured, runner = capturing (Reply.Ok "")
            let jj = Jj.WithRunner runner

            let! _ = jj.At("/bound/dir").Status()

            match captured.Value with
            | Some cmd ->
                Assert.That(cmd.WorkingDirectory, Is.EqualTo(Some "/bound/dir"), "the view binds dir as cwd")
                Assert.That(String.concat " " cmd.Arguments, Is.EqualTo "diff -r @ --summary --color never")
            | None -> Assert.Fail "no command captured"
        }

    [<Test>]
    member _.JjAtRawRunStaysProcessCwd() : Task =
        task {
            // The raw `Run` hatch runs in the PROCESS cwd (WorkingDirectory = None), NOT the bound
            // dir — the deliberate `bare`-forwarder asymmetry.
            let captured, runner = capturing (Reply.Ok "ok\n")
            let jj = Jj.WithRunner runner

            let! _ = jj.At("/bound/dir").Run [ "root" ]

            match captured.Value with
            | Some cmd -> Assert.That(cmd.WorkingDirectory, Is.EqualTo None, "the raw Run hatch is NOT bound to dir")
            | None -> Assert.Fail "no command captured"
        }
