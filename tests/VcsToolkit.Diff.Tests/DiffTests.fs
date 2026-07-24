module VcsToolkit.Diff.Tests

open System.Text
open NUnit.Framework
open VcsToolkit.Diff

// Build fixtures from character codes so no backslash/tab/newline escape ever has to
// survive a round-trip through the editor — every control byte is explicit.
let private nl = string (char 10)
let private tab = string (char 9)

/// Concatenate lines, each terminated by a newline (mirrors the Rust `concat!` fixtures).
let private doc (lines: string list) =
    lines |> List.map (fun l -> l + nl) |> String.concat ""

/// Produce a git C-quoted path: wrap in double quotes, octal-escape every non-ASCII
/// byte and the backslash/quote, and render a tab as `\t` — matching git's default
/// `core.quotePath` rendering that the parser must decode.
let private gitQuote (path: string) : string =
    let bs = char 92
    let dq = char 34
    let bytes = Encoding.UTF8.GetBytes path
    let sb = StringBuilder()
    sb.Append dq |> ignore

    for b in bytes do
        if b = 9uy then
            sb.Append(bs).Append 't' |> ignore
        elif int b >= 32 && int b < 128 && b <> byte dq && b <> byte bs then
            sb.Append(char (int b)) |> ignore
        else
            let octal = System.Convert.ToString(int b, 8).PadLeft(3, '0')
            sb.Append(bs).Append octal |> ignore

    sb.Append dq |> ignore
    sb.ToString()

