module VcsToolkit.Git.ConflictTests

open NUnit.Framework
open ProcessKit
open VcsToolkit.Git

// A two-way (`merge` style) and a three-way (`diff3`) conflicted file.
let private MERGE_2WAY =
    "line 1\n<<<<<<< HEAD\nmain line 2\n=======\nfeature line 2\n>>>>>>> feature\nline 3\n"

let private DIFF3 =
    "line 1\n<<<<<<< HEAD\nmain line 2\n||||||| 0b025ce\nline 2\n=======\nfeature line 2\n>>>>>>> feature\nline 3\n"

let private parse (s: string) =
    match Conflict.parseConflicts s with
    | Ok segments -> segments
    | Error e -> failwithf "parse failed: %A" e

/// The conflict region at index `i`, or a test failure.
let private regionAt (segments: ConflictSegment list) (i: int) : ConflictRegion =
    match segments.[i] with
    | ConflictSegment.Conflict region -> region
    | other -> failwithf "expected a conflict at %d, got %A" i other

/// Assert `resolve segments side` succeeds with `expected` (string compare — unambiguous).
let private assertResolve (segments: ConflictSegment list) (side: ResolutionSide) (expected: string) =
    match Conflict.resolve segments side with
    | Ok s -> Assert.That(s, Is.EqualTo expected)
    | Error e -> Assert.Fail $"resolve failed: {e}"

