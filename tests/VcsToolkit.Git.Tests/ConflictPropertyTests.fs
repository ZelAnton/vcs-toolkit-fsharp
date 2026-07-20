// Flat namespace-qualified module (like the sibling `VcsToolkit.Git.ConflictTests`), so it
// never reuses another file's flat module name (FS0247) — see K-028.
module VcsToolkit.Git.ConflictPropertyTests

open FsCheck
open FsCheck.FSharp
open NUnit.Framework
// Opened last so FsCheck.NUnit's parameterless [<Property>] shadows NUnit.Framework's own
// 2-arg PropertyAttribute — otherwise `[<Property>]` binds to the wrong type (K-028).
open FsCheck.NUnit
open VcsToolkit.Git

// A property-based companion to the example-based `ConflictTests`: FsCheck generators build
// valid conflict files (all marker styles, CRLF/LF mixes, no trailing newline, repeated
// base-marker content) plus deliberately adversarial text, and check the three documented
// invariants of `VcsToolkit.Git.Conflict` — byte-exact roundtrip, resolve/side consistency,
// and total (never-throwing) parsing.

// ----- Model of a generated conflict file -------------------------------------------------

/// Which resolution a physical line contributes to, so a generated file's expected `resolve`
/// output can be recomputed by filtering. `Marker` lines are emitted by `render` but by no
/// `resolve` (a resolution drops the markers and keeps one side's content).
type private Role =
    | InText
    | InOurs
    | InBase
    | InTheirs
    | Marker

/// One generated physical line: its bytes split into content and a chosen line ending, plus
/// the resolution role. Keeping the ending separate lets the file's final line drop it (a
/// conflict/text at EOF with no trailing newline) without disturbing any earlier line.
type private PLine =
    { Text: string
      Ending: string
      Role: Role }

/// A file is a sequence of plain-text runs and conflict regions; a conflict remembers whether
/// it records a base (diff3/zdiff3), which decides whether `resolve Base` must succeed.
type private Block =
    | TextBlock of PLine list
    | ConflictBlock of hasBase: bool * lines: PLine list

// ----- Generators -------------------------------------------------------------------------

let private genEnding: Gen<string> = Gen.elements [ "\n"; "\r\n" ]

/// The whole-file trailing-newline choice: LF, CRLF, or none (last line ends at EOF).
let private genFinalEnding: Gen<string> = Gen.elements [ "\n"; "\r\n"; "" ]

/// Marker run lengths worth exercising: the default 7 and a couple of widened
/// `merge.conflictMarkerSize` values.
let private genMarkerLen: Gen<int> = Gen.elements [ 7; 8; 15 ]

let private genLabel: Gen<string> =
    Gen.elements [ ""; "HEAD"; "feature"; "0b025ce"; "topic branch" ]

/// A content word that can never be mistaken for a marker: it never begins with a
/// `<`/`|`/`=`/`>` run, and never contains a newline (which would split it into two lines).
let private genWord: Gen<string> =
    Gen.elements
        [ ""
          "alpha"
          "line 2"
          "main line 2"
          "feature line 2"
          "  indented"
          "tab\there"
          "unicode é 中" ]

let private markerLine (ch: char) (n: int) (label: string) (ending: string) (role: Role) : PLine =
    let labelPart = if label = "" then "" else " " + label

    { Text = System.String(ch, n) + labelPart
      Ending = ending
      Role = role }

let private genContentLine (role: Role) : Gen<PLine> =
    gen {
        let! w = genWord
        let! e = genEnding
        return { Text = w; Ending = e; Role = role }
    }

let private genContentLines (role: Role) : Gen<PLine list> = Gen.listOf (genContentLine role)

let private genTextBlock: Gen<Block> = Gen.map TextBlock (genContentLines InText)

