module VcsToolkit.Jj.Tests

open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.CliSupport
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

/// A runner that records **every** command it runs (in call order) and replies `reply` to
/// all of them — for asserting the exact argv a read/mutation builds, including where the
/// read-only `--ignore-working-copy` flag lands relative to the subcommand.
let private recording (reply: Reply) : ResizeArray<Command> * ScriptedRunner =
    let calls = ResizeArray<Command>()

    let runner =
        ScriptedRunner()
            .When(
                (fun (cmd: Command) ->
                    calls.Add cmd
                    true),
                reply
            )

    calls, runner

/// The argv (program excluded), in order, of a captured command.
let private argsOf (cmd: Command) = cmd.Arguments |> Seq.toList

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
        let author = $"{tab}\"Ada\"{tab}2026-05-31T10:00:00+00:00{tab}"

        let got =
            JjParse.parseAnnotate $"kxoyzabc{author}fn main() {{\nkxoyzabc{author}}}\nqlmnopqr{author}// added later"

        Assert.That(got.Length, Is.EqualTo 3)
        Assert.That(got.[0].ChangeId, Is.EqualTo "kxoyzabc")
        Assert.That(got.[0].Author, Is.EqualTo "Ada", "the escape_json-framed author name is decoded")
        Assert.That(got.[0].Time, Is.EqualTo "2026-05-31T10:00:00+00:00")
        Assert.That(got.[0].Line, Is.EqualTo 1)
        Assert.That(got.[0].Content, Is.EqualTo "fn main() {")
        Assert.That(got.[2].Line, Is.EqualTo 3)
        Assert.That(JjParse.parseAnnotate("").Length, Is.EqualTo 0)

    [<Test>]
    member _.AnnotatePreservesCrAndIgnoresTrailingNewline() =
        let author = $"{tab}\"Ada\"{tab}2026-05-31T10:00:00+00:00{tab}"

        let got =
            JjParse.parseAnnotate $"kxoyzabc{author}fn main() {{{cr}\nkxoyzabc{author}}}{cr}\n"

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
        // Input mirrors `CONFLICTED_PATHS_TEMPLATE`'s output: one `.escape_json()`-framed
        // path per line.
        let got = JjParse.parseResolveList "\"src/a.rs\"\n\"b.txt\"\n"

        Assert.That(got.Length, Is.EqualTo 2)
        Assert.That(got.[0], Is.EqualTo "src/a.rs")
        Assert.That(got.[1], Is.EqualTo "b.txt")
        Assert.That(JjParse.parseResolveList("").Length, Is.EqualTo 0)
        // OS-native backslash separators (Windows, from `path.display()`) are normalised to
        // `/`; `.escape_json()` renders a literal backslash doubled (`\\`), which
        // `decodeJsonField` unescapes to one backslash before normalisation.
        let win =
            JjParse.parseResolveList $"\"sub{string (char 92)}{string (char 92)}c.txt\"\n"

        Assert.That(win.[0], Is.EqualTo "sub/c.txt")

    [<Test>]
    member _.ResolveListPreservesInternalRunsOfSpacesInPaths() =
        // The `jj resolve --list` human-readable format cannot reliably distinguish an
        // internal run of spaces in a path from its dynamically sized column padding
        // (investigated on jj 0.42.0 — see `parseResolveList`'s doc comment), so
        // `parseResolveList` now reads `jj file list -T CONFLICTED_PATHS_TEMPLATE`'s
        // JSON-framed output instead, which has no such ambiguity.
        let got = JjParse.parseResolveList "\"src/has  two   spaces.txt\"\n"

        Assert.That(got.Length, Is.EqualTo 1)
        Assert.That(got.[0], Is.EqualTo "src/has  two   spaces.txt")

    [<Test>]
    member _.WorkspacesSplitTabFieldsAndBookmarks() =
        // Bookmarks are the framing contract's space-joined list (a raw name decodes to
        // itself); no bookmarks → an empty list.
        let got =
            JjParse.parseWorkspaces $"default{tab}e2aa3420{tab}main feature\nws1{tab}12345678{tab}\n"

        Assert.That(got.Length, Is.EqualTo 2)
        Assert.That(got.[0].Name, Is.EqualTo "default")
        Assert.That(got.[0].Commit, Is.EqualTo "e2aa3420")
        Assert.That(got.[0].Bookmarks.Length, Is.EqualTo 2)
        Assert.That(got.[1].Bookmarks.Length, Is.EqualTo 0, "no bookmarks → empty list, not [\"\"]")

    [<Test>]
    member _.WorkspacesRoundTripExoticNames() =
        // A workspace name holding a REAL tab is `.escape_json()`-framed as `"ta\tb"`
        // (backslash-t, not a literal tab), so the row still splits on the two column
        // separators and the interior tab is DECODED, not split on. A comma/slash in a
        // bookmark name likewise survives the space-joined escaped list (T-014).
        let field0 = "\"ta\\tb\"" // escape_json of  ta<TAB>b
        let field2 = "\"co,mma\" \"pl/ain\"" // two escaped bookmark names, space-joined
        let got = JjParse.parseWorkspaces $"{field0}{tab}c0ffee{tab}{field2}\n"
        Assert.That(got.Length, Is.EqualTo 1)
        Assert.That(got.[0].Name, Is.EqualTo $"ta{tab}b", "the interior tab is decoded, not split on")
        Assert.That(got.[0].Commit, Is.EqualTo "c0ffee")
        Assert.That(got.[0].Bookmarks.Length, Is.EqualTo 2)
        Assert.That(got.[0].Bookmarks.[0], Is.EqualTo "co,mma", "a comma in a bookmark name survives")
        Assert.That(got.[0].Bookmarks.[1], Is.EqualTo "pl/ain")

    [<Test>]
    member _.BookmarksRoundTripSpecialCharsInName() =
        // A git-imported bookmark name can carry a comma/tab/quote; `.escape_json()`
        // framing means the row still splits on the real tab separator and
        // `decodeJsonField` restores the exact name — free text no longer breaks the
        // field split (T-014).
        let name = "\"we,ird\\tname\"" // escape_json of  we,ird<TAB>name
        let got = JjParse.parseBookmarks $"{name}{tab}f5d07685\n"
        Assert.That(got.Length, Is.EqualTo 1)
        Assert.That(got.[0].Name, Is.EqualTo $"we,ird{tab}name")
        Assert.That(got.[0].Target, Is.EqualTo "f5d07685")

    [<Test>]
    member _.BookmarksTruncatedUnicodeEscapeStopsDecoding() =
        // A `\u` escape with zero hex digits must not fall back to a literal NUL char
        // (code 0) — decoding stops instead, per decodeJsonField's own doc-comment
        // ("a truncated or malformed escape simply stops decoding").
        let got = JjParse.parseBookmarks $"\"no\\u\"{tab}f5d07685\n"
        Assert.That(got.Length, Is.EqualTo 1)
        Assert.That(got.[0].Name, Is.EqualTo "no", "decoding stops at the truncated escape, no NUL char")
        Assert.That(got.[0].Name, Does.Not.Contain('\000'))

    [<Test>]
    member _.BookmarksPartiallyTruncatedUnicodeEscapeStopsDecoding() =
        // Same contract for a partially-truncated escape (1-3 valid hex digits followed
        // by end of string): still a truncated escape, still stops decoding rather than
        // building a partial code point.
        let got = JjParse.parseBookmarks $"\"no\\u12\"{tab}f5d07685\n"
        Assert.That(got.Length, Is.EqualTo 1)
        Assert.That(got.[0].Name, Is.EqualTo "no", "decoding stops at the partially truncated escape")

    [<Test>]
    member _.ReachableBookmarksDecodeEscapedNames() =
        // Space-joined escaped names: a comma in a name survives (a name never holds a
        // space, so the join stays reversible), and each token decodes on its own.
        let got = JjParse.parseReachableBookmarks $"\"co,mma\" \"main\"{tab}abc123\n"

        Assert.That(got.Length, Is.EqualTo 2)
        Assert.That(got.[0].Name, Is.EqualTo "co,mma")
        Assert.That(got.[1].Name, Is.EqualTo "main")
        Assert.That(got.[0].Target, Is.EqualTo "abc123")

    [<Test>]
    member _.FullIdsDisambiguateSharedShortPrefix() =
        // Identity targets carry the FULL commit id, so two bookmarks sharing a 16-char
        // short prefix stay distinct — a `.short()` key would collide and cross-reference
        // wrongly (T-014).
        let a = "abcdef0123456789abcdef0123456789abcdef01"
        let b = "abcdef0123456789ffffffffffffffffffffffff" // same 16-char prefix
        let bms = JjParse.parseBookmarks $"\"one\"{tab}{a}\n\"two\"{tab}{b}\n"
        Assert.That(bms.[0].Target, Is.EqualTo a)
        Assert.That(bms.[1].Target, Is.EqualTo b)
        Assert.That(bms.[0].Target, Is.Not.EqualTo(bms.[1].Target), "full ids must not collide")
        // The same holds for the workspace commit (the WorktreeInfo.Commit source).
        let ws = JjParse.parseWorkspaces $"\"w1\"{tab}{a}{tab}\n\"w2\"{tab}{b}{tab}\n"
        Assert.That(ws.[0].Commit, Is.Not.EqualTo(ws.[1].Commit))

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
        Assert.That(JjFileset.Path("src/a(b).rs").Value, Is.EqualTo "root-file:\"src/a(b).rs\"")
        // A literal quote is escaped for the `root-file:"…"` string literal, on both platforms.
        Assert.That(JjFileset.Path("a\"b.txt").Value, Is.EqualTo "root-file:\"a\\\"b.txt\"")

        if System.OperatingSystem.IsWindows() then
            // On Windows a backslash separator is normalised to `/`.
            Assert.That(JjFileset.Path($"src{string (char 92)}a.rs").Value, Is.EqualTo "root-file:\"src/a.rs\"")
        else
            // On non-Windows `\` is a legitimate filename byte: it is kept and
            // escaped as `\\` (not normalised to `/`), so the literal round-trips.
            let bs = string (char 92)
            Assert.That(JjFileset.Path($"a{bs}b.txt").Value, Is.EqualTo $"root-file:\"a{bs}{bs}b.txt\"")
            Assert.That(JjFileset.Path($"a{bs}").Value, Is.EqualTo $"root-file:\"a{bs}{bs}\"")
            Assert.That(JjFileset.Path($"a{bs}b\"c.txt").Value, Is.EqualTo $"root-file:\"a{bs}{bs}b\\\"c.txt\"")

    [<Test>]
    member _.FilesetEscapingMatchesPlatformSemanticsIndependentOfHostOs() =
        // Model both branches explicitly so the platform-independent escaping
        // logic is exercised regardless of which OS actually runs this test.
        let windowsEscape (path: string) =
            path.Replace(char 92, '/').Replace("\"", "\\\"")

        let nonWindowsEscape (path: string) =
            path.Replace("\\", "\\\\").Replace("\"", "\\\"")

        let bs = string (char 92)
        Assert.That(windowsEscape $"src{bs}a.rs", Is.EqualTo "src/a.rs")
        Assert.That(nonWindowsEscape $"a{bs}b.txt", Is.EqualTo $"a{bs}{bs}b.txt")
        Assert.That(nonWindowsEscape $"a{bs}", Is.EqualTo $"a{bs}{bs}")
        Assert.That(nonWindowsEscape $"a{bs}b\"c.txt", Is.EqualTo $"a{bs}{bs}b\\\"c.txt")
        Assert.That(windowsEscape "a\"b.txt", Is.EqualTo "a\\\"b.txt")
        Assert.That(nonWindowsEscape "a\"b.txt", Is.EqualTo "a\\\"b.txt")

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
            // `Status` resolves the workspace root first, then runs the summary query from it.
            let jj =
                Jj.WithRunner(
                    ScriptedRunner()
                        .On([ "root" ], Reply.Ok "/repo\n")
                        .On([ "diff"; "-r"; "@"; "--summary" ], Reply.Ok "M a.rs\nA b.rs\n")
                )

            match! jj.Status "." with
            | Ok entries ->
                Assert.That(entries.Length, Is.EqualTo 2)
                Assert.That(entries.[0].Status = 'M')
                Assert.That(entries.[1].Path, Is.EqualTo "b.rs")
            | Error e -> Assert.Fail $"status failed: {e}"
        }

    [<Test>]
    member _.StatusRunsFromResolvedRootRegardlessOfCallerDir() : Task =
        task {
            // The core criterion: `diff --summary` runs FROM the resolved workspace root, not
            // from whatever `dir` the caller passed — proven by capturing that command's cwd for
            // both a root-dir call and a subdirectory-dir call and finding it pinned to "/repo"
            // either way, with both calls agreeing on the same repo-relative paths.
            let captured = ref (None: Command option)

            let recordDiffCommand (cmd: Command) =
                let args = cmd.Arguments |> Seq.toList

                if List.truncate 4 args = [ "diff"; "-r"; "@"; "--summary" ] then
                    captured.Value <- Some cmd

                true

            let runner =
                ScriptedRunner().On([ "root" ], Reply.Ok "/repo\n").When(recordDiffCommand, Reply.Ok "M sub/file.rs\n")

            let jj = Jj.WithRunner runner

            let! fromRoot = jj.Status "/repo"
            let cwdFromRoot = captured.Value |> Option.bind (fun c -> c.WorkingDirectory)
            captured.Value <- None

            let! fromSub = jj.Status "/repo/sub"
            let cwdFromSub = captured.Value |> Option.bind (fun c -> c.WorkingDirectory)

            Assert.That(cwdFromRoot, Is.EqualTo(Some "/repo"), "called from the root dir: still runs from the root")

            Assert.That(
                cwdFromSub,
                Is.EqualTo(Some "/repo"),
                "called from a subdirectory dir: runs from the root, NOT the subdirectory"
            )

            match fromRoot, fromSub with
            | Ok a, Ok b -> Assert.That((a = b), Is.True, "same repo-relative paths regardless of the caller's dir")
            | _ -> Assert.Fail "expected both calls to succeed"
        }

    [<Test>]
    member _.StatusPreservesLeadingWhitespaceInWorkspaceRoot() : Task =
        task {
            // A leading space is legal in a Unix path; `Status` must run the diff query from
            // the root exactly as `Root` returned it (TrimEnd-only), not further `Trim()`-ed
            // (which would silently drop the leading space and point at a nonexistent dir).
            let captured = ref (None: Command option)

            let recordDiffCommand (cmd: Command) =
                let args = cmd.Arguments |> Seq.toList

                if List.truncate 4 args = [ "diff"; "-r"; "@"; "--summary" ] then
                    captured.Value <- Some cmd

                true

            let runner =
                ScriptedRunner().On([ "root" ], Reply.Ok " /repo\n").When(recordDiffCommand, Reply.Ok "M file.rs\n")

            let jj = Jj.WithRunner runner

            let! _ = jj.Status " /repo"
            let cwd = captured.Value |> Option.bind (fun c -> c.WorkingDirectory)

            Assert.That(cwd, Is.EqualTo(Some " /repo"), "the leading space must be preserved, not trimmed")
        }

    [<Test>]
    member _.StatusRejectsPathEscapingWorkspaceRoot() : Task =
        task {
            // Defensive backstop: a `..`-carrying path from `diff --summary` is rejected as an
            // error instead of silently propagated raw.
            let jj =
                Jj.WithRunner(
                    ScriptedRunner()
                        .On([ "root" ], Reply.Ok "/repo\n")
                        .On([ "diff"; "-r"; "@"; "--summary" ], Reply.Ok "M ../outside.rs\n")
                )

            let! result = jj.Status "/repo"
            Assert.That(Result.isError result, Is.True, "an escaping path must be rejected, not propagated raw")
        }

    [<Test>]
    member _.StatusRejectsOldPathEscapingWorkspaceRoot() : Task =
        task {
            // The rejection covers OldPath too (rename/copy), not just Path.
            let jj =
                Jj.WithRunner(
                    ScriptedRunner()
                        .On([ "root" ], Reply.Ok "/repo\n")
                        .On([ "diff"; "-r"; "@"; "--summary" ], Reply.Ok "R {../old.rs => new.rs}\n")
                )

            let! result = jj.Status "/repo"
            Assert.That(Result.isError result, Is.True, "an escaping OldPath must be rejected too")
        }

    [<Test>]
    member _.DiffSummaryRunsFromResolvedRootAndNormalisesRenameOldPath() : Task =
        task {
            // Mirrors `Status`: resolves the workspace root and runs the range query from
            // there, including a rename/copy's `OldPath`.
            let jj =
                Jj.WithRunner(
                    ScriptedRunner()
                        .On([ "root" ], Reply.Ok "/repo\n")
                        .On([ "diff"; "-r"; "(@-)..(@)"; "--summary" ], Reply.Ok "R sub/{old.rs => new.rs}\n")
                )

            match! jj.DiffSummary("/repo/sub", "@-", "@") with
            | Ok [ entry ] ->
                Assert.That(entry.Status = 'R')
                Assert.That(entry.Path, Is.EqualTo "sub/new.rs")
                Assert.That(entry.OldPath, Is.EqualTo(Some "sub/old.rs"))
            | Ok other -> Assert.Fail $"expected one entry, got {other.Length}"
            | Error e -> Assert.Fail $"diff_summary failed: {e}"
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
    member _.NewChildStartsUndescribedChildOfParent() : Task =
        task {
            // Unlike `NewMerge`/`NewChange`, `NewChild` carries no `-m`: the resulting
            // change is left undescribed, and `parent` itself is untouched.
            let jj = scripted [ "new"; "feat" ] (Reply.Ok "")

            match! jj.NewChild(".", "feat") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"new_child failed: {e}"
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
                scripted [ "commit"; "-m"; "msg"; "root-file:\"x|y.rs\""; "root-file:\"z.rs\"" ] (Reply.Ok "")

            match! jj.CommitPaths(".", [ JjFileset.Path "x|y.rs"; JjFileset.Path "z.rs" ], "msg") with
            | Ok() -> ()
            | Error e -> Assert.Fail $"commit_paths failed: {e}"
        }

    [<Test>]
    member _.CommitPathsRefusesEmptyFilesetsBeforeSpawning() : Task =
        task {
            let captured, runner = capturing (Reply.Ok "")
            let jj = Jj.WithRunner runner

            let assertRefusal result =
                match result with
                | Error(ProcessError.Spawn(program, reason)) ->
                    Assert.That(program, Is.EqualTo "jj")

                    Assert.That(
                        reason,
                        Is.EqualTo
                            "commit requires at least one fileset — an empty commit would commit the entire working copy"
                    )
                | Error e -> Assert.Fail $"expected a Spawn refusal, got {e}"
                | Ok() -> Assert.Fail "an empty fileset set must be refused before spawning"

            let! direct = jj.CommitPaths(".", [], "msg")
            assertRefusal direct

            let! bound = jj.At(".").CommitPaths([], "msg")
            assertRefusal bound

            Assert.That(captured.Value.IsNone, "the guard must refuse before any spawn")
        }

    [<Test>]
    member _.LogPathsScopesToFilesetsLiterally() : Task =
        task {
            // A glob metacharacter proves the fileset is exact-path (`root-file:"…"`), matched
            // literally rather than expanded as a fileset operator. The path-scoping filesets append
            // after the `-r <revset> -n<max> --no-graph -T <template>` prefix and drive a scoped `jj log`.
            let changeRow = $"kztuxlro{tab}38e00654{tab}false{tab}touched src\n"

            let jj =
                scripted [ "log"; "-r"; "main..@"; "-n20"; "--no-graph"; "root-file:\"src/*.rs\"" ] (Reply.Ok changeRow)

            match! jj.LogPaths(".", "main..@", 20, [ JjFileset.Path "src/*.rs" ]) with
            | Ok changes ->
                Assert.That(changes.Length, Is.EqualTo 1)
                Assert.That(changes.[0].ChangeId, Is.EqualTo "kztuxlro")
                Assert.That(changes.[0].Description, Is.EqualTo "touched src")
            | Error e -> Assert.Fail $"LogPaths failed: {e}"
        }

    [<Test>]
    member _.LogPathsRefusesEmptyFilesetsBeforeSpawning() : Task =
        task {
            // A fileset-less `jj log -r <revset>` is unrestricted history; the refusal precedes any
            // spawn, so the loud fallback is never reached.
            let jj =
                Jj.WithRunner(ScriptedRunner().Fallback(Reply.Fail(1, "must not spawn — refusal must precede it")))

            match! jj.LogPaths(".", "@", 5, []) with
            | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "jj")
            | Error e -> Assert.Fail $"expected a Spawn refusal, got {e}"
            | Ok _ -> Assert.Fail "an empty fileset set must be refused before spawning"
        }

    [<Test>]
    member _.SquashPathsBuildsArgs() : Task =
        task {
            let jj =
                scripted [ "squash"; "--from"; "@"; "--into"; "feat"; "root-file:\"a.rs\"" ] (Reply.Ok "")

            let spec = SquashPaths.Create("@", "feat").WithFilesets [ JjFileset.Path "a.rs" ]

            match! jj.SquashPaths(".", spec) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"squash_paths failed: {e}"
        }

    [<Test>]
    member _.SquashPathsKeepsDestinationMessage() : Task =
        task {
            let jj =
                scripted [ "squash"; "--use-destination-message"; "root-file:\"a.rs\"" ] (Reply.Ok "")

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
    member _.GitCloneRefusesLeadingDashDestinationBeforeSpawning() : Task =
        task {
            // `dest` is a bare positional in `jj git clone <url> <dest>`: a leading-`-` value would
            // be parsed as a flag. The refusal must precede any spawn — `captured` staying `None`
            // proves the runner was never invoked (not merely that an error code came back).
            let captured, runner = capturing (Reply.Ok "")
            let jj = Jj.WithRunner runner

            match! jj.GitClone("https://x/r.git", "--colocate=/etc", false) with
            | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "jj")
            | Error e -> Assert.Fail $"expected a Spawn refusal, got {e}"
            | Ok() -> Assert.Fail "a leading-dash destination must be refused"

            Assert.That(captured.Value.IsNone, "the guard must refuse before any spawn")
        }

    [<Test>]
    member _.AbsorbBuildsArgs() : Task =
        task {
            let jj =
                scripted [ "absorb"; "--from"; "@-"; "root-file:\"src/a.rs\"" ] (Reply.Ok "")

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
                    (Reply.Ok
                        $"kz{tab}\"Ada\"{tab}2026-05-31T10:00:00+00:00{tab}line one\nkz{tab}\"Ada\"{tab}2026-05-31T10:00:00+00:00{tab}line two")

            match! annotate.FileAnnotate(".", "src/a.rs", Some "@-") with
            | Ok lines ->
                Assert.That(lines.Length, Is.EqualTo 2)
                Assert.That(lines.[0].ChangeId, Is.EqualTo "kz")
                Assert.That(lines.[0].Author, Is.EqualTo "Ada", "the escape_json-framed author name is decoded")
                Assert.That(lines.[0].Time, Is.EqualTo "2026-05-31T10:00:00+00:00")
                Assert.That(lines.[1].Line, Is.EqualTo 2)
            | Error e -> Assert.Fail $"file_annotate failed: {e}"

            // file_show wraps the path as an exact-path fileset; the blob's trailing newline is
            // PRESERVED (untrimmed) so a read-modify-write stays byte-exact.
            let show =
                scripted [ "file"; "show"; "-r"; "@-"; "root-file:\"src/a.rs\"" ] (Reply.Ok "content\n")

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
                    (Reply.Ok
                        $"kz{tab}\"Ada\"{tab}2026-05-31T10:00:00+00:00{tab}fn main() {{{cr}\nkz{tab}\"Ada\"{tab}2026-05-31T10:00:00+00:00{tab}}}{cr}\n")

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
    member _.WorkspaceAddRefusesLeadingDashPathBeforeSpawning() : Task =
        task {
            // `spec.Path` is a bare positional (`--name`/`-r` carry Name/Base as flag values), so a
            // leading-`-` path would be parsed as a flag — refused before any spawn. `captured`
            // staying `None` proves the runner was never invoked.
            let captured, runner = capturing (Reply.Ok "")
            let jj = Jj.WithRunner runner
            let spec = WorkspaceAdd.Create("ws1", "main", "--sparse-patterns")

            match! jj.WorkspaceAdd(".", spec) with
            | Error(ProcessError.Spawn(program, _)) -> Assert.That(program, Is.EqualTo "jj")
            | Error e -> Assert.Fail $"expected a Spawn refusal, got {e}"
            | Ok() -> Assert.Fail "a leading-dash workspace path must be refused"

            Assert.That(captured.Value.IsNone, "the guard must refuse before any spawn")
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

    [<TestCase("main", "\"main\"\n")>]
    [<TestCase("main,test", "\"main,test\"\n")>]
    [<TestCase("my\"quote", "\"my\\\"quote\"\n")>]
    member _.CurrentBookmarkAndTrunkRoundTripEscapedNames(name: string, rendered: string) : Task =
        task {
            let current = scripted [ "log"; "-r"; "@" ] (Reply.Ok rendered)

            match! current.CurrentBookmark "." with
            | Ok v -> Assert.That(v, Is.EqualTo(Some name))
            | Error e -> Assert.Fail $"current_bookmark failed: {e}"

            let trunk = scripted [ "log"; "-r"; "trunk()" ] (Reply.Ok rendered)

            match! trunk.Trunk "." with
            | Ok v -> Assert.That(v, Is.EqualTo(Some name))
            | Error e -> Assert.Fail $"trunk failed: {e}"

            let none = scripted [ "log"; "-r"; "@" ] (Reply.Ok "\n")

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
            // A conflict-free revision is a normal, empty-output success on `jj file list`
            // (unlike `resolve --list`, it never exits non-zero just for "nothing matched").
            let none = scripted [ "file"; "list" ] (Reply.Ok "")

            match! none.ResolveList(".", "@") with
            | Ok xs -> Assert.That(xs, Is.Empty)
            | Error e -> Assert.Fail $"resolve_list (none) failed: {e}"

            // A real failure (bad revset, …) must surface as an error.
            let bad =
                scripted [ "file"; "list" ] (Reply.Fail(1, "Error: Revision `bogus` doesn't exist"))

            let! r2 = bad.ResolveList(".", "bogus")
            Assert.That(Result.isError r2, Is.True)
            // Success with conflicts → parsed, JSON-decoded paths.
            let some = scripted [ "file"; "list" ] (Reply.Ok "\"a.rs\"\n")

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

    // One valid `op log` row (`OP_TEMPLATE` shape) whose short id is `id` — for scripting the
    // divergence probe. Needs >= 3 tab fields for `parseOperations` to keep the id.
    static member private OpRow(id: string) =
        $"{id}{tab}u@h{tab}2026-01-01T00:00:00+00:00{tab}probe\n"

    [<Test>]
    member _.TransactionRestoresOpHeadOnError() : Task =
        task {
            // Capture (op log --limit 1) yields the pre-op; the mutation fails; the
            // divergence probe (op log --limit 32) still shows that pre-op, so the rollback
            // restores it with that exact id (the `abc123` token gates the restore rule).
            let runner =
                ScriptedRunner()
                    .On([ "op"; "log"; "--limit"; "1" ], Reply.Ok "abc123\n")
                    .On([ "op"; "log"; "--limit"; "32" ], Reply.Ok(SemanticsTests.OpRow "abc123"))
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
            // the unscripted restore command would raise. Success must NOT roll back (so the
            // divergence probe never runs either).
            let runner =
                ScriptedRunner().On([ "op"; "log" ], Reply.Ok "abc123\n").On([ "describe" ], Reply.Ok "")

            let jj = Jj.WithRunner runner

            match! jj.Transaction("/r", (fun tx -> tx.Describe("/r", "wip"))) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"transaction should succeed: {e}"
        }

    [<Test>]
    member _.RollbackToRestoresWhenCapturedOpStillVisible() : Task =
        task {
            // A newer op sits on top of the captured one, but the captured op is still within
            // the probe window → restore proceeds and reports RolledBack.
            let runner =
                ScriptedRunner()
                    .On([ "op"; "log" ], Reply.Ok(SemanticsTests.OpRow "opX" + SemanticsTests.OpRow "opabc"))
                    .On([ "op"; "restore"; "opabc" ], Reply.Ok "")

            let jj = Jj.WithRunner runner

            match! jj.RollbackTo("/r", "opabc") with
            | Ok RollbackOutcome.RolledBack -> ()
            | other -> Assert.Fail $"expected RolledBack, got {other}"
        }

    [<Test>]
    member _.RollbackToSkipsWhenCapturedOpDivergedOut() : Task =
        task {
            // The probe op-log no longer contains the captured op → a concurrent op advanced
            // past it. The rollback must be refused (no `op restore` scripted — one would
            // raise) and report the divergence.
            let runner =
                ScriptedRunner().On([ "op"; "log" ], Reply.Ok(SemanticsTests.OpRow "opNEW"))

            let jj = Jj.WithRunner runner

            match! jj.RollbackTo("/r", "opabc") with
            | Ok(RollbackOutcome.SkippedDiverged("opabc", "opNEW")) -> ()
            | other -> Assert.Fail $"expected SkippedDiverged(opabc, opNEW), got {other}"
        }

    [<Test>]
    member _.RollbackToRunsCleanupOnFreshBudgetDespiteCancelledToken() : Task =
        task {
            // The client's ambient cancellation token is already fired. If the cleanup ran on
            // it, `op log`/`op restore` would error as cancelled before matching (the
            // ScriptedRunner cancellation contract). Reaching RolledBack proves the cleanup
            // ran on a *fresh* budget instead.
            use cts = new System.Threading.CancellationTokenSource()
            cts.Cancel()

            let runner =
                ScriptedRunner()
                    .On([ "op"; "log" ], Reply.Ok(SemanticsTests.OpRow "opabc"))
                    .On([ "op"; "restore"; "opabc" ], Reply.Ok "")

            let jj = (Jj.WithRunner runner).DefaultCancelOn cts.Token

            match! jj.RollbackTo("/r", "opabc") with
            | Ok RollbackOutcome.RolledBack -> ()
            | other -> Assert.Fail $"expected RolledBack on a fresh budget, got {other}"
        }

    [<Test>]
    member _.TransactionRollsBackCancelledClosureOnFreshBudget() : Task =
        task {
            // The closure cancels the operation's token, then a mutation on that token fails
            // as cancelled — the classic "cancelled mid-transaction". The rollback must still
            // run (on a fresh budget): we record that `op restore` actually reached the runner.
            use cts = new System.Threading.CancellationTokenSource()
            let restoreRan = ref false

            let runner =
                ScriptedRunner()
                    .On([ "op"; "log"; "--limit"; "1" ], Reply.Ok "opabc\n")
                    .On([ "op"; "log"; "--limit"; "32" ], Reply.Ok(SemanticsTests.OpRow "opabc"))
                    .When(
                        (fun (cmd: Command) ->
                            if cmd.Arguments |> Seq.contains "restore" then
                                restoreRan.Value <- true
                                true
                            else
                                false),
                        Reply.Ok ""
                    )

            let jj = (Jj.WithRunner runner).DefaultCancelOn cts.Token

            let closure (tx: Jj) =
                task {
                    cts.Cancel()
                    return! tx.Describe("/r", "wip")
                }

            let! res = jj.Transaction("/r", closure)

            Assert.That(Result.isError res, Is.True, "the cancelled closure's error must surface")
            Assert.That(restoreRan.Value, Is.True, "rollback must run on a fresh budget despite cancellation")
        }

    [<Test>]
    member _.TransactionSkipsRollbackWhenOpLogDiverged() : Task =
        task {
            // The closure fails; between op-head capture and rollback a concurrent op advanced
            // the op-log so the captured op is no longer visible. The rollback must be refused:
            // no `op restore` is scripted, so an attempt would raise. The closure error still
            // surfaces.
            let runner =
                ScriptedRunner()
                    .On([ "op"; "log"; "--limit"; "1" ], Reply.Ok "opabc\n")
                    .On([ "op"; "log"; "--limit"; "32" ], Reply.Ok(SemanticsTests.OpRow "opNEW"))
                    .On([ "describe" ], Reply.Fail(1, "boom"))

            let jj = Jj.WithRunner runner

            let! res = jj.Transaction("/r", (fun tx -> tx.Describe("/r", "wip")))
            Assert.That(Result.isError res, Is.True, "the closure error must still surface")
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
            let! o = isErr (jj.NewChild(".", "-evil"))

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
                  n, "empty edit"
                  o, "new_child parent" ] do
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
            // A modelled method through the `at(dir)` view resolves the workspace root for the
            // bound dir first, then runs the query from that resolved root — byte-identical argv
            // to the dir-taking form (incl. the forced `--color never`). Here `/bound/dir` IS its
            // own resolved root, so the diff command's cwd still equals it.
            let captured = ref (None: Command option)

            let runner =
                ScriptedRunner()
                    .On([ "root" ], Reply.Ok "/bound/dir\n")
                    .When(
                        (fun (cmd: Command) ->
                            captured.Value <- Some cmd
                            true),
                        Reply.Ok ""
                    )

            let jj = Jj.WithRunner runner

            let! _ = jj.At("/bound/dir").Status()

            match captured.Value with
            | Some cmd ->
                Assert.That(
                    cmd.WorkingDirectory,
                    Is.EqualTo(Some "/bound/dir"),
                    "the diff query runs from the resolved workspace root"
                )

                Assert.That(String.concat " " cmd.Arguments, Is.EqualTo "diff -r @ --summary --color never")
            | None -> Assert.Fail "no command captured"
        }

    [<Test>]
    member _.JjAtRawRunBindsDir() : Task =
        task {
            // The raw `Run`/`RunRaw` hatches on the bound view run in the bound `dir`
            // (WorkingDirectory = Some dir), like the modelled methods — not the process cwd.
            let captured, runner = capturing (Reply.Ok "ok\n")
            let jj = Jj.WithRunner runner

            let! _ = jj.At("/bound/dir").Run [ "root" ]

            match captured.Value with
            | Some cmd ->
                Assert.That(cmd.WorkingDirectory, Is.EqualTo(Some "/bound/dir"), "the raw Run hatch binds dir")
            | None -> Assert.Fail "no command captured"

            let capturedRaw, runnerRaw = capturing (Reply.Ok "")
            let jjRaw = Jj.WithRunner runnerRaw

            let! _ = jjRaw.At("/bound/dir").RunRaw [ "status" ]

            match capturedRaw.Value with
            | Some cmd ->
                Assert.That(cmd.WorkingDirectory, Is.EqualTo(Some "/bound/dir"), "the raw RunRaw hatch binds dir")
            | None -> Assert.Fail "no command captured for RunRaw"
        }

    [<Test>]
    member _.JjUnboundRawRunStaysProcessCwd() : Task =
        task {
            // The unbound client's raw `Run` still runs in the process cwd (WorkingDirectory =
            // None) — the `dir`-bound form lives only on the `at(dir)` view / `Run(dir, …)`.
            let captured, runner = capturing (Reply.Ok "ok\n")
            let jj = Jj.WithRunner runner

            let! _ = jj.Run [ "root" ]

            match captured.Value with
            | Some cmd -> Assert.That(cmd.WorkingDirectory, Is.EqualTo None, "the unbound raw Run is NOT bound to dir")
            | None -> Assert.Fail "no command captured"
        }

// ---------------------------------------------------------------------------
// Read-only mode (`ReadOnly` / `--ignore-working-copy`)
// ---------------------------------------------------------------------------

[<TestFixture>]
type ReadOnlyModeTests() =

    [<Test>]
    member _.ReadOnlyIsObservableAndPreservedByConfigChaining() =
        // `Create`/`WithRunner` build a snapshotting client; `ReadOnly()` flips the mode on and
        // returns a NEW client (this one unchanged); the config chainables carry the mode either
        // way, so composing timeouts/env onto a read-only client keeps it read-only.
        let plain = permissive ()
        Assert.That(plain.IsReadOnly, Is.False, "a fresh client snapshots on reads")
        let ro = plain.ReadOnly()
        Assert.That(ro.IsReadOnly, Is.True, "ReadOnly() turns the mode on")
        Assert.That(plain.IsReadOnly, Is.False, "ReadOnly() is non-destructive — the original is unchanged")

        Assert.That(
            (ro.DefaultTimeout(System.TimeSpan.FromSeconds 1.0)).IsReadOnly,
            Is.True,
            "config chaining preserves read-only"
        )

        Assert.That((ro.DefaultEnv("K", "V")).IsReadOnly, Is.True)

        Assert.That(
            (plain.DefaultTimeout(System.TimeSpan.FromSeconds 1.0)).IsReadOnly,
            Is.False,
            "a plain client stays snapshotting through config chaining"
        )

    [<Test>]
    member _.EveryReadPrependsIgnoreWorkingCopyBeforeItsSubcommand() : Task =
        task {
            // The audit: exercise every single-command read on a read-only client and prove each
            // one prepends the global `--ignore-working-copy` flag *before* its subcommand — so no
            // read path was missed and the flag can never land in a value slot. Compound reads
            // (`Status`/`DiffSummary`, which also resolve the root) are pinned separately below.
            let calls, runner = recording (Reply.Ok "")
            let jj = (Jj.WithRunner runner).ReadOnly()

            let! _ = jj.StatusText "."
            let! _ = jj.Log(".", "@", 5)
            let! _ = jj.Bookmarks "."
            let! _ = jj.BookmarksAll "."
            let! _ = jj.ReachableBookmarks "."
            let! _ = jj.Root "."
            let! _ = jj.CurrentBookmark "."
            let! _ = jj.Trunk "."
            let! _ = jj.DiffStat(".", "@")
            let! _ = jj.DiffText(".", DiffSpec.WorkingTree)
            let! _ = jj.CommitCount(".", "@")
            let! _ = jj.IsConflicted(".", "@")
            let! _ = jj.ResolveList(".", "@")
            let! _ = jj.TemplateQuery(".", "@", "commit_id", Some 1)
            let! _ = jj.Evolog(".", "@", 3)
            let! _ = jj.FileShow(".", "@", "a.fs")
            let! _ = jj.OpHead "."
            let! _ = jj.OpLog(".", 10)
            let! _ = jj.WorkspaceList "."
            let! _ = jj.WorkspaceRoot(".", None)
            let! _ = jj.WorkspaceRoots(".", [ "default" ])

            Assert.That(calls.Count, Is.EqualTo 21, "one command per read (WorkspaceRoots fans out one per name)")

            for cmd in calls do
                let args = argsOf cmd
                let joined = String.concat " " args

                Assert.That(
                    List.head args,
                    Is.EqualTo "--ignore-working-copy",
                    $"the read-only flag must lead the argv (before the subcommand): {joined}"
                )

                Assert.That(
                    (List.item 1 args) <> "--ignore-working-copy",
                    Is.True,
                    "a real subcommand — not a second flag — must follow"
                )

            // Spot-check a couple of exact argv prefixes: flag, then the (possibly two-word) subcommand.
            Assert.That(
                ((argsOf calls.[2]) |> List.take 3) = [ "--ignore-working-copy"; "bookmark"; "list" ],
                Is.True,
                "bookmark list leads with the flag then the two-word subcommand"
            )

            Assert.That(
                ((argsOf calls.[17]) |> List.take 3) = [ "--ignore-working-copy"; "op"; "log" ],
                Is.True,
                "op log leads with the flag then the two-word subcommand"
            )
        }

    [<Test>]
    member _.StatusReadOnlyFlagsBothTheRootProbeAndTheDiffQuery() : Task =
        task {
            // `Status` resolves the workspace root first, then runs `diff --summary` from it: BOTH
            // underlying commands must be read-only, or resolving the path prefix would itself
            // snapshot and defeat the point.
            let calls, runner = recording (Reply.Ok "")
            let jj = (Jj.WithRunner runner).ReadOnly()

            let! _ = jj.Status "/repo"

            Assert.That(calls.Count, Is.EqualTo 2, "Status = root probe + diff --summary")

            let subcommands =
                calls |> Seq.map (fun c -> (argsOf c) |> List.item 1) |> List.ofSeq

            Assert.That((subcommands = [ "root"; "diff" ]), Is.True, "the root probe precedes the diff query")

            for cmd in calls do
                Assert.That(
                    List.head (argsOf cmd),
                    Is.EqualTo "--ignore-working-copy",
                    "both the root probe and the diff query carry the flag"
                )
        }

    [<Test>]
    member _.ReadOnlyFileAnnotatePrependsFlagAheadOfTheDashDashSeparator() : Task =
        task {
            // `file annotate` ends in a `-- <path>` separator, and jj requires global flags to
            // precede `--`. The read-only flag is prepended at the very front, so it leads the
            // argv AND sits before `--` (and the untrusted path can never inject it).
            let calls, runner = recording (Reply.Ok "")
            let jj = (Jj.WithRunner runner).ReadOnly()

            let! _ = jj.FileAnnotate(".", "src/a.fs", None)

            Assert.That(calls.Count, Is.EqualTo 1)
            let args = argsOf calls.[0]

            Assert.That(
                (args |> List.take 3) = [ "--ignore-working-copy"; "file"; "annotate" ],
                Is.True,
                "the flag leads, then the two-word `file annotate` subcommand"
            )

            let flagIdx = List.findIndex ((=) "--ignore-working-copy") args
            let dashIdx = List.findIndex ((=) "--") args
            Assert.That(flagIdx < dashIdx, Is.True, "the read-only flag must precede the `--` path separator")
        }

    [<Test>]
    member _.DefaultSnapshottingClientNeverCarriesTheFlag() : Task =
        task {
            // The default (non-read-only) client must behave exactly as before: it snapshots, so
            // its reads carry NO `--ignore-working-copy`.
            let calls, runner = recording (Reply.Ok "")
            let jj = Jj.WithRunner runner // NOT read-only

            let! _ = jj.Bookmarks "."
            let! _ = jj.Log(".", "@", 5)
            let! _ = jj.OpLog(".", 10)
            let! _ = jj.Status "/repo"

            Assert.That(calls.Count >= 4, Is.True)

            for cmd in calls do
                let args = argsOf cmd
                let joined = String.concat " " args

                Assert.That(
                    List.contains "--ignore-working-copy" args,
                    Is.False,
                    $"a snapshotting client must not pass the flag: {joined}"
                )
        }

    [<Test>]
    member _.ReadOnlyModeNeverAltersAMutationsArgv() : Task =
        task {
            // The mode is read-only *for reads only*: a mutation on a read-only client keeps its
            // exact argv (no flag), so `describe`/`new`/`bookmark set`/`rebase`/`op restore` still
            // snapshot and record operations as they always have.
            let calls, runner = recording (Reply.Ok "")
            let jj = (Jj.WithRunner runner).ReadOnly()

            let! _ = jj.Describe(".", "msg")
            let! _ = jj.NewChange(".", "msg")
            let! _ = jj.BookmarkSet(".", "feat", "@")
            let! _ = jj.Rebase(".", "main")
            let! _ = jj.OpRestore(".", "abc123")
            let! _ = jj.CommitPaths(".", [ JjFileset.Path "a.fs" ], "msg")

            Assert.That(calls.Count, Is.EqualTo 6)

            for cmd in calls do
                let args = argsOf cmd
                let joined = String.concat " " args

                Assert.That(
                    List.contains "--ignore-working-copy" args,
                    Is.False,
                    $"a mutation must be untouched by read-only mode: {joined}"
                )

                Assert.That(
                    List.head args <> "--ignore-working-copy",
                    Is.True,
                    "the argv still leads with the subcommand"
                )
        }

[<TestFixture>]
type ObserverWiringTests() =

    [<Test>]
    member _.WithObserverThreadsThroughTheJjClient() : Task =
        task {
            let events = ResizeArray<CommandEvent>()

            let observer =
                { new ICommandObserver with
                    member _.OnStarted(ev) = events.Add ev
                    member _.OnFinished(_, _, _) = () }

            let jj =
                Jj.WithRunner(ScriptedRunner().Fallback(Reply.Ok "jj 0.42.0")).WithObserver observer

            match! jj.Run [ "--version" ] with
            | Ok _ -> ()
            | Error e -> Assert.Fail $"{e}"

            Assert.That(events.Count, Is.EqualTo 1, "the observer is threaded through the Jj client")
            Assert.That(events[0].Program, Is.EqualTo "jj")
        }