[<TestFixture>]
type GitConflictTests() =

    [<Test>]
    member _.ParsesTwoWayMergeStyle() =
        let segments = parse MERGE_2WAY
        Assert.That(segments.Length, Is.EqualTo 3)
        let region = regionAt segments 1
        Assert.That(region.OursLabel, Is.EqualTo "HEAD")
        Assert.That(region.TheirsLabel, Is.EqualTo "feature")
        Assert.That(region.Ours = [ "main line 2\n" ])
        Assert.That(region.Theirs = [ "feature line 2\n" ])
        Assert.That(region.Base = None)
        Assert.That(region.MarkerLen, Is.EqualTo 7)

    [<Test>]
    member _.ParsesDiff3WithBase() =
        let region = regionAt (parse DIFF3) 1
        Assert.That(region.BaseLabel = Some "0b025ce")
        Assert.That(region.Base = Some [ "line 2\n" ])

    [<Test>]
    member _.RepeatedBaseMarkerLineIsBaseContent() =
        // A SECOND `|`-run line inside a diff3 region is base *content*, not a replacement base
        // marker — overwriting it used to drop a line on render, breaking the byte-exact roundtrip.
        let s = "<<<<<<<< HEAD\n|||||||| base\n|||||||| base\n========\n>>>>>>>> branché\n"
        let segments = parse s
        let region = regionAt segments 0
        Assert.That(region.Base = Some [ "|||||||| base\n" ], "the second |-run line is base content")
        Assert.That(Conflict.render segments, Is.EqualTo s, "roundtrip must be byte-exact")

    [<Test>]
    member _.RenderRoundtripsExactly() =
        // Byte-exact roundtrip — including CRLF, custom marker sizes, and a conflict at EOF with no
        // trailing newline.
        let crlf = "a\r\n<<<<<<< HEAD\r\nours\r\n=======\r\ntheirs\r\n>>>>>>> b\r\nz\r\n"

        let wide =
            "<<<<<<<<<<<<<<< HEAD\nours\n===============\ntheirs\n>>>>>>>>>>>>>>> b\n"

        let eof = "x\n<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> b"

        for sample in [ MERGE_2WAY; DIFF3; crlf; wide; eof ] do
            Assert.That(Conflict.render (parse sample), Is.EqualTo sample, $"roundtrip: {sample}")

        // The wide sample detected the larger marker run.
        Assert.That((regionAt (parse wide) 0).MarkerLen, Is.EqualTo 15)

    [<Test>]
    member _.ResolveTakesOneSideEverywhere() =
        let two = MERGE_2WAY + "between\n" + MERGE_2WAY
        let segments = parse two
        assertResolve segments ResolutionSide.Ours "line 1\nmain line 2\nline 3\nbetween\nline 1\nmain line 2\nline 3\n"

        assertResolve
            segments
            ResolutionSide.Theirs
            "line 1\nfeature line 2\nline 3\nbetween\nline 1\nfeature line 2\nline 3\n"

        // No base recorded in merge style → Base resolution is refused.
        Assert.That(Result.isError (Conflict.resolve segments ResolutionSide.Base))

        assertResolve (parse DIFF3) ResolutionSide.Base "line 1\nline 2\nline 3\n"

    [<Test>]
    member _.EmptySidesAndCleanFilesParse() =
        // One side deleted everything.
        let deletion = "<<<<<<< HEAD\n=======\nkept\n>>>>>>> b\n"
        assertResolve (parse deletion) ResolutionSide.Ours ""
        // A file without conflicts is one text segment.
        let clean = parse "just\ntext\n"
        Assert.That(clean.Length, Is.EqualTo 1)
        Assert.That(Conflict.hasConflictMarkers "just\ntext\n", Is.False)
        Assert.That(Conflict.hasConflictMarkers MERGE_2WAY)

    [<Test>]
    member _.MalformedFilesAreParseErrors() =
        // Only a genuinely broken *region* (an opener with no separator/terminator) is an error.
        for bad in
            [ "<<<<<<< HEAD\nours\n" // no separator
              "<<<<<<< HEAD\nours\n=======\ntheirs\n" ] do // no terminator
            match Conflict.parseConflicts bad with
            | Error(ProcessError.Parse _) -> ()
            | other -> Assert.Fail $"{bad} must fail with a Parse error, got {other}"

    [<Test>]
    member _.MarkerLikeContentOutsideARegionIsText() =
        // A `=======`/`>>>>>>>` run outside any region is ordinary content (Markdown underline,
        // divider, quoted email) — parsed as text, never an error, byte-exact roundtrip. (H6)
        for content in
            [ "Heading\n=======\nbody\n" // RST/Markdown setext underline
              "a\n=======================\nb\n" // divider banner
              ">>>>>>> deep email quote\nreply\n" // quoted email
              "code: a <<<<<<< b\n" ] do // marker run not at line start
            let segments = parse content

            Assert.That(
                segments
                |> List.forall (fun s ->
                    match s with
                    | ConflictSegment.Text _ -> true
                    | _ -> false),
                $"{content} must be all text"
            )

            Assert.That(Conflict.render segments, Is.EqualTo content, "round-trips byte-exact")

    [<Test>]
    member _.ParseNeverThrowsAndRoundtripsAdversarialInput() =
        // A stand-in for the Rust proptest: the marker grammar slices on marker-run lengths and
        // must never throw on hostile content, and whenever a file parses, re-rendering is
        // byte-exact. Exercise a corpus of adversarial marker permutations.
        let samples =
            [ ""
              "\n"
              "<<<<<<<\n=======\n>>>>>>>\n" // labelless markers
              "<<<<<<< a\n<<<<<<< b\n=======\n>>>>>>> c\n" // nested-looking opener inside ours
              "<<<<<<< a\n||||||| b\n=======\n>>>>>>> c\n" // empty ours/base/theirs
              "======= not a real sep\n" // bare separator as text
              "|||||||\n" // bare base marker as text (no opener)
              "<<<<<<<x\n" // run not followed by space/EOL → not a marker → text
              "a\r\nb\r\n" // CRLF, no markers
              "<<<<<<<<<< HEAD\nours\n========\ntheirs\n>>>>>>>>>> b\n" ] // mismatched run lengths

        for s in samples do
            Conflict.hasConflictMarkers s |> ignore

            match Conflict.parseConflicts s with
            | Ok segments -> Assert.That(Conflict.render segments, Is.EqualTo s, $"roundtrip: {s}")
            | Error _ -> () // a malformed region is a legitimate parse error, not a throw
