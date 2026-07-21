namespace VcsToolkit.Git

// Typed model of git conflict markers — parse a conflicted file's *content* into structured
// regions and write a chosen resolution back. Pure functions (no subprocess), so everything here
// is hermetic. Ported from the Rust `vcs_git::conflict`.
//
// Handles git's three `merge.conflictStyle`s with one grammar: `merge` (2-way: ours/theirs),
// `diff3` (3-way: ours/base/theirs), and `zdiff3` (same markers as diff3). Marker length is
// variable (`merge.conflictMarkerSize`, default 7) and is detected per region. Lines are kept
// verbatim (including `\r\n` and a missing trailing newline), so `render` is a byte-exact roundtrip.

open System
open System.Text
open ProcessKit
open VcsToolkit.Diff

/// Which side of a conflict a resolution keeps.
[<RequireQualifiedAccess>]
type ResolutionSide =
    /// The `<<<<<<<` side (typically `HEAD`).
    | Ours
    /// The `|||||||` base (diff3/zdiff3 only).
    | Base
    /// The `>>>>>>>` side (the merged-in branch).
    | Theirs

/// One conflicted region: the lines of each side plus the verbatim marker lines (kept so
/// rendering is byte-exact). All line lists store lines **with** their original endings; the last
/// line of a file may have none.
[<Sealed>]
type ConflictRegion
    internal
    (
        oursLabel: string,
        baseLabel: string option,
        theirsLabel: string,
        ours: string list,
        baseLines: string list option,
        theirs: string list,
        markerLen: int,
        markerOurs: string,
        markerBase: string option,
        markerSep: string,
        markerEnd: string
    ) =

    /// Label after the `<<<<<<<` marker (e.g. `HEAD`); empty when absent.
    member _.OursLabel = oursLabel
    /// Label after the `|||||||` marker; `None` for 2-way conflicts.
    member _.BaseLabel = baseLabel
    /// Label after the `>>>>>>>` marker (e.g. the branch name).
    member _.TheirsLabel = theirsLabel
    /// The `<<<<<<<`-side lines (verbatim, endings included).
    member _.Ours = ours
    /// The base lines (`diff3`/`zdiff3`); `None` for 2-way conflicts.
    member _.Base = baseLines
    /// The `>>>>>>>`-side lines (verbatim, endings included).
    member _.Theirs = theirs
    /// The marker run length (7 unless `merge.conflictMarkerSize` raised it).
    member _.MarkerLen = markerLen

    // Verbatim marker lines, for byte-exact rendering (internal render state).
    member internal _.MarkerOurs = markerOurs
    member internal _.MarkerBase = markerBase
    member internal _.MarkerSep = markerSep
    member internal _.MarkerEnd = markerEnd

/// A conflicted file as a sequence of plain-text runs and conflict regions — the shape that keeps
/// `render` a byte-exact roundtrip.
[<RequireQualifiedAccess>]
type ConflictSegment =
    /// Lines outside any conflict (verbatim, endings included).
    | Text of string list
    /// One conflicted region.
    | Conflict of ConflictRegion

