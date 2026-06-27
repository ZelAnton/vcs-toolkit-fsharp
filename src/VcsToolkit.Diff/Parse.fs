namespace VcsToolkit.Diff

open System
open System.Text

/// The git-format unified-diff parser and the `<tool> --version` banner parser.
/// Pure and total: arbitrary CLI text in, never an exception. Auto-opened so
/// consumers reach `parseDiff` / `parseDottedVersion` flat after `open VcsToolkit.Diff`.
[<AutoOpen>]
module Parse =

    // Digit-only, invariant-culture parse matching Rust's `usize::from_str` (which
    // rejects signs/whitespace), so a malformed hunk range like `-5` reads as 0.
    let private parseIntOr0 (s: string) : uint64 =
        if s.Length > 0 && s |> Seq.forall Char.IsAsciiDigit then
            match UInt64.TryParse(s, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
            | true, v -> v
            | _ -> 0UL
        else
            0UL

    let private stripPrefix (prefix: string) (s: string) : string option =
        if s.StartsWith(prefix, StringComparison.Ordinal) then
            Some(s.Substring prefix.Length)
        else
            None

    /// Split `text` into lines, each including its trailing `\n` (the last may lack one).
    let private splitInclusive (text: string) : string list =
        let result = ResizeArray<string>()
        let mutable start = 0

        for i in 0 .. text.Length - 1 do
            if text.[i] = '\n' then
                result.Add(text.Substring(start, i - start + 1))
                start <- i + 1

        if start < text.Length then
            result.Add(text.Substring start)

        List.ofSeq result

    /// Lines with terminators stripped (mirrors Rust `str::lines`: handles `\n` and
    /// `\r\n`, and yields no trailing empty for a final newline).
    let private linesOf (text: string) : string list =
        if text = "" then
            []
        else
            let parts = text.Split('\n')
            let n = parts.Length

            [ for idx in 0 .. n - 1 do
                  let part = parts.[idx]
                  let isLast = idx = n - 1

                  if isLast && part = "" then
                      () // a final '\n' yields no trailing empty line
                  elif (not isLast) && part.EndsWith("\r", StringComparison.Ordinal) then
                      yield part.Substring(0, part.Length - 1) // the '\r' of a '\r\n' terminator
                  else
                      yield part ] // a bare trailing '\r' with no following '\n' is kept

    /// Slice a git-format diff into per-file sections (each starts at `diff --git`).
    let private diffSections (full: string) : string list =
        let bounds = ResizeArray<int>()
        let mutable idx = 0

        for line in splitInclusive full do
            if line.StartsWith("diff --git ", StringComparison.Ordinal) then
                bounds.Add idx

            idx <- idx + line.Length

        [ for k in 0 .. bounds.Count - 1 ->
              let s = bounds.[k]
              let e = if k + 1 < bounds.Count then bounds.[k + 1] else full.Length
              full.Substring(s, e - s) ]

    /// Decode a git C-quoted path. git wraps a path in double quotes and C-escapes it
    /// when it has a control byte, a `"`, a `\`, or (default `core.quotePath`) a
    /// non-ASCII byte. An unquoted path is returned unchanged. Octal escapes decode to
    /// raw bytes, so a multi-byte UTF-8 filename round-trips; invalid UTF-8 falls back
    /// to the replacement char. Decoding stops at the first unescaped closing quote.
    let private unquoteGitPath (s: string) : string =
        let bytes = Encoding.UTF8.GetBytes s

        if bytes.Length = 0 || bytes.[0] <> byte '"' then
            s
        else
            let out = ResizeArray<byte>(bytes.Length)
            let mutable i = 1
            let mutable stop = false

            while not stop && i < bytes.Length do
                let b = bytes.[i]

                if b = byte '"' then
                    stop <- true
                elif b = byte '\\' && i + 1 < bytes.Length then
                    i <- i + 1
                    let e = bytes.[i]

                    match char e with
                    | 'a' -> out.Add 0x07uy
                    | 'b' -> out.Add 0x08uy
                    | 't' -> out.Add(byte '\t')
                    | 'n' -> out.Add(byte '\n')
                    | 'v' -> out.Add 0x0Buy
                    | 'f' -> out.Add 0x0Cuy
                    | 'r' -> out.Add(byte '\r')
                    | '"' -> out.Add(byte '"')
                    | '\\' -> out.Add(byte '\\')
                    | c when c >= '0' && c <= '7' ->
                        // Up to 3 octal digits → one byte (`\NNN`, NNN ≤ 0o377).
                        let mutable v = uint32 (e - byte '0')
                        let mutable taken = 0

                        while taken < 2
                              && i + 1 < bytes.Length
                              && bytes.[i + 1] >= byte '0'
                              && bytes.[i + 1] <= byte '7' do
                            i <- i + 1
                            v <- v * 8u + uint32 (bytes.[i] - byte '0')
                            taken <- taken + 1

                        out.Add(byte v)
                    | _ -> out.Add e // unknown escape: keep the byte

                    i <- i + 1
                else
                    out.Add b
                    i <- i + 1

            Encoding.UTF8.GetString(out.ToArray())

    /// Parse a `<start>[,<count>]` hunk range; an omitted count means 1 line.
    let private parseHunkRange (range: string) : uint64 * uint64 =
        match range.IndexOf ',' with
        | -1 -> (parseIntOr0 range, 1UL)
        | idx -> (parseIntOr0 (range.Substring(0, idx)), parseIntOr0 (range.Substring(idx + 1)))

    /// Parse a hunk header `@@ -<os>[,<ol>] +<ns>[,<nl>] @@[ <section>]` into an empty
    /// `Hunk`; `None` for any other line.
    let private parseHunkHeader (line: string) : Hunk option =
        if not (line.StartsWith("@@ ", StringComparison.Ordinal)) then
            None
        else
            let rest = line.Substring 3

            match rest.IndexOf(" @@", StringComparison.Ordinal) with
            | -1 -> None
            | idx ->
                let ranges = rest.Substring(0, idx)
                let section = rest.Substring(idx + 3)
                let parts = ranges.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

                if
                    parts.Length < 2
                    || not (parts.[0].StartsWith("-", StringComparison.Ordinal))
                    || not (parts.[1].StartsWith("+", StringComparison.Ordinal))
                then
                    None
                else
                    let oldStart, oldLines = parseHunkRange (parts.[0].Substring 1)
                    let newStart, newLines = parseHunkRange (parts.[1].Substring 1)

                    let sec =
                        if section.StartsWith(" ", StringComparison.Ordinal) then
                            section.Substring 1
                        else
                            section

                    Some
                        { OldStart = oldStart
                          OldLines = oldLines
                          NewStart = newStart
                          NewLines = newLines
                          Section = sec
                          Lines = [] }

    /// Fallback path extraction for sections with no `+++`/`---`/`rename` lines
    /// (e.g. binary files): the `b/<new>` of the `diff --git` header. Handles the
    /// unquoted `a/<p> b/<p>` form and git's C-quoted `"a/<p>" "b/<p>"` form.
    let private headerBPath (section: string) : string option =
        match linesOf section with
        | [] -> None
        | first :: _ ->
            if not (first.StartsWith("diff --git ", StringComparison.Ordinal)) then
                None
            else
                let s = first.Substring 11

                let path =
                    match s.LastIndexOf("\"b/", StringComparison.Ordinal) with
                    | q when q >= 0 -> defaultArg (stripPrefix "b/" (unquoteGitPath (s.Substring q))) ""
                    | _ ->
                        match s.IndexOf(" b/", StringComparison.Ordinal) with
                        | -1 -> ""
                        | idx -> defaultArg (stripPrefix "b/" (s.Substring(idx + 1))) ""

                if path <> "" then Some path else None

    /// Determine the `FileDiff` for one `diff --git` section.
    let private parseSection (section: string) : FileDiff option =
        let mutable kind = ChangeKind.Modified
        let mutable newPath: string option = None
        let mutable minusPath: string option = None
        let mutable renameTo: string option = None
        let mutable renameFrom: string option = None
        let hunks = ResizeArray<Hunk>()
        let mutable curHeader: Hunk option = None
        let curLines = ResizeArray<DiffLine>()

        let closeCurrent () =
            match curHeader with
            | Some h ->
                hunks.Add { h with Lines = List.ofSeq curLines }
                curLines.Clear()
                curHeader <- None
            | None -> ()

        for line in linesOf section do
            match parseHunkHeader line with
            | Some h ->
                closeCurrent ()
                curHeader <- Some h
            | None ->
                match curHeader with
                | Some _ ->
                    // Inside a hunk body: classify by the leading marker. `\ No newline`
                    // annotations and stray blank lines are dropped.
                    if line.Length > 0 then
                        match line.[0] with
                        | ' ' -> curLines.Add(DiffLine.Context(line.Substring 1))
                        | '+' -> curLines.Add(DiffLine.Added(line.Substring 1))
                        | '-' -> curLines.Add(DiffLine.Removed(line.Substring 1))
                        | _ -> ()
                | None ->
                    // Header region (before the first `@@`).
                    if line.StartsWith("new file", StringComparison.Ordinal) then
                        kind <- ChangeKind.Added
                    elif line.StartsWith("deleted file", StringComparison.Ordinal) then
                        kind <- ChangeKind.Deleted
                    elif line.StartsWith("rename to ", StringComparison.Ordinal) then
                        renameTo <- Some(unquoteGitPath ((line.Substring 10).TrimEnd()))
                    elif line.StartsWith("rename from ", StringComparison.Ordinal) then
                        renameFrom <- Some(unquoteGitPath ((line.Substring 12).TrimEnd()))
                    elif line.StartsWith("+++ ", StringComparison.Ordinal) then
                        newPath <- stripPrefix "b/" (unquoteGitPath ((line.Substring 4).TrimEnd()))
                    elif line.StartsWith("--- ", StringComparison.Ordinal) then
                        minusPath <- stripPrefix "a/" (unquoteGitPath ((line.Substring 4).TrimEnd()))

        closeCurrent ()

        let normalize (p: string) = p.Replace('\\', '/')

        // A rename keeps its old path so a caller can record the deletion too.
        let oldPath =
            match renameTo with
            | Some _ ->
                kind <- ChangeKind.Renamed
                Option.map normalize renameFrom
            | None -> None

        // Resolve the path by priority (rename target → `+++ b/` → `--- a/` → header),
        // skipping any present-but-empty source so a malformed line falls through.
        let path =
            [ renameTo; newPath; minusPath ]
            |> List.choose id
            |> List.tryFind (fun p -> p <> "")
            |> Option.orElseWith (fun () -> headerBPath section)

        match path with
        | None -> None
        | Some p ->
            Some
                { Change = kind
                  Path = normalize p
                  OldPath = oldPath
                  Hunks = List.ofSeq hunks
                  Raw = section }

    /// Parse a git-format unified diff into one `FileDiff` per file. Works on
    /// `git diff` and `jj diff --git` output alike.
    let parseDiff (diff: string) : FileDiff list =
        diffSections diff |> List.choose parseSection

    /// The numeric prefix of `s` (`"38-dev"` → 38); `None` when it has none.
    let private leadingNumber (s: string) : uint64 option =
        let len = s |> Seq.takeWhile Char.IsAsciiDigit |> Seq.length

        if len = 0 then
            None
        else
            match
                UInt64.TryParse(
                    s.Substring(0, len),
                    Globalization.NumberStyles.None,
                    Globalization.CultureInfo.InvariantCulture
                )
            with
            | true, v -> Some v
            | _ -> None

    /// Find the first `N.N[.N…]` token in `raw` and return its leading three numeric
    /// components (a missing patch reads as 0). Tolerant of trailers like `0-dev` or
    /// `2.54.0.windows.1`.
    let parseDottedVersion (raw: string) : VcsToolkit.Diff.Version option =
        raw.Split([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun token ->
            let parts = token.Split('.')

            match (if parts.Length >= 1 then leadingNumber parts.[0] else None) with
            | None -> None
            | Some major ->
                // A bare number ("2") is not a version token.
                match (if parts.Length >= 2 then leadingNumber parts.[1] else None) with
                | None -> None
                | Some minor ->
                    let patch =
                        if parts.Length >= 3 then
                            defaultArg (leadingNumber parts.[2]) 0UL
                        else
                            0UL

                    Some
                        { Major = major
                          Minor = minor
                          Patch = patch })
