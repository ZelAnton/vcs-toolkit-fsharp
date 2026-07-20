// Flat namespace-qualified module (like the sibling `VcsToolkit.Jj.ConflictTests`), so it
// never reuses another file's flat module name (FS0247) — see K-028.
module VcsToolkit.Jj.ConflictPropertyTests

open FsCheck
open FsCheck.FSharp
open NUnit.Framework
// Opened last so FsCheck.NUnit's parameterless [<Property>] shadows NUnit.Framework's own
// 2-arg PropertyAttribute — otherwise `[<Property>]` binds to the wrong type (K-028).
open FsCheck.NUnit
open VcsToolkit.Jj

// A property-based companion to the example-based `ConflictTests`: FsCheck generators build
// valid jj-materialized conflict files (native `diff`/`snapshot` styles, CRLF/LF mixes, no
// trailing newline, `(no terminating newline)` labels) plus deliberately adversarial text,
// and check the three documented invariants of `VcsToolkit.Jj.Conflict` — byte-exact
// roundtrip, resolve/side consistency, and total (never-throwing) parsing/materialization.

// ----- Generators for well-formed jj-materialized conflict files --------------------------
//
// jj keeps every marker line verbatim, so `parse -> render` is byte-exact for anything that
// parses. These generators only need to produce text that PARSES (both native styles, all
// line-ending shapes); the resolve/side properties derive their expectation from the parsed
// region's own `Sides()`/`Base()` accessors, so the generator never has to predict jj's
// missing-newline materialization arithmetic.

let private genEnding: Gen<string> = Gen.elements [ "\n"; "\r\n" ]

/// The whole-file trailing-newline choice: LF, CRLF, or none (last line ends at EOF).
let private genFinalEnding: Gen<string> = Gen.elements [ "\n"; "\r\n"; "" ]

/// Marker run lengths worth exercising — jj lengthens all of a file's markers together.
let private genMarkerLen: Gen<int> = Gen.elements [ 7; 8; 9 ]

/// Section labels, including jj's `(no terminating newline)` marker so the missing-newline
/// materialization paths (mixed-eol handling) are exercised by `Sides()`/`Base()`.
let private genLabel: Gen<string> =
    Gen.elements
        [ "aaa 111 \"side-a\""
          "bbb 222 \"base\""
          "ccc 333 \"side-b\""
          "ddd 444 \"side\" (no terminating newline)"
          "rnxsupvw 638ae425 \"base\"" ]

let private lineWith (text: string) : Gen<string * string> = Gen.map (fun e -> text, e) genEnding

/// Snapshot/base body lines: plain content that can never be a section or end marker.
let private genBodyLine: Gen<string * string> =
    gen {
        let! w = Gen.elements [ ""; "line 2"; "main line 2"; "feature line 2"; "  indented"; "content" ]
        let! e = genEnding
        return w, e
    }

/// Diff body lines: `-`/`+`/` `-prefixed (plus an occasional non-diff line the materializer
/// simply drops) — never a full marker run.
let private genDiffLine: Gen<string * string> =
    gen {
        let! w = Gen.elements [ "-line 2"; "+main line 2"; " context"; "-"; "+"; " "; "not a diff line" ]
        let! e = genEnding
        return w, e
    }

let private genSection (n: int) : Gen<(string * string) list> =
    Gen.oneof
        [ // Diff section: `%%%%%%% diff from:` then a matching `\\\\\\\ to:` line, then a body.
          gen {
              let! fromLbl = genLabel
              let! toLbl = genLabel
              let! header = lineWith (System.String('%', n) + " diff from: " + fromLbl)
              let! toLine = lineWith (System.String('\\', n) + "        to: " + toLbl)
              let! body = Gen.listOf genDiffLine
              return header :: toLine :: body
          }
          // Snapshot section: `+++++++ label` then verbatim body.
          gen {
              let! lbl = genLabel
              let! marker = lineWith (System.String('+', n) + " " + lbl)
              let! body = Gen.listOf genBodyLine
              return marker :: body
          }
          // Base section: `------- label` then verbatim body.
          gen {
              let! lbl = genLabel
              let! marker = lineWith (System.String('-', n) + " " + lbl)
              let! body = Gen.listOf genBodyLine
              return marker :: body
          } ]