[<TestFixture>]
type DiffTests() =

    [<Test>]
    member _.DecodesGitQuotedPaths() =
        let decode = TextParse.unquoteGitPath
        let smile = Encoding.UTF8.GetString([| 0xF0uy; 0x9Fuy; 0x98uy; 0x80uy |])

        Assert.That(decode "", Is.EqualTo "")
        Assert.That(decode "abc/def", Is.EqualTo "abc/def")
        Assert.That(decode "\"\"", Is.EqualTo "")
        Assert.That(decode "\"hello\\nworld\"", Is.EqualTo "hello\nworld")
        Assert.That(decode "\"a\\tb\"", Is.EqualTo "a\tb")
        Assert.That(decode "\"path\\\\to\\\\file\"", Is.EqualTo "path\\to\\file")
        Assert.That(decode "\"say \\\"hi\\\"\"", Is.EqualTo "say \"hi\"")
        Assert.That(decode "\"\\101\"", Is.EqualTo "A")
        Assert.That(decode "\"\\360\\237\\230\\200\"", Is.EqualTo smile)
        Assert.That(decode "\"\\x\"", Is.EqualTo "x")
        Assert.That(decode "\"unterminated", Is.EqualTo "unterminated")

    [<Test>]
    member _.CoversAddModifyDeleteRename() =
        let full =
            doc
                [ "diff --git a/new b/new"
                  "new file mode 100644"
                  "--- /dev/null"
                  "+++ b/new"
                  "@@ -0,0 +1 @@"
                  "+n"
                  "diff --git a/mod b/mod"
                  "--- a/mod"
                  "+++ b/mod"
                  "@@ -1 +1 @@"
                  "-a"
                  "+b"
                  "diff --git a/gone b/gone"
                  "deleted file mode 100644"
                  "--- a/gone"
                  "+++ /dev/null"
                  "@@ -1 +0,0 @@"
                  "-x"
                  "diff --git a/old/f.txt b/new/f.txt"
                  "similarity index 100%"
                  "rename from old/f.txt"
                  "rename to new/f.txt" ]

        let files = parseDiff full
        Assert.That(files.Length, Is.EqualTo 4)
        Assert.That(files.[0].Path, Is.EqualTo "new")
        Assert.That(files.[0].Change, Is.EqualTo ChangeKind.Added)
        Assert.That(files.[1].Path, Is.EqualTo "mod")
        Assert.That(files.[1].Change, Is.EqualTo ChangeKind.Modified)
        Assert.That(files.[2].Path, Is.EqualTo "gone")
        Assert.That(files.[2].Change, Is.EqualTo ChangeKind.Deleted)
        Assert.That(files.[3].Path, Is.EqualTo "new/f.txt")
        Assert.That(files.[3].Change, Is.EqualTo ChangeKind.Renamed)
        // The rename carries its old path so the deletion is recorded too.
        Assert.That(files.[3].OldPath, Is.EqualTo(Some "old/f.txt"))

    [<Test>]
    member _.HandlesSpacePaths() =
        // git appends a trailing tab to +++/--- paths containing spaces; the path must
        // survive intact (the `diff --git` header is ambiguous here).
        let full =
            doc
                [ "diff --git a/a b/c.txt b/a b/c.txt"
                  "--- a/a b/c.txt" + tab
                  "+++ b/a b/c.txt" + tab
                  "@@ -1 +1 @@"
                  "-x"
                  "+y" ]

        let files = parseDiff full
        Assert.That(files.Length, Is.EqualTo 1)
        Assert.That(files.[0].Path, Is.EqualTo "a b/c.txt")

    [<Test>]
    member _.UnquotesNonAsciiModify() =
        let full =
            doc
                [ sprintf "diff --git %s %s" (gitQuote "a/café.txt") (gitQuote "b/café.txt")
                  "index 45b983b..b023018 100644"
                  sprintf "--- %s" (gitQuote "a/café.txt")
                  sprintf "+++ %s" (gitQuote "b/café.txt")
                  "@@ -1 +1 @@"
                  "-hi"
                  "+bye" ]

        let files = parseDiff full
        Assert.That(files.Length, Is.EqualTo 1, "the non-ASCII file must not be dropped")
        Assert.That(files.[0].Path, Is.EqualTo "café.txt")
        Assert.That(files.[0].Change, Is.EqualTo ChangeKind.Modified)

    [<Test>]
    member _.UnquotesNonAsciiRename() =
        let full =
            doc
                [ sprintf "diff --git %s %s" (gitQuote "a/café.txt") (gitQuote "b/résumé.txt")
                  "similarity index 100%"
                  sprintf "rename from %s" (gitQuote "café.txt")
                  sprintf "rename to %s" (gitQuote "résumé.txt") ]

        let files = parseDiff full
        Assert.That(files.Length, Is.EqualTo 1)
        Assert.That(files.[0].Path, Is.EqualTo "résumé.txt")
        Assert.That(files.[0].Change, Is.EqualTo ChangeKind.Renamed)
        Assert.That(files.[0].OldPath, Is.EqualTo(Some "café.txt"))

    [<Test>]
    member _.UnquotesQuotedHeaderFallback() =
        // A binary/mode-only quoted section (no +++/---/rename) resolves its path from
        // the quoted `diff --git` header.
        let full =
            doc
                [ sprintf "diff --git %s %s" (gitQuote "a/café.bin") (gitQuote "b/café.bin")
                  "index 0000000..1111111 100644"
                  sprintf "Binary files %s and %s differ" (gitQuote "a/café.bin") (gitQuote "b/café.bin") ]

        let files = parseDiff full
        Assert.That(files.Length, Is.EqualTo 1)
        Assert.That(files.[0].Path, Is.EqualTo "café.bin")

    [<Test>]
    member _.UnquotesEscapedTabPath() =
        let p = "a" + tab + "b.txt"

        let full =
            doc
                [ sprintf "diff --git %s %s" (gitQuote ("a/" + p)) (gitQuote ("b/" + p))
                  sprintf "--- %s" (gitQuote ("a/" + p))
                  sprintf "+++ %s" (gitQuote ("b/" + p))
                  "@@ -1 +1 @@"
                  "-x"
                  "+y" ]

        let files = parseDiff full
        Assert.That(files.Length, Is.EqualTo 1)
        Assert.That(files.[0].Path, Is.EqualTo p)

    [<Test>]
    member _.PreservesLiteralBackslashesInGitPaths() =
        let bs = string (char 92)
        let path = "dir" + bs + "literal.txt"

        let full =
            doc
                [ sprintf "diff --git %s %s" (gitQuote ("a/" + path)) (gitQuote ("b/" + path))
                  sprintf "--- %s" (gitQuote ("a/" + path))
                  sprintf "+++ %s" (gitQuote ("b/" + path))
                  "@@ -1 +1 @@"
                  "-old"
                  "+new" ]

        let files = parseDiff full
        Assert.That(files.Length, Is.EqualTo 1)
        Assert.That(files.[0].Path, Is.EqualTo path)

    [<Test>]
    member _.DropsSectionsWithNoResolvablePath() =
        let bad = doc [ "diff --git a/x b/"; "binary files differ" ]
        Assert.That(parseDiff bad, Is.Empty)

        // An empty `+++ b/` falls through to the header's real `b/<path>`.
        let recover =
            doc [ "diff --git a/real.txt b/real.txt"; "+++ b/"; "binary files differ" ]

        let files = parseDiff recover
        Assert.That(files.Length, Is.EqualTo 1)
        Assert.That(files.[0].Path, Is.EqualTo "real.txt")

        // A mode-only change keeps its path via the header fallback.
        let modeOnly =
            doc [ "diff --git a/f.sh b/f.sh"; "old mode 100644"; "new mode 100755" ]

        let files = parseDiff modeOnly
        Assert.That(files.Length, Is.EqualTo 1)
        Assert.That(files.[0].Path, Is.EqualTo "f.sh")

    [<Test>]
    member _.HeaderFallbackHandlesSubstringBSlashInPath() =
        // The unquoted header form is symmetric (`a/<p> b/<p>`); a path whose directory
        // name itself contains " b/" (e.g. "a b/file.bin") must not make the naive
        // first-match split pick the b-marker inside the a-path.
        let full = doc [ "diff --git a/a b/file.bin b/a b/file.bin"; "binary files differ" ]

        let files = parseDiff full
        Assert.That(files.Length, Is.EqualTo 1)
        Assert.That(files.[0].Path, Is.EqualTo "a b/file.bin")

    [<Test>]
    member _.HeaderFallbackStillResolvesOrdinaryUnquotedPath() =
        // Regression guard: an ordinary path with no " b/" substring inside it must keep
        // resolving via the header fallback.
        let full = doc [ "diff --git a/plain.bin b/plain.bin"; "binary files differ" ]

        let files = parseDiff full
        Assert.That(files.Length, Is.EqualTo 1)
        Assert.That(files.[0].Path, Is.EqualTo "plain.bin")

    [<Test>]
    member _.CopyOnlyBinarySectionResolvesFromCopyTo() =
        // A copy-only section (no +++/---/rename lines) has an asymmetric header
        // (a-path != b-path), so the header fallback can't recover it; `copy to` must
        // be used instead of silently dropping the file.
        let full =
            doc
                [ "diff --git a/orig.bin b/copy.bin"
                  "similarity index 100%"
                  "copy from orig.bin"
                  "copy to copy.bin"
                  "Binary files a/orig.bin and b/copy.bin differ" ]

        let files = parseDiff full
        Assert.That(files.Length, Is.EqualTo 1)
        Assert.That(files.[0].Path, Is.EqualTo "copy.bin")

    [<Test>]
    member _.ParsesHunkRangesAndBody() =
        let full =
            doc
                [ "diff --git a/f b/f"
                  "--- a/f"
                  "+++ b/f"
                  "@@ -1,2 +1,3 @@ fn main()"
                  " ctx"
                  "-old"
                  "+new"
                  "+added" ]

        let files = parseDiff full
        Assert.That(files.Length, Is.EqualTo 1)
        // The verbatim section is preserved for display.
        Assert.That(files.[0].Raw, Is.EqualTo full)
        let hunk = files.[0].Hunks.[0]
        Assert.That(hunk.OldStart, Is.EqualTo 1UL)
        Assert.That(hunk.OldLines, Is.EqualTo 2UL)
        Assert.That(hunk.NewStart, Is.EqualTo 1UL)
        Assert.That(hunk.NewLines, Is.EqualTo 3UL)
        Assert.That(hunk.Section, Is.EqualTo "fn main()")
        Assert.That(hunk.Lines.Length, Is.EqualTo 4)
        Assert.That(hunk.Lines.[0], Is.EqualTo(DiffLine.Context "ctx"))
        Assert.That(hunk.Lines.[1], Is.EqualTo(DiffLine.Removed "old"))
        Assert.That(hunk.Lines.[2], Is.EqualTo(DiffLine.Added "new"))
        Assert.That(hunk.Lines.[3], Is.EqualTo(DiffLine.Added "added"))

    [<Test>]
    member _.OmittedCountDefaultsToOne() =
        let full =
            doc [ "diff --git a/f b/f"; "--- a/f"; "+++ b/f"; "@@ -3 +3 @@"; "-a"; "+b" ]

        let hunk = (parseDiff full).[0].Hunks.[0]
        Assert.That(hunk.OldStart, Is.EqualTo 3UL)
        Assert.That(hunk.OldLines, Is.EqualTo 1UL)
        Assert.That(hunk.NewStart, Is.EqualTo 3UL)
        Assert.That(hunk.NewLines, Is.EqualTo 1UL)

