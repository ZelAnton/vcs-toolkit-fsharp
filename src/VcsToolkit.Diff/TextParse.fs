namespace VcsToolkit.Diff

open System

/// Low-level, total text/number parsing utilities shared by the diff parser here and the
/// git/jj CLI wrappers above it (which reference `VcsToolkit.Diff`). Kept as one home so the
/// near-identical copies these callers had grown — differing only in `string list` vs
/// `string[]` result shape — no longer drift apart. Deliberately **not** `[<AutoOpen>]`:
/// reached qualified (`TextParse.linesOf`) so it never floods a consumer's flat namespace.
[<RequireQualifiedAccess>]
module TextParse =

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
