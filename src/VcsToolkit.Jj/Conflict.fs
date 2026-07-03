namespace VcsToolkit.Jj

// Typed model of jj's **materialized** conflict markers — parse a conflicted file's content into
// structured regions and write a chosen resolution back. Pure functions (no subprocess), so
// everything here is hermetic. Ported from the Rust `vcs_jj::conflict`.
//
// Covers jj's native styles (`ui.conflict-marker-style`): `diff` (the 0.38 default — one side as a
// unified diff against the base) and `snapshot` (every side and the base verbatim). Files
// materialized with the `git` style use git's grammar — parse those with `VcsToolkit.Git.Conflict`
// (a documented asymmetry, not an oversight). Lines are kept verbatim (including `\r\n` and a
// missing trailing newline), so `render` is a byte-exact roundtrip.

open System
open System.Text
open ProcessKit

/// Pure helpers shared by the conflict types and the `Conflict` module.
[<AutoOpen>]
module private JjConflictInternal =

    /// jj's marker for a side whose content does not end in a newline; appended to the label.
    [<Literal>]
    let NO_EOL_MARKER = "(no terminating newline)"

    /// Split `s` into lines that each KEEP their trailing `\n` (Rust `str::split_inclusive`):
    /// `"a\nb"` → `["a\n"; "b"]`, `"a\n"` → `["a\n"]`, `""` → `[]`.
    let splitInclusive (s: string) : string list =
        if s = "" then
            []
        else
            let result = ResizeArray<string>()
            let mutable start = 0

            for i in 0 .. s.Length - 1 do
                if s.[i] = '\n' then
                    result.Add(s.Substring(start, i - start + 1))
                    start <- i + 1

            if start < s.Length then
                result.Add(s.Substring start)

            List.ofSeq result

    /// Whether a section label carries jj's no-terminating-newline marker.
    let labeledNoEol (label: string) : bool =
        (label.TrimEnd()).EndsWith(NO_EOL_MARKER, StringComparison.Ordinal)

    /// Strip all consecutive leading `prefix`es from `s` (Rust `str::trim_start_matches`).
    let rec trimStartMatches (prefix: string) (s: string) : string =
        if prefix <> "" && s.StartsWith(prefix, StringComparison.Ordinal) then
            trimStartMatches prefix (s.Substring prefix.Length)
        else
            s

    /// Strip all consecutive trailing `suffix`es from `s` (Rust `str::trim_end_matches`).
    let rec trimEndMatches (suffix: string) (s: string) : string =
        if suffix <> "" && s.EndsWith(suffix, StringComparison.Ordinal) then
            trimEndMatches suffix (s.Substring(0, s.Length - suffix.Length))
        else
            s

    /// Materialize a diff section: `old = true` keeps `-`/` ` lines (the base), `old = false`
    /// keeps `+`/` ` lines (the side), stripping the prefix char but preserving the line ending.
    let applyDiff (lines: string list) (old: bool) : string list =
        let keep = if old then [| '-'; ' ' |] else [| '+'; ' ' |]

        lines
        |> List.choose (fun line ->
            if line.Length = 0 then
                None
            elif Array.contains line.[0] keep then
                Some(line.Substring 1)
            else
                None)

    /// Re-join a section's rendered sub-lines (already prefix-stripped for a diff) into the
    /// side/base's true bytes, undoing jj's trailing-newline encoding.
    ///
    /// - `noEol` — the role's label carries the no-terminating-newline marker: the last sub-line's
    ///   `\n` is jj's display artifact; drop it.
    /// - `mixed` — the region is in the explicit trailing-newline mode (`mixedEol`): a newline-
    ///   terminated side carries one extra trailing empty sub-line marking "has newline"; drop it.
    ///
    /// jj emits that terminator with the file's own ending, so strip a full `\r\n` on a CRLF file
    /// (a bare `\n` pop would leave a stray `\r` and silently corrupt the resolved bytes — C3).
    let joinSublines (sublines: string list) (noEol: bool) (mixed: bool) : string list =
        let content = String.concat "" sublines

        let content =
            if noEol || mixed then
                if content.EndsWith("\r\n", StringComparison.Ordinal) then
                    content.Substring(0, content.Length - 2)
                elif content.EndsWith("\n", StringComparison.Ordinal) then
                    content.Substring(0, content.Length - 1)
                else
                    content
            else
                content

        splitInclusive content

    /// The marker run length when `line` starts with a run of `ch` (>= 7) followed by a space or
    /// line end. `None` otherwise.
    let markerRun (line: string) (ch: char) : int option =
        let trimmed = line.TrimEnd('\r', '\n')
        let n = trimmed |> Seq.takeWhile (fun c -> c = ch) |> Seq.length
        let rest = trimmed.Substring n

        if n >= 7 && (rest = "" || rest.StartsWith(' ')) then
            Some n
        else
            None

    /// The label after an `n`-char marker run (empty when none).
    let markerLabel (line: string) (n: int) : string =
        (line.TrimEnd('\r', '\n')).Substring(n).TrimStart()

    /// Parse a `conflict N of M` header into `(N, M)`.
    let parseCounter (label: string) : (uint32 * uint32) option =
        if not (label.StartsWith("conflict ", StringComparison.Ordinal)) then
            None
        else
            let rest = label.Substring("conflict ".Length)

            let parts =
                rest.Split([| ' '; '\t'; '\n'; '\r'; '\f'; '\v' |], StringSplitOptions.RemoveEmptyEntries)

            if parts.Length >= 3 && parts.[1] = "of" then
                match
                    UInt32.TryParse(
                        parts.[0],
                        Globalization.NumberStyles.None,
                        Globalization.CultureInfo.InvariantCulture
                    ),
                    UInt32.TryParse(
                        parts.[2],
                        Globalization.NumberStyles.None,
                        Globalization.CultureInfo.InvariantCulture
                    )
                with
                | (true, n), (true, m) -> Some(n, m)
                | _ -> None
            else
                None