let private genRegion: Gen<(string * string) list> =
    gen {
        let! n = genMarkerLen
        let! num = Gen.choose (1, 3)
        let! tot = Gen.choose (1, 3)
        // A region must open with a section marker, so always emit at least one section.
        let! first = genSection n
        let! more = Gen.listOf (genSection n)
        let! opener = lineWith (System.String('<', n) + sprintf " conflict %d of %d" num tot)
        let! ender = lineWith (System.String('>', n) + sprintf " conflict %d of %d ends" num tot)
        return (opener :: List.concat (first :: more)) @ [ ender ]
    }

let private genTextLine: Gen<string * string> =
    gen {
        let! w = Gen.elements [ "line 1"; "line 3"; "middle"; "some content"; ""; "  indented" ]
        let! e = genEnding
        return w, e
    }

let private genFileLines: Gen<(string * string) list> =
    gen {
        let! leading = Gen.listOf genTextLine

        let! blocks =
            Gen.listOf (
                gen {
                    let! r = genRegion
                    let! t = Gen.listOf genTextLine
                    return r @ t
                }
            )

        return leading @ List.concat blocks
    }

let private assembleLines (finalEnding: string) (lines: (string * string) list) : string =
    let fixedLines =
        match List.rev lines with
        | (text, _) :: restRev ->
            // A truly empty final line (no content and no ending) would vanish from the split
            // and break the roundtrip, so keep a newline there.
            let ending = if finalEnding = "" && text = "" then "\n" else finalEnding
            List.rev ((text, ending) :: restRev)
        | [] -> []

    fixedLines |> List.map (fun (t, e) -> t + e) |> String.concat ""

let private genValidJj: Gen<string> =
    gen {
        let! lines = genFileLines
        let! fe = genFinalEnding
        return assembleLines fe lines
    }

// ----- Adversarial / arbitrary input for the totality property ----------------------------

let private bs (k: int) : string = System.String('\\', k)

let private edgeChars =
    [ '<'; '%'; '+'; '-'; '>'; '\\'; '\n'; '\r'; ' '; 'c'; '7'; '\000' ]

let private charGen: Gen<char> =
    Gen.oneof [ Gen.elements edgeChars; Gen.choose (32, 126) |> Gen.map char ]

let private genGarbage: Gen<string> =
    Gen.arrayOf charGen |> Gen.map (fun a -> System.String(a))

/// jj marker-shaped fragments, concatenated in random order/quantity — the "nearly valid but
/// broken" corpus: unterminated regions, orphan section markers, a diff header with no `to:`
/// line, a mismatched-length `to:` run, a terminator-like content line, marker-like content,
/// a git-style triad (redirected), and a conflict at EOF with no trailing newline.
let private jjTokens =
    [ "<<<<<<< conflict 1 of 1\n"
      "<<<<<<< conflict 2 of 3\n"
      "<<<<<<< not a header\n"
      "%%%%%%% diff from: x\n"
      bs 7 + "        to: y\n"
      bs 8 + "        to: z\n"
      "+++++++ side\n"
      "------- base\n"
      ">>>>>>> conflict 1 of 1 ends\n"
      ">>>>>>> recommends\n"
      "-line 2\n"
      "+main 2\n"
      " context\n"
      "stray content\n"
      "line 1\n"
      "line 3\r\n"
      "\n"
      "%%%%%%% orphan diff header\n"
      "<<<<<<< conflict 1 of 1\r\n"
      ">>>>>>> conflict 1 of 1 ends"
      System.String('<', 9) + " conflict 1 of 1\n"
      System.String('>', 9) + " conflict 1 of 1 ends\n"
      "<<<<<<< abc 123 \"side-a\"\nx\n||||||| base\ny\n=======\nz\n>>>>>>> def\n" ]

let private genAdversarial: Gen<string> =
    Gen.listOf (Gen.elements jjTokens) |> Gen.map (String.concat "")

