module VcsToolkit.Jj.ConflictTests

open NUnit.Framework
open ProcessKit
open VcsToolkit.Jj

// The 7-backslash `to:` marker run (must match the region's `%%%%%%%` run length of 7).
let private bs7 = System.String('\\', 7)

// Captured verbatim from jj 0.38 (default `diff` style).
let private DIFF_STYLE =
    "line 1\n<<<<<<< conflict 1 of 1\n%%%%%%% diff from: rnxsupvw 638ae425 \"base\"\n"
    + bs7
    + "        to: ozvltnxm 92f2b14f \"side-a\"\n-line 2\n+main line 2\n+++++++ xyrusolp ad268d1f \"side-b\"\nfeature line 2\n>>>>>>> conflict 1 of 1 ends\nline 3\n"

// Captured verbatim from jj 0.38 (`snapshot` style).
let private SNAPSHOT_STYLE =
    "line 1\n<<<<<<< conflict 1 of 1\n+++++++ kttusupp 7eedad44 \"side-a\"\nmain line 2\n------- rzkutuko 4fe1246f \"base\"\nline 2\n+++++++ ukuqwwlw 38f5069b \"side-b\"\nfeature line 2\n>>>>>>> conflict 1 of 1 ends\nline 3\n"

let private parse (s: string) =
    match Conflict.parseConflicts s with
    | Ok segments -> segments
    | Error e -> failwithf "parse failed: %A" e

let private regionAt (segments: JjConflictSegment list) (i: int) : JjConflictRegion =
    match segments.[i] with
    | JjConflictSegment.Conflict region -> region
    | other -> failwithf "expected a conflict at %d, got %A" i other

let private assertResolve (segments: JjConflictSegment list) (r: JjResolution) (expected: string) (label: string) =
    match Conflict.resolve segments r with
    | Ok s -> Assert.That(s, Is.EqualTo expected, label)
    | Error e -> Assert.Fail $"resolve failed ({label}): {e}"

/// jj switches to an explicit trailing-newline representation the moment ANY side lacks a
/// terminating newline. `resolve` must reproduce each side's and the base's exact bytes (incl. a
/// missing final newline), and `render` must round-trip. Regression for the C3 silent corruption.
let private checkEol (input: string) (side0: string) (side1: string) (baseStr: string) =
    let segments = parse input
    Assert.That(Conflict.render segments, Is.EqualTo input, "render must round-trip byte-exact")
    assertResolve segments (JjResolution.Side 0) side0 "side 0"
    assertResolve segments (JjResolution.Side 1) side1 "side 1"
    assertResolve segments JjResolution.Base baseStr "base"