/// One section inside a jj conflict region. jj may add marker styles over time, so treat this as
/// potentially extensible (the Rust model marks it `#[non_exhaustive]`) — add a `| _ ->` arm when
/// pattern-matching so a future section kind doesn't break your code.
[<RequireQualifiedAccess>]
type JjConflictSection =
    /// A `%%%%%%%` section: one side as a unified diff from the base (`-`/`+`/` `-prefixed lines).
    /// The side's content is the diff's *new* text; the base is its *old* text.
    | Diff of fromLabel: string * toLabel: string * lines: string list
    /// A `+++++++` section: one side's content, verbatim.
    | Snapshot of label: string * lines: string list
    /// A `-------` section (snapshot style): the base's content, verbatim.
    | Base of label: string * lines: string list

/// One materialized jj conflict region (`<<<<<<< conflict N of M` … `>>>>>>> … ends`).
[<Sealed>]
type JjConflictRegion
    internal
    (
        number: uint32,
        total: uint32,
        sections: JjConflictSection list,
        markerStart: string,
        markerEnd: string,
        sectionMarkers: string list
    ) =

    /// This region's number within the file (the `N` of `conflict N of M`).
    member _.Number = number
    /// The file's total conflict count (the `M`).
    member _.Total = total
    /// The region's sections, in file order.
    member _.Sections = sections

    // Verbatim marker lines for byte-exact rendering (internal render state).
    member internal _.MarkerStart = markerStart
    member internal _.MarkerEnd = markerEnd
    member internal _.SectionMarkers = sectionMarkers

    /// Whether jj rendered this region in its explicit trailing-newline mode — true as soon as any
    /// side (or base) lacks a terminating newline, which changes the whole region's representation.
    member private _.MixedEol() : bool =
        sections
        |> List.exists (fun section ->
            match section with
            | JjConflictSection.Snapshot(label, _)
            | JjConflictSection.Base(label, _) -> labeledNoEol label
            | JjConflictSection.Diff(fromLabel, toLabel, _) -> labeledNoEol fromLabel || labeledNoEol toLabel)

    /// The materialized content of each *side*, in file order (a diff section contributes its new
    /// text; base sections are not sides). Each side is newline-terminated lines whose
    /// concatenation is the side's exact bytes — including a **missing** terminating newline.
    member this.Sides() : string list list =
        let mixed = this.MixedEol()

        sections
        |> List.choose (fun section ->
            match section with
            | JjConflictSection.Diff(_, toLabel, lines) ->
                Some(joinSublines (applyDiff lines false) (labeledNoEol toLabel) mixed)
            | JjConflictSection.Snapshot(label, lines) -> Some(joinSublines lines (labeledNoEol label) mixed)
            | JjConflictSection.Base _ -> None)

    /// The base content, when the region records one (a diff section's old text, or a snapshot-
    /// style `-------` section). Honors a missing terminating newline the same way `Sides` does.
    member this.Base() : string list option =
        let mixed = this.MixedEol()

        sections
        |> List.tryPick (fun section ->
            match section with
            | JjConflictSection.Diff(fromLabel, _, lines) ->
                Some(joinSublines (applyDiff lines true) (labeledNoEol fromLabel) mixed)
            | JjConflictSection.Base(label, lines) -> Some(joinSublines lines (labeledNoEol label) mixed)
            | JjConflictSection.Snapshot _ -> None)