/// Pure git conflict-marker parsing, rendering, and resolution. No subprocess.
[<RequireQualifiedAccess>]
module Conflict =

    /// The length of the leading `ch` run when `line` is a marker line for it: the run must be
    /// followed by a space + label, or end the line. `None` otherwise.
    let private markerRun (line: string) (ch: char) : int option =
        let trimmed = line.TrimEnd('\r', '\n')
        let n = trimmed |> Seq.takeWhile (fun c -> c = ch) |> Seq.length

        if n = 0 then
            None
        else
            let rest = trimmed.Substring n

            if rest.Length = 0 || rest.StartsWith(' ') then
                Some n
            else
                None

    /// The label after an `n`-char marker run (empty when none).
    let private markerLabel (line: string) (n: int) : string =
        (line.TrimEnd('\r', '\n')).Substring(n).TrimStart()

    let private parseError (message: string) : ProcessError = ProcessError.Parse(BINARY, message)

    /// Does `content` contain a line that looks like a conflict-start marker? A cheap pre-check
    /// before a full `parseConflicts`.
    let hasConflictMarkers (content: string) : bool =
        TextParse.splitInclusive content
        |> List.exists (fun line ->
            match markerRun line '<' with
            | Some n -> n >= 7
            | None -> false)

    /// Parse a conflicted file's content into text/conflict segments.
    ///
    /// Errors with `ProcessError.Parse` only on a genuinely malformed *region*: a `<<<<<<<`-opened
    /// region missing its `=======` separator or `>>>>>>>` terminator. A `=======`/`>>>>>>>` run
    /// **outside** any region is treated as ordinary content (a Markdown/RST underline, a divider,
    /// a quoted email), so a file with no real conflict — or a real conflict alongside marker-like
    /// content — parses cleanly.
    let parseConflicts (content: string) : Result<ConflictSegment list, ProcessError> =
        // `List.toArray` for the O(1) indexed `lines.[i]` random access the parse loop below needs
        // (a `string list` would make it O(n²)); the split itself is the shared `TextParse` one.
        let lines = TextParse.splitInclusive content |> List.toArray
        let segments = ResizeArray<ConflictSegment>()
        let text = ResizeArray<string>()
        let mutable i = 0
        let mutable error: ProcessError option = None

        while i < lines.Length && error.IsNone do
            let line = lines.[i]
            i <- i + 1
            // A region starts at a `<<<<<<<`-run of length >= 7. A `=======`/`>>>>>>>` run OUTSIDE a
            // region is ordinary content (a setext underline, a divider banner, a deep email quote)
            // — kept verbatim as text. A genuinely broken region (an opener with no separator or
            // terminator) is still caught inside the loops below.
            match markerRun line '<' with
            | Some n when n >= 7 ->
                if text.Count > 0 then
                    segments.Add(ConflictSegment.Text(List.ofSeq text))
                    text.Clear()

                let markerOurs = line
                let oursLabel = markerLabel line n
                let ours = ResizeArray<string>()
                let mutable baseLines: ResizeArray<string> option = None
                let mutable markerBase: string option = None
                let mutable baseLabel: string option = None
                let mutable markerSep: string option = None

                // Ours, until the base marker (diff3) or the separator.
                while markerSep.IsNone && error.IsNone do
                    if i >= lines.Length then
                        error <-
                            Some(
                                parseError (
                                    sprintf "unterminated conflict (no ======= after \"%s\")" (markerOurs.TrimEnd())
                                )
                            )
                    else
                        let l = lines.[i]
                        i <- i + 1

                        // Only the FIRST `|`-run is the diff3 base marker; a later matching line is
                        // base *content* (a region has exactly one base marker — a repeated one used
                        // to overwrite it and lose a line on render).
                        if baseLines.IsNone && markerRun l '|' = Some n then
                            baseLabel <- Some(markerLabel l n)
                            markerBase <- Some l
                            baseLines <- Some(ResizeArray<string>())
                        elif markerRun l '=' = Some n then
                            markerSep <- Some l
                        else
                            match baseLines with
                            | Some bl -> bl.Add l
                            | None -> ours.Add l

                if error.IsNone then
                    // Theirs, until the end marker.
                    let theirs = ResizeArray<string>()
                    let mutable markerEnd: string option = None

                    while markerEnd.IsNone && error.IsNone do
                        if i >= lines.Length then
                            error <-
                                Some(
                                    parseError (
                                        sprintf "unterminated conflict (no >>>>>>> after \"%s\")" (markerOurs.TrimEnd())
                                    )
                                )
                        else
                            let l = lines.[i]
                            i <- i + 1

                            if markerRun l '>' = Some n then
                                markerEnd <- Some l
                            else
                                theirs.Add l

                    match markerEnd, markerSep with
                    | Some endMarker, Some markerSepLine ->
                        let theirsLabel = markerLabel endMarker n

                        segments.Add(
                            ConflictSegment.Conflict(
                                ConflictRegion(
                                    oursLabel,
                                    baseLabel,
                                    theirsLabel,
                                    List.ofSeq ours,
                                    (baseLines |> Option.map List.ofSeq),
                                    List.ofSeq theirs,
                                    n,
                                    markerOurs,
                                    markerBase,
                                    markerSepLine,
                                    endMarker
                                )
                            )
                        )
                    | _ -> ()
            // markerEnd None: an error was recorded above. Some-markerEnd with a
            // None markerSep is unreachable — the loop above exits only with markerSep set.
            | _ -> text.Add line

        match error with
        | Some e -> Error e
        | None ->
            if text.Count > 0 then
                segments.Add(ConflictSegment.Text(List.ofSeq text))

            Ok(List.ofSeq segments)

    /// Re-render segments verbatim — the byte-exact inverse of `parseConflicts`.
    let render (segments: ConflictSegment list) : string =
        let out = StringBuilder()

        for segment in segments do
            match segment with
            | ConflictSegment.Text lines -> lines |> List.iter (fun l -> out.Append l |> ignore)
            | ConflictSegment.Conflict region ->
                out.Append region.MarkerOurs |> ignore
                region.Ours |> List.iter (fun l -> out.Append l |> ignore)

                match region.MarkerBase with
                | Some marker ->
                    out.Append marker |> ignore

                    match region.Base with
                    | Some baseLines -> baseLines |> List.iter (fun l -> out.Append l |> ignore)
                    | None -> ()
                | None -> ()

                out.Append region.MarkerSep |> ignore
                region.Theirs |> List.iter (fun l -> out.Append l |> ignore)
                out.Append region.MarkerEnd |> ignore

        out.ToString()

    /// Produce the file content with every conflict resolved to `side`. Errors with a clear
    /// message when `side` is `Base` and a region records none (2-way `merge` style).
    let resolve (segments: ConflictSegment list) (side: ResolutionSide) : Result<string, ProcessError> =
        let out = StringBuilder()
        let mutable error: ProcessError option = None

        for segment in segments do
            if error.IsNone then
                match segment with
                | ConflictSegment.Text lines -> lines |> List.iter (fun l -> out.Append l |> ignore)
                | ConflictSegment.Conflict region ->
                    let chosen =
                        match side with
                        | ResolutionSide.Ours -> Some region.Ours
                        | ResolutionSide.Theirs -> Some region.Theirs
                        | ResolutionSide.Base -> region.Base

                    match chosen with
                    | Some lines -> lines |> List.iter (fun l -> out.Append l |> ignore)
                    | None ->
                        error <-
                            Some(
                                ProcessError.Spawn(
                                    BINARY,
                                    "cannot resolve to Base: this conflict records no base (2-way `merge` style; use diff3/zdiff3)"
                                )
                            )

        match error with
        | Some e -> Error e
        | None -> Ok(out.ToString())