[<TestFixture>]
type JjConflictTests() =

    [<Test>]
    member _.ParsesDiffStyleAndMaterializesSides() =
        let segments = parse DIFF_STYLE
        Assert.That(segments.Length, Is.EqualTo 3)
        let region = regionAt segments 1
        Assert.That((region.Number, region.Total), Is.EqualTo((1u, 1u)))
        Assert.That(region.Sections.Length, Is.EqualTo 2)
        let sides = region.Sides()
        Assert.That(sides.Length, Is.EqualTo 2)
        Assert.That(sides.[0] = [ "main line 2\n" ], "diff side = applied new text")
        Assert.That(sides.[1] = [ "feature line 2\n" ], "snapshot side verbatim")
        Assert.That(region.Base() = Some [ "line 2\n" ], "diff old text = base")

    [<Test>]
    member _.ParsesSnapshotStyle() =
        let region = regionAt (parse SNAPSHOT_STYLE) 1
        Assert.That(region.Sections.Length, Is.EqualTo 3)
        let sides = region.Sides()
        Assert.That(sides.[0] = [ "main line 2\n" ])
        Assert.That(sides.[1] = [ "feature line 2\n" ])
        Assert.That(region.Base() = Some [ "line 2\n" ])

        match region.Sections.[1] with
        | JjConflictSection.Base(label, _) -> Assert.That(label.Contains "\"base\"")
        | other -> Assert.Fail $"expected a Base section, got {other}"

    [<Test>]
    member _.ContentRunEndingInEndsIsNotTheTerminator() =
        // A content line that is a run of exactly n `>` followed by a word ending in "ends" must
        // NOT be mistaken for the terminator — only `conflict N of M ends` ends the region.
        let input =
            "line 1\n<<<<<<< conflict 1 of 1\n+++++++ side-a\n>>>>>>> recommends\n------- base\nline 2\n+++++++ side-b\nfeature line 2\n>>>>>>> conflict 1 of 1 ends\nline 3\n"

        let segments = parse input
        Assert.That(segments.Length, Is.EqualTo 3, "the region did not end early at `recommends`")
        let region = regionAt segments 1
        Assert.That((region.Number, region.Total), Is.EqualTo((1u, 1u)))

        Assert.That(
            region.Sides().[0] |> List.exists (fun l -> l.Contains "recommends"),
            "the `>>>…recommends` content line is part of side-a, not the terminator"
        )

        Assert.That(Conflict.render segments, Is.EqualTo input, "round-trips byte-for-byte")

    [<Test>]
    member _.DiffSectionRejectsMismatchedToMarkerLength() =
        // A `%%%%%%%` (7) diff header followed by an 8-long `\` run is malformed and must be
        // rejected, with an error pointing at the `to:` line.
        let input =
            "<<<<<<< conflict 1 of 1\n%%%%%%% diff from: ab cd \"base\"\n"
            + System.String('\\', 8)
            + "        to: ef gh \"side\"\n-line\n+new\n>>>>>>> conflict 1 of 1 ends\n"

        match Conflict.parseConflicts input with
        | Error(ProcessError.Parse(_, msg)) -> Assert.That(msg.Contains "to:", "error should point at the `to:` line")
        | other -> Assert.Fail $"expected a Parse error, got {other}"

    [<Test>]
    member _.MarkerLikeContentLineIsNotRejected() =
        // H6: a line starting with a `<<<<<<<` run that is not a `conflict N of M` header is
        // content, not a git-style-file error.
        let plain = "<<<<<<< a line documenting git markers\nmore text\n"
        let segs = parse plain

        Assert.That(
            segs
            |> List.forall (fun s ->
                match s with
                | JjConflictSegment.Text _ -> true
                | _ -> false)
        )

        Assert.That(Conflict.render segs, Is.EqualTo plain, "round-trips")

        // A real jj conflict preceded by a marker-like content line.
        let mixed =
            "<<<<<<< documentation, not a header\n<<<<<<< conflict 1 of 1\n+++++++ aaa 111 \"side-a\"\nX\n>>>>>>> conflict 1 of 1 ends\n"

        let segs = parse mixed
        Assert.That(Conflict.render segs, Is.EqualTo mixed, "round-trips")

        Assert.That(
            segs
            |> List.exists (fun s ->
                match s with
                | JjConflictSegment.Conflict _ -> true
                | _ -> false),
            "the real region still parses"
        )

    [<Test>]
    member _.RenderRoundtripsExactly() =
        for sample in [ DIFF_STYLE; SNAPSHOT_STYLE ] do
            Assert.That(Conflict.render (parse sample), Is.EqualTo sample, $"roundtrip: {sample}")

        // Conflict at EOF without a trailing newline still roundtrips.
        let trimmed = DIFF_STYLE.Substring(0, DIFF_STYLE.Length - "line 3\n".Length)
        let eof = trimmed.Substring(0, trimmed.Length - 1) // drop the end marker's final newline
        Assert.That(Conflict.render (parse eof), Is.EqualTo eof)

    [<Test>]
    member _.ResolvePicksSidesAndBase() =
        let segments = parse DIFF_STYLE
        assertResolve segments (JjResolution.Side 0) "line 1\nmain line 2\nline 3\n" "side 0"
        assertResolve segments (JjResolution.Side 1) "line 1\nfeature line 2\nline 3\n" "side 1"
        assertResolve segments JjResolution.Base "line 1\nline 2\nline 3\n" "base"
        Assert.That(Result.isError (Conflict.resolve segments (JjResolution.Side 2)))

    [<Test>]
    member _.ResolveHonorsMissingTerminatingNewline() =
        // side-a (snapshot) lacks the terminating newline; side-b via diff.
        checkEol
            ("line 1\n<<<<<<< conflict 1 of 1\n+++++++ aaa 111 \"side-a\" (no terminating newline)\nmain 2\n%%%%%%% diff from: bbb 222 \"base\"\n"
             + bs7
             + "        to: ccc 333 \"side-b\"\n-line 2\n+feat 2\n \n>>>>>>> conflict 1 of 1 ends")
            "line 1\nmain 2" // side-a: no trailing newline (was corrupted to `main 2\n`)
            "line 1\nfeat 2\n" // side-b: keeps its newline, no phantom blank line
            "line 1\nline 2\n"

        // side-b (via diff) lacks the terminating newline; side-a via diff.
        checkEol
            ("line 1\n<<<<<<< conflict 1 of 1\n%%%%%%% diff from: bbb 222 \"base\"\n"
             + bs7
             + "        to: aaa 111 \"side-a\"\n-line 2\n+main 2\n \n+++++++ ccc 333 \"side-b\" (no terminating newline)\nfeat 2\n>>>>>>> conflict 1 of 1 ends")
            "line 1\nmain 2\n"
            "line 1\nfeat 2"
            "line 1\nline 2\n"

        // base lacks the terminating newline (both sides keep theirs).
        checkEol
            ("line 1\n<<<<<<< conflict 1 of 1\n%%%%%%% diff from: bbb 222 \"base\" (no terminating newline)\n"
             + bs7
             + "        to: aaa 111 \"side-a\"\n-line 2\n+main 2\n+\n+++++++ ccc 333 \"side-b\"\nfeat 2\n\n>>>>>>> conflict 1 of 1 ends")
            "line 1\nmain 2\n"
            "line 1\nfeat 2\n"
            "line 1\nline 2" // base: no trailing newline

        // both sides lack a terminating newline (base keeps one).
        checkEol
            ("line 1\n<<<<<<< conflict 1 of 1\n%%%%%%% diff from: bbb 222 \"base\"\n"
             + bs7
             + "        to: aaa 111 \"side-a\" (no terminating newline)\n-line 2\n-\n+main 2\n+++++++ ccc 333 \"side-b\" (no terminating newline)\nfeat 2\n>>>>>>> conflict 1 of 1 ends")
            "line 1\nmain 2"
            "line 1\nfeat 2"
            "line 1\nline 2\n"

    [<Test>]
    member _.ResolveHonorsMissingNewlineWithCrlf() =
        // CRLF variant: the materializer must strip a full `\r\n` terminator — a bare-`\n` pop
        // would leave a stray `\r` and corrupt every resolution on Windows files.
        checkEol
            ("line 1\r\n<<<<<<< conflict 1 of 1\r\n+++++++ aaa 111 \"side-a\" (no terminating newline)\r\nmain 2\r\n%%%%%%% diff from: bbb 222 \"base\"\r\n"
             + bs7
             + "        to: ccc 333 \"side-b\"\r\n-line 2\r\n+feat 2\r\n \r\n>>>>>>> conflict 1 of 1 ends")
            "line 1\r\nmain 2" // side-a: no trailing newline, no stray \r
            "line 1\r\nfeat 2\r\n" // side-b: full CRLF terminator preserved
            "line 1\r\nline 2\r\n"

    [<Test>]
    member _.ResolveHandlesThreeSidedConflictWithMissingNewline() =
        let input =
            "line 1\n<<<<<<< conflict 1 of 1\n%%%%%%% diff from: aaa 111 \"base\"\n"
            + bs7
            + "        to: bbb 222 \"sa\"\n-line 2\n+AAA\n \n%%%%%%% diff from: aaa 111 \"base\"\n"
            + bs7
            + "        to: ccc 333 \"sb\"\n-line 2\n+BBB\n \n+++++++ ddd 444 \"sc\" (no terminating newline)\nCCC\n>>>>>>> conflict 1 of 1 ends"

        let segs = parse input
        Assert.That(Conflict.render segs, Is.EqualTo input, "render round-trips")
        assertResolve segs (JjResolution.Side 0) "line 1\nAAA\n" "side 0"
        assertResolve segs (JjResolution.Side 1) "line 1\nBBB\n" "side 1"
        assertResolve segs (JjResolution.Side 2) "line 1\nCCC" "side 2 (no-eol)"
        assertResolve segs JjResolution.Base "line 1\nline 2\n" "base"

    [<Test>]
    member _.ResolveHandlesSnapshotStyleWithMissingNewlineAndBase() =
        checkEol
            "line 1\n<<<<<<< conflict 1 of 1\n+++++++ aaa 111 \"sa\" (no terminating newline)\nAAA\n------- bbb 222 \"base\"\nline 2\n\n+++++++ ccc 333 \"sb\"\nBBB\n\n>>>>>>> conflict 1 of 1 ends"
            "line 1\nAAA" // sa: no trailing newline
            "line 1\nBBB\n"
            "line 1\nline 2\n"

    [<Test>]
    member _.MultiRegionCountersParse() =
        let second =
            DIFF_STYLE.Replace("conflict 1 of 1", "conflict 2 of 2").Replace("line 1\n", "").Replace("line 3\n", "")

        let two = DIFF_STYLE + "middle\n" + second
        let segments = parse two

        let counters =
            segments
            |> List.choose (fun s ->
                match s with
                | JjConflictSegment.Conflict r -> Some(r.Number, r.Total)
                | _ -> None)

        Assert.That((counters = [ (1u, 1u); (2u, 2u) ]))

    [<Test>]
    member _.GitStyleAndMalformedAreRejected() =
        let gitStyle =
            "<<<<<<< abc 123 \"side-a\"\nx\n||||||| base\ny\n=======\nz\n>>>>>>> def\n"

        match Conflict.parseConflicts gitStyle with
        | Error(ProcessError.Parse(_, msg)) ->
            Assert.That(msg.Contains "VcsToolkit.Git.Conflict", "git-style error should redirect")
        | other -> Assert.Fail $"expected a Parse error, got {other}"

        Assert.That(Result.isError (Conflict.parseConflicts "<<<<<<< conflict 1 of 1\nstray content\n"))
        Assert.That(Conflict.hasConflictMarkers DIFF_STYLE)
        Assert.That(Conflict.hasConflictMarkers gitStyle, Is.False, "git markers aren't jj's")

    [<Test>]
    member _.LongerRegionMarkersTreatShorterTerminatorAsContent() =
        // jj lengthens ALL of a file's markers together when the content contains marker-like runs,
        // so a section/end marker must match the region's opening run length. A perfectly-formed but
        // SHORTER `conflict N of M ends` line in the body is content, not an early terminator.
        let input =
            "<<<<<<<<< conflict 1 of 1\n+++++++++ side-a\n>>>>>>> conflict 1 of 1 ends\n>>>>>>>>> conflict 1 of 1 ends\n"

        let segs = parse input
        Assert.That(segs.Length, Is.EqualTo 1, "the region did not end early at the 7-long line")
        Assert.That(Conflict.render segs, Is.EqualTo input, "byte-exact roundtrip")

        Assert.That(
            (regionAt segs 0).Sides().[0]
            |> List.exists (fun l -> l.Contains "conflict 1 of 1 ends"),
            "the shorter terminator-like line is side-a content"
        )

    [<Test>]
    member _.ResolveRejectsNegativeSide() =
        // Rust's `usize` makes this impossible; F#'s `int` allows it, so the bounds guard must
        // refuse it cleanly rather than throw.
        let segs = parse DIFF_STYLE
        Assert.That(Result.isError (Conflict.resolve segs (JjResolution.Side(-1))))

    [<Test>]
    member _.DiffHeaderMissingToLineIsAParseError() =
        // A `%%%%%%%` diff header with no following `to:` line (here, at EOF) is malformed input.
        match Conflict.parseConflicts "<<<<<<< conflict 1 of 1\n%%%%%%% diff from: x\n" with
        | Error(ProcessError.Parse _) -> ()
        | other -> Assert.Fail $"expected a Parse error, got {other}"

    [<Test>]
    member _.ParseNeverThrowsAndRoundtripsAdversarialInput() =
        // Stand-in for the Rust proptest: never throw on hostile content, and whenever a file
        // parses, re-rendering is byte-exact (and the materializers don't throw).
        let samples =
            [ ""
              "\n"
              "<<<<<<< conflict 1 of 1\n>>>>>>> conflict 1 of 1 ends\n" // empty region
              "<<<<<<< not a header\n" // marker-like content
              "%%%%%%% orphan diff header\n" // section marker with no region
              "line\nmore\n" // plain text
              DIFF_STYLE
              SNAPSHOT_STYLE ]

        for s in samples do
            Conflict.hasConflictMarkers s |> ignore

            match Conflict.parseConflicts s with
            | Ok segments ->
                Assert.That(Conflict.render segments, Is.EqualTo s, $"roundtrip: {s}")
                // Exercise the materializers on whatever parsed.
                for seg in segments do
                    match seg with
                    | JjConflictSegment.Conflict r ->
                        r.Sides() |> ignore
                        r.Base() |> ignore
                    | _ -> ()
            | Error _ -> () // a malformed region is a legitimate parse error, not a throw