let private genConflictBlock: Gen<Block> =
    gen {
        let! n = genMarkerLen
        let! oursLabel = genLabel
        let! baseLabel = genLabel
        let! theirsLabel = genLabel
        let! hasBase = Gen.elements [ true; false ]
        let! oursLines = genContentLines InOurs
        let! baseBody = genContentLines InBase
        let! theirsLines = genContentLines InTheirs
        let! eOurs = genEnding
        let! eBase = genEnding
        let! eSep = genEnding
        let! eEnd = genEnding
        let! injectRepeatedBase = Gen.elements [ true; false ]

        // A SECOND `|`-run line inside the base is base *content*, not a replacement base
        // marker (the RepeatedBaseMarker edge case) — inject one to cover it byte-exactly.
        let baseLines =
            if injectRepeatedBase then
                { Text = System.String('|', n) + " still base"
                  Ending = eBase
                  Role = InBase }
                :: baseBody
            else
                baseBody

        let opener = markerLine '<' n oursLabel eOurs Marker
        let sep = markerLine '=' n "" eSep Marker
        let ender = markerLine '>' n theirsLabel eEnd Marker

        let lines =
            if hasBase then
                let baseMarker = markerLine '|' n baseLabel eBase Marker
                [ opener ] @ oursLines @ [ baseMarker ] @ baseLines @ [ sep ] @ theirsLines @ [ ender ]
            else
                [ opener ] @ oursLines @ [ sep ] @ theirsLines @ [ ender ]

        return ConflictBlock(hasBase, lines)
    }

let private genFile: Gen<Block list> =
    gen {
        let! leading = genTextBlock

        let! rest =
            Gen.listOf (
                gen {
                    let! c = genConflictBlock
                    let! t = genTextBlock
                    return [ c; t ]
                }
            )

        return leading :: List.concat rest
    }

// ----- Assembling a generated file into bytes + expected resolutions ----------------------

/// A generated file's bytes plus the three expected `resolve` outputs. `Base` is
/// `Some expected` only when every region records a base (so `resolve Base` succeeds);
/// `None` means `resolve Base` must error (a 2-way `merge` region is present).
type private Assembled =
    { Text: string
      Ours: string
      Theirs: string
      Base: string option }

let private renderLines (lines: PLine list) : string =
    lines |> List.map (fun p -> p.Text + p.Ending) |> String.concat ""

let private applyFinalEnding (finalEnding: string) (lines: PLine list) : PLine list =
    match List.rev lines with
    | last :: restRev ->
        // A truly empty final line (no content and no ending) would vanish from the split and
        // break the roundtrip, so keep a newline there.
        let ending =
            if finalEnding = "" && last.Text = "" then "\n" else finalEnding

        List.rev ({ last with Ending = ending } :: restRev)
    | [] -> []

let private assemble (finalEnding: string) (blocks: Block list) : Assembled =
    let lines =
        blocks
        |> List.collect (function
            | TextBlock ls -> ls
            | ConflictBlock(_, ls) -> ls)
        |> applyFinalEnding finalEnding

    let pick roles =
        lines |> List.filter (fun p -> List.contains p.Role roles) |> renderLines

    let allBase =
        blocks
        |> List.forall (function
            | ConflictBlock(hasBase, _) -> hasBase
            | TextBlock _ -> true)

    { Text = renderLines lines
      Ours = pick [ InText; InOurs ]
      Theirs = pick [ InText; InTheirs ]
      Base = if allBase then Some(pick [ InText; InBase ]) else None }

let private genAssembled: Gen<Assembled> =
    gen {
        let! blocks = genFile
        let! fe = genFinalEnding
        return assemble fe blocks
    }

// ----- Adversarial / arbitrary input for the totality property ----------------------------

let private edgeChars =
    [ '<'; '|'; '='; '>'; '\n'; '\r'; ' '; 'a'; 'H'; '\t'; '\000'; '7' ]

let private charGen: Gen<char> =
    Gen.oneof [ Gen.elements edgeChars; Gen.choose (32, 126) |> Gen.map char ]

let private genGarbage: Gen<string> =
    Gen.arrayOf charGen |> Gen.map (fun a -> System.String(a))

