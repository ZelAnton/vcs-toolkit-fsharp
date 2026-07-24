namespace VcsToolkit.Diff

open System

/// Low-level, total text/number parsing utilities shared by the diff parser here and the
/// git/jj CLI wrappers above it (which reference `VcsToolkit.Diff`). Kept as one home so the
/// near-identical copies these callers had grown — differing only in `string list` vs
/// `string[]` result shape — no longer drift apart. Deliberately **not** `[<AutoOpen>]`:
/// reached qualified (`TextParse.linesOf`) so it never floods a consumer's flat namespace.
[<RequireQualifiedAccess>]
module TextParse =

    /// Decode a git C-quoted path. Unquoted paths pass through unchanged; octal escapes decode
    /// to raw UTF-8 bytes so multi-byte filenames round-trip. An unknown escape is preserved
    /// verbatim, and decoding stops at the first unescaped closing quote.
    let internal unquoteGitPath (s: string) : string =
        let bytes = Text.Encoding.UTF8.GetBytes s

        if bytes.Length = 0 || bytes.[0] <> byte '"' then
            s
        else
            let output = ResizeArray<byte>(bytes.Length)
            let mutable i = 1
            let mutable stop = false

            while not stop && i < bytes.Length do
                let b = bytes.[i]

                if b = byte '"' then
                    stop <- true
                elif b = byte '\\' && i + 1 < bytes.Length then
                    i <- i + 1
                    let escaped = bytes.[i]

                    match char escaped with
                    | 'a' -> output.Add 0x07uy
                    | 'b' -> output.Add 0x08uy
                    | 't' -> output.Add(byte '\t')
                    | 'n' -> output.Add(byte '\n')
                    | 'v' -> output.Add 0x0Buy
                    | 'f' -> output.Add 0x0Cuy
                    | 'r' -> output.Add(byte '\r')
                    | '"' -> output.Add(byte '"')
                    | '\\' -> output.Add(byte '\\')
                    | c when c >= '0' && c <= '7' ->
                        // Up to 3 octal digits form one byte (`\NNN`, NNN <= 0o377).
                        let mutable value = uint32 (escaped - byte '0')
                        let mutable taken = 0

                        while taken < 2
                              && i + 1 < bytes.Length
                              && bytes.[i + 1] >= byte '0'
                              && bytes.[i + 1] <= byte '7' do
                            i <- i + 1
                            value <- value * 8u + uint32 (bytes.[i] - byte '0')
                            taken <- taken + 1

                        output.Add(byte value)
                    | _ ->
                        output.Add(byte '\\')
                        output.Add escaped

                    i <- i + 1
                else
                    output.Add b
                    i <- i + 1

            Text.Encoding.UTF8.GetString(output.ToArray())

    /// Digit-only, invariant-culture parse of a `usize`-width field, matching Rust's
    /// `usize::from_str` (which rejects signs and whitespace), so a malformed token like `-5`
    /// reads as 0 rather than a sign-led or thrown value. The single `uint64` parser shared by
    /// the diff hunk-range parser, `git`'s diff-stat/ahead-behind/change-count fields, and `jj`'s
    /// numeric template tokens. (A signed `int32`-width field — e.g. git blame's line numbers —
    /// needs a distinct parser and keeps its own, local to that parser.)
    let parseUInt64Or0 (s: string) : uint64 =
        if s.Length > 0 && s |> Seq.forall Char.IsAsciiDigit then
            match UInt64.TryParse(s, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
            | true, v -> v
            | _ -> 0UL
        else
            0UL

    /// Split `text` into lines that each KEEP their trailing `\n` (like Rust `str::split_inclusive`):
    /// `"a\nb"` → `["a\n"; "b"]`, `"a\n"` → `["a\n"]`, `""` → `[]`. A caller needing random access
    /// converts with `List.toArray`.
    let splitInclusive (text: string) : string list =
        if text.Length = 0 then
            []
        else
            let result = ResizeArray<string>()
            let mutable start = 0

            for i in 0 .. text.Length - 1 do
                if text.[i] = '\n' then
                    result.Add(text.Substring(start, i - start + 1))
                    start <- i + 1

            if start < text.Length then
                result.Add(text.Substring start)

            List.ofSeq result

    /// Lines with terminators stripped (mirrors Rust `str::lines`: strips the `\r` of a `\r\n`,
    /// keeps a bare trailing `\r`, and yields no trailing empty for a final `\n`).
    let linesOf (text: string) : string list =
        if text.Length = 0 then
            []
        else
            let parts = text.Split('\n')
            let n = parts.Length

            [ for idx in 0 .. n - 1 do
                  let part = parts.[idx]
                  let isLast = idx = n - 1

                  if isLast && part.Length = 0 then
                      () // a final '\n' yields no trailing empty line
                  elif (not isLast) && part.EndsWith("\r", StringComparison.Ordinal) then
                      yield part.Substring(0, part.Length - 1) // the '\r' of a '\r\n' terminator
                  else
                      yield part ] // a bare trailing '\r' with no following '\n' is kept