let private genAnyText: Gen<string> = Gen.oneof [ genGarbage; genAdversarial ]

// ----- Resolve consistency, derived from the parsed regions -------------------------------

/// The documented resolve invariant recomputed from the *parsed* regions: concatenating each
/// text run and each region's chosen side (`Sides().[idx]`) must equal `resolve (Side idx)`.
/// `None` means some region lacks that side, so `resolve` must error.
let private refResolveSide (segments: JjConflictSegment list) (idx: int) : string option =
    let parts =
        segments
        |> List.map (fun seg ->
            match seg with
            | JjConflictSegment.Text lines -> Some(String.concat "" lines)
            | JjConflictSegment.Conflict region ->
                let sides = region.Sides()

                if idx >= 0 && idx < sides.Length then
                    Some(String.concat "" sides.[idx])
                else
                    None)

    if List.forall Option.isSome parts then
        Some(parts |> List.map Option.get |> String.concat "")
    else
        None

let private refResolveBase (segments: JjConflictSegment list) : string option =
    let parts =
        segments
        |> List.map (fun seg ->
            match seg with
            | JjConflictSegment.Text lines -> Some(String.concat "" lines)
            | JjConflictSegment.Conflict region -> region.Base() |> Option.map (String.concat ""))

    if List.forall Option.isSome parts then
        Some(parts |> List.map Option.get |> String.concat "")
    else
        None

let private sideConsistent (segments: JjConflictSegment list) (idx: int) : bool =
    match refResolveSide segments idx, Conflict.resolve segments (JjResolution.Side idx) with
    | Some expected, Ok actual -> actual = expected
    | None, Error _ -> true
    | _ -> false

let private baseConsistent (segments: JjConflictSegment list) : bool =
    match refResolveBase segments, Conflict.resolve segments JjResolution.Base with
    | Some expected, Ok actual -> actual = expected
    | None, Error _ -> true
    | _ -> false

// ----- Properties -------------------------------------------------------------------------

[<TestFixture>]
type JjConflictPropertyTests() =

    /// Roundtrip: `render (parseConflicts s) = s` byte-for-byte for freely generated valid
    /// jj conflict files (diff/snapshot sections, widened markers, CRLF/LF mixes, no trailing
    /// newline, `(no terminating newline)` labels).
    [<Property>]
    member _.RenderRoundtripsValidConflicts() =
        Prop.forAll (Arb.fromGen genValidJj) (fun s ->
            match Conflict.parseConflicts s with
            | Ok segments -> Conflict.render segments = s
            | Error _ -> false)

    /// Resolve consistency: resolving to a side or the base yields exactly the concatenation
    /// of the surrounding text and the region's materialized `Sides()`/`Base()`, and errors
    /// exactly when a region lacks the requested side/base.
    [<Property>]
    member _.ResolveMatchesParsedRegions() =
        Prop.forAll (Arb.fromGen genValidJj) (fun s ->
            match Conflict.parseConflicts s with
            | Error _ -> false
            | Ok segments ->
                sideConsistent segments 0
                && sideConsistent segments 1
                && sideConsistent segments 2
                && baseConsistent segments)

    /// Totality: `parseConflicts` never throws on arbitrary (including deliberately broken,
    /// nested, mismatched, git-style) input — it returns `Ok` or a structural `Error`.
    /// Whenever it parses, the materializers never throw, re-rendering is byte-exact, and
    /// `resolve` stays consistent with the regions.
    [<Property>]
    member _.ParseIsTotalOnArbitraryInput() =
        Prop.forAll (Arb.fromGen genAnyText) (fun s ->
            Conflict.hasConflictMarkers s |> ignore

            match Conflict.parseConflicts s with
            | Error _ -> true
            | Ok segments ->
                // The materializers must not throw on whatever parsed.
                for seg in segments do
                    match seg with
                    | JjConflictSegment.Conflict region ->
                        region.Sides() |> ignore
                        region.Base() |> ignore
                    | JjConflictSegment.Text _ -> ()

                Conflict.render segments = s
                && sideConsistent segments 0
                && baseConsistent segments)