[<TestFixture>]
type VersionTests() =

    [<Test>]
    member _.ParsesRealWorldShapes() =
        let v = parseDottedVersion "git version 2.54.0.windows.1" |> Option.get
        Assert.That(v.Major, Is.EqualTo 2UL)
        Assert.That(v.Minor, Is.EqualTo 54UL)
        Assert.That(v.Patch, Is.EqualTo 0UL)

        let v = parseDottedVersion "git version 2.41.0-rc1" |> Option.get
        Assert.That(v.Patch, Is.EqualTo 0UL)

        let v = parseDottedVersion "git version 2.54" |> Option.get
        Assert.That(v.Patch, Is.EqualTo 0UL, "missing patch defaults to 0")

        let v = parseDottedVersion "jj 0.42.0" |> Option.get
        Assert.That(v.Major, Is.EqualTo 0UL)
        Assert.That(v.Minor, Is.EqualTo 42UL)

        Assert.That((parseDottedVersion "no digits here").IsNone)
        Assert.That((parseDottedVersion "git version unknowable").IsNone)

    [<Test>]
    member _.OrdersNumerically() =
        let lo = parseDottedVersion "jj 0.38.0" |> Option.get
        let hi = parseDottedVersion "jj 0.40.0" |> Option.get
        Assert.That(hi > lo)
        // The discriminating case: 2.9.0 < 2.10.0 holds numerically but FAILS under a
        // lexicographic ("9" > "10") comparison — guards the custom comparison itself.
        let nine =
            { Major = 2UL
              Minor = 9UL
              Patch = 0UL }

        let ten =
            { Major = 2UL
              Minor = 10UL
              Patch = 0UL }

        Assert.That(nine < ten)
        Assert.That(ten > nine)

    [<Test>]
    member _.DisplaysDotted() =
        let v = parseDottedVersion "git version 2.54.1" |> Option.get
        Assert.That(v.ToString(), Is.EqualTo "2.54.1")