/// Marker-shaped fragments, concatenated in random order/quantity — the "nearly valid but
/// broken/nested/mismatched" corpus (unterminated regions, mismatched run lengths, a `|`-run
/// with no opener, marker-like content mid-line, a conflict at EOF with no trailing newline).
let private markerTokens =
    [ "<<<<<<< HEAD\n"
      "<<<<<<<\n"
      "||||||| base\n"
      "|||||||\n"
      "=======\n"
      ">>>>>>> b\n"
      ">>>>>>>\n"
      "ours\n"
      "theirs\n"
      "base line\n"
      "text\r\n"
      "<<<<<<<<<<<<<<< HEAD\n"
      "===============\n"
      ">>>>>>>>>>>>>>> b\n"
      "<<<<<<< a\n"
      "========\n"
      ">>>>>>>>\n"
      "text <<<<<<< inline\n"
      "\n"
      "no trailing newline" ]

let private genAdversarial: Gen<string> =
    Gen.listOf (Gen.elements markerTokens) |> Gen.map (String.concat "")

let private genAnyText: Gen<string> = Gen.oneof [ genGarbage; genAdversarial ]

/// The documented resolve invariant recomputed from the *parsed* regions: concatenating each
/// text run and each region's chosen side must equal `resolve`. `None` means a region lacks
/// the requested side (a 2-way region has no base), so `resolve` must error.
let private refResolve (segments: ConflictSegment list) (side: ResolutionSide) : string option =
    let parts =
        segments
        |> List.map (fun seg ->
            match seg with
            | ConflictSegment.Text lines -> Some(String.concat "" lines)
            | ConflictSegment.Conflict region ->
                let chosen =
                    match side with
                    | ResolutionSide.Ours -> Some region.Ours
                    | ResolutionSide.Theirs -> Some region.Theirs
                    | ResolutionSide.Base -> region.Base

                chosen |> Option.map (String.concat ""))

    if List.forall Option.isSome parts then
        Some(parts |> List.map Option.get |> String.concat "")
    else
        None

let private resolveMatches (segments: ConflictSegment list) (side: ResolutionSide) : bool =
    match refResolve segments side, Conflict.resolve segments side with
    | Some expected, Ok actual -> actual = expected
    | None, Error _ -> true
    | _ -> false

// ----- Properties -------------------------------------------------------------------------

[<TestFixture>]
type GitConflictPropertyTests() =

    /// Roundtrip: `render (parseConflicts s) = s` byte-for-byte for freely generated valid
    /// conflict files (merge/diff3, widened markers, CRLF/LF mixes, no trailing newline,
    /// repeated base-marker content).
    [<Property>]
    member _.RenderRoundtripsValidConflicts() =
        Prop.forAll (Arb.fromGen genAssembled) (fun a ->
            match Conflict.parseConflicts a.Text with
            | Ok segments -> Conflict.render segments = a.Text
            | Error _ -> false)

    /// Resolve consistency: resolving to a side yields exactly the concatenation of the
    /// surrounding text and that side's regions — and `Base` errors iff a 2-way region exists.
    [<Property>]
    member _.ResolveMatchesGeneratedSides() =
        Prop.forAll (Arb.fromGen genAssembled) (fun a ->
            match Conflict.parseConflicts a.Text with
            | Error _ -> false
            | Ok segments ->
                let ours =
                    match Conflict.resolve segments ResolutionSide.Ours with
                    | Ok s -> s = a.Ours
                    | Error _ -> false

                let theirs =
                    match Conflict.resolve segments ResolutionSide.Theirs with
                    | Ok s -> s = a.Theirs
                    | Error _ -> false

                let baseSide =
                    match a.Base, Conflict.resolve segments ResolutionSide.Base with
                    | Some expected, Ok s -> s = expected
                    | None, Error _ -> true
                    | _ -> false

                ours && theirs && baseSide)

    /// Totality: `parseConflicts` never throws on arbitrary (including deliberately broken,
    /// nested, mismatched) input — it returns `Ok` or a structural `Error`. Whenever it
    /// parses, re-rendering is byte-exact and `resolve` stays consistent with the regions.
    [<Property>]
    member _.ParseIsTotalOnArbitraryInput() =
        Prop.forAll (Arb.fromGen genAnyText) (fun s ->
            Conflict.hasConflictMarkers s |> ignore

            match Conflict.parseConflicts s with
            | Error _ -> true
            | Ok segments ->
                Conflict.render segments = s
                && resolveMatches segments ResolutionSide.Ours
                && resolveMatches segments ResolutionSide.Theirs
                && resolveMatches segments ResolutionSide.Base)