/// A conflicted file as a sequence of plain-text runs and conflict regions.
[<RequireQualifiedAccess>]
type JjConflictSegment =
    /// Lines outside any conflict (verbatim).
    | Text of string list
    /// One materialized conflict region.
    | Conflict of JjConflictRegion

/// What `resolve` keeps in place of each conflict region.
[<RequireQualifiedAccess>]
type JjResolution =
    /// The N-th side (0-based, file order) — `Side 0` is the first side.
    | Side of int
    /// The recorded base.
    | Base

/// Pure jj conflict-marker parsing, rendering, and resolution. No subprocess.
[<RequireQualifiedAccess>]
module Conflict =

    // A mutable section accumulator used while parsing (the DU sections are immutable).
    type private SectionBuilder =
        | DiffB of fromLabel: string * toLabel: string * lines: ResizeArray<string>
        | SnapshotB of label: string * lines: ResizeArray<string>
        | BaseB of label: string * lines: ResizeArray<string>

        member this.Lines =
            match this with
            | DiffB(_, _, l)
            | SnapshotB(_, l)
            | BaseB(_, l) -> l

        member this.ToSection() : JjConflictSection =
            match this with
            | DiffB(f, t, l) -> JjConflictSection.Diff(f, t, List.ofSeq l)
            | SnapshotB(lbl, l) -> JjConflictSection.Snapshot(lbl, List.ofSeq l)
            | BaseB(lbl, l) -> JjConflictSection.Base(lbl, List.ofSeq l)

    let private parseError (message: string) : ProcessError = ProcessError.Parse(BINARY, message)

    let private refuse (message: string) : ProcessError = ProcessError.Spawn(BINARY, message)

    /// Does `content` contain a jj conflict-start marker (`<<<<<<< conflict N of M`)? Cheap pre-check.
    let hasConflictMarkers (content: string) : bool =
        splitInclusive content
        |> List.exists (fun line ->
            match markerRun line '<' with
            | Some n -> parseCounter (markerLabel line n) |> Option.isSome
            | None -> false)

    /// Whether `content` carries git's `<<<`/`===`/`>>>` triad — used only to steer a caller who
    /// passed a git-style file to `VcsToolkit.Git.Conflict`. Requires all three marker runs.
    let private looksGitStyle (content: string) : bool =
        let lines = splitInclusive content

        let hasRun ch =
            lines |> List.exists (fun l -> (markerRun l ch).IsSome)

        hasRun '<' && hasRun '=' && hasRun '>'

    /// Parse a jj-materialized conflicted file (native `diff`/`snapshot` styles) into
    /// text/conflict segments. Errors with `ProcessError.Parse` on malformed input (unterminated
    /// region, content before the first section marker). A **git-style** file (the `<<<`/`===`/`>>>`
    /// triad with no jj `conflict N of M` header) is redirected to `VcsToolkit.Git.Conflict`.
    let parseConflicts (content: string) : Result<JjConflictSegment list, ProcessError> =
        // Steer to the git parser only when the file is genuinely git-style (the full triad) AND has
        // no jj header — never for a jj file that just carries a marker-like content line.
        if not (hasConflictMarkers content) && looksGitStyle content then
            Error(
                parseError
                    "git-style conflict markers — parse this file with VcsToolkit.Git.Conflict (jj's `git` marker style uses git's grammar)"
            )
        else
            let lines = splitInclusive content |> List.toArray
            let segments = ResizeArray<JjConflictSegment>()
            let text = ResizeArray<string>()
            let mutable i = 0
            let mutable error: ProcessError option = None

            while i < lines.Length && error.IsNone do
                let line = lines.[i]
                i <- i + 1

                let counter =
                    match markerRun line '<' with
                    | Some n ->
                        parseCounter (markerLabel line n)
                        |> Option.map (fun (num, tot) -> (n, num, tot))
                    | None -> None

                match counter with
                | None ->
                    // A `<<<` run that isn't a `conflict N of M` header is content, not an error:
                    // jj lengthens its real markers past any marker-like content.
                    text.Add line
                | Some(n, number, total) ->
                    if text.Count > 0 then
                        segments.Add(JjConflictSegment.Text(List.ofSeq text))
                        text.Clear()

                    let markerStart = line
                    let sections = ResizeArray<SectionBuilder>()
                    let sectionMarkers = ResizeArray<string>()
                    let mutable markerEnd: string option = None

                    while markerEnd.IsNone && error.IsNone do
                        if i >= lines.Length then
                            error <- Some(parseError (sprintf "unterminated jj conflict %d of %d" number total))
                        else
                            let l = lines.[i]
                            i <- i + 1

                            // Section/end markers must match the region's opening run length — jj
                            // lengthens ALL of a file's markers together, so a shorter run is content.
                            let isEnd =
                                if markerRun l '>' = Some n then
                                    // Rely SOLELY on the structural `conflict N of M` check (after
                                    // trimming a trailing ` ends`) — no loose `ends_with("ends")`.
                                    parseCounter ((trimEndMatches " ends" (markerLabel l n)).TrimEnd())
                                    |> Option.isSome
                                else
                                    false

                            if isEnd then
                                markerEnd <- Some l
                            elif markerRun l '%' = Some n then
                                // `%%%%%%% diff from: …` then a `\\\\\\\        to: …` line.
                                let fromLabel = (trimStartMatches "diff from:" (markerLabel l n)).Trim()

                                if i >= lines.Length then
                                    error <- Some(parseError "diff section missing its `to:` line")
                                else
                                    let toLine = lines.[i]
                                    i <- i + 1

                                    if markerRun toLine '\\' <> Some n then
                                        error <-
                                            Some(
                                                parseError (
                                                    sprintf
                                                        "diff section: expected a %d-long `\\` `to:` line, got %s"
                                                        n
                                                        (toLine.TrimEnd())
                                                )
                                            )
                                    else
                                        let toLabel = (trimStartMatches "to:" (markerLabel toLine n)).Trim()
                                        sectionMarkers.Add(l + toLine)
                                        sections.Add(DiffB(fromLabel, toLabel, ResizeArray<string>()))
                            elif markerRun l '+' = Some n then
                                sectionMarkers.Add l
                                sections.Add(SnapshotB(markerLabel l n, ResizeArray<string>()))
                            elif markerRun l '-' = Some n then
                                sectionMarkers.Add l
                                sections.Add(BaseB(markerLabel l n, ResizeArray<string>()))
                            elif sections.Count = 0 then
                                error <-
                                    Some(
                                        parseError (
                                            sprintf
                                                "content before the first section marker in conflict %d: %s"
                                                number
                                                (l.TrimEnd())
                                        )
                                    )
                            else
                                // Content line for the current section.
                                sections.[sections.Count - 1].Lines.Add l

                    match markerEnd with
                    | Some endMarker when error.IsNone ->
                        let finalSections = sections |> Seq.map (fun s -> s.ToSection()) |> List.ofSeq

                        segments.Add(
                            JjConflictSegment.Conflict(
                                JjConflictRegion(
                                    number,
                                    total,
                                    finalSections,
                                    markerStart,
                                    endMarker,
                                    List.ofSeq sectionMarkers
                                )
                            )
                        )
                    | _ -> () // an error was recorded above

            match error with
            | Some e -> Error e
            | None ->
                if text.Count > 0 then
                    segments.Add(JjConflictSegment.Text(List.ofSeq text))

                Ok(List.ofSeq segments)

    /// Re-render segments verbatim — the byte-exact inverse of `parseConflicts`.
    let render (segments: JjConflictSegment list) : string =
        let out = StringBuilder()

        let sectionLines (section: JjConflictSection) : string list =
            match section with
            | JjConflictSection.Diff(_, _, lines)
            | JjConflictSection.Snapshot(_, lines)
            | JjConflictSection.Base(_, lines) -> lines

        for segment in segments do
            match segment with
            | JjConflictSegment.Text lines -> lines |> List.iter (fun l -> out.Append l |> ignore)
            | JjConflictSegment.Conflict region ->
                out.Append region.MarkerStart |> ignore

                // Pair each section with its verbatim marker. They are equal-length by
                // construction (the parser pushes one marker per section); truncate to the shorter
                // — matching Rust's `.zip()` — so a mismatch degrades gracefully rather than throwing
                // inside the byte-exact render contract.
                let paired = min region.Sections.Length region.SectionMarkers.Length

                List.zip (List.truncate paired region.Sections) (List.truncate paired region.SectionMarkers)
                |> List.iter (fun (section, marker) ->
                    out.Append marker |> ignore
                    sectionLines section |> List.iter (fun l -> out.Append l |> ignore))

                out.Append region.MarkerEnd |> ignore

        out.ToString()

    /// Produce the file content with every conflict resolved to `resolution`. Errors with a clear
    /// message when a region has no such side/base.
    let resolve (segments: JjConflictSegment list) (resolution: JjResolution) : Result<string, ProcessError> =
        let out = StringBuilder()
        let mutable error: ProcessError option = None

        for segment in segments do
            if error.IsNone then
                match segment with
                | JjConflictSegment.Text lines -> lines |> List.iter (fun l -> out.Append l |> ignore)
                | JjConflictSegment.Conflict region ->
                    let chosen =
                        match resolution with
                        | JjResolution.Side idx ->
                            let sides = region.Sides()

                            if idx >= 0 && idx < sides.Length then
                                Some sides.[idx]
                            else
                                error <-
                                    Some(
                                        refuse (
                                            sprintf
                                                "conflict %d has %d side(s); Side(%d) does not exist"
                                                region.Number
                                                sides.Length
                                                idx
                                        )
                                    )

                                None
                        | JjResolution.Base ->
                            match region.Base() with
                            | Some b -> Some b
                            | None ->
                                error <- Some(refuse (sprintf "conflict %d records no base" region.Number))
                                None

                    match chosen with
                    | Some lines -> lines |> List.iter (fun l -> out.Append l |> ignore)
                    | None -> ()

        match error with
        | Some e -> Error e
        | None -> Ok(out.ToString())
