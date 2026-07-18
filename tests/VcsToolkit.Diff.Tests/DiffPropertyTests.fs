// Flat (unqualified) module name: `DiffTests.fs` already claims the top-level module
// `VcsToolkit.Diff.Tests`, so nesting under that same qualified path here would clash
// (F# forbids a name being both a module and a namespace prefix in one assembly).
module DiffPropertyTests

open FsCheck
open FsCheck.FSharp
open NUnit.Framework
// Opened last: NUnit.Framework's own `PropertyAttribute` (test metadata, 2-arg
// constructor) would otherwise shadow FsCheck.NUnit's parameterless `[<Property>]`.
open FsCheck.NUnit
open VcsToolkit.Diff

/// Printable ASCII plus a curated set of characters the diff/version parsers treat
/// specially (CRLF, NUL, tab, quote/backslash, non-ASCII, control bytes) — hand-rolled
/// instead of the default `Arbitrary<char>` so these edge cases show up often, not just
/// eventually.
let private edgeChars =
    [ ' '
      'a'
      'Z'
      '0'
      '9'
      '\t'
      '\r'
      '\n'
      '\000'
      '"'
      '\\'
      '+'
      '-'
      '@'
      '.'
      'é'
      '中'
      '\u001f' ]

let private charGen: Gen<char> =
    Gen.oneof [ Gen.elements edgeChars; Gen.choose (32, 126) |> Gen.map char ]

/// Any string over the char pool above — the "arbitrary garbage" generator.
let private genArbitraryString: Gen<string> =
    Gen.arrayOf charGen |> Gen.map (fun arr -> System.String(arr))

/// Strings built from the literal tokens `parseDiff` / `parseDottedVersion` key off
/// (`diff --git`, `@@`, `+`/`-`/` ` markers, dotted-version digits, non-digit
/// trailers), concatenated in random order and quantity — the "nearly valid but
/// adversarial" generator.
let private diffEdgeTokens =
    [ "diff --git a/x b/y"
      "--- a/x"
      "+++ b/y"
      "@@ -1,2 +1,3 @@"
      "@@ "
      "@@"
      "+added"
      "-removed"
      " context"
      "rename from a"
      "rename to b"
      "new file mode 100644"
      "deleted file mode 100644"
      "similarity index 100%"
      "\r\n"
      "\n"
      "\t"
      "1.2.3"
      "2.54.0.windows.1"
      "0-dev"
      "..."
      "-"
      "+"
      "\""
      "\\" ]

let private genEdgeString: Gen<string> =
    Gen.listOf (Gen.elements diffEdgeTokens) |> Gen.map (String.concat "")

let private arbAnyString: Arbitrary<string> =
    Arb.fromGen (Gen.oneof [ genArbitraryString; genEdgeString ])

/// A generated hunk body line: a change marker (` `/`+`/`-`) with a fixed, safe content
/// word — never itself a `@@ `-prefixed line, so it can't be mistaken for a new hunk
/// header while assembling a synthetic diff.
let private lineGen: Gen<char * string> =
    Gen.map2
        (fun marker content -> marker, content)
        (Gen.elements [ ' '; '+'; '-' ])
        (Gen.elements [ "alpha"; "bee"; "cat"; "delta12"; "e"; "foxtrot" ])

/// One synthetic modified file: a unique path plus a random-length list of body lines.
type private GenFile =
    { Path: string
      Lines: (char * string) list }

let private filesGen: Gen<GenFile list> =
    Gen.listOf (Gen.listOf lineGen)
    |> Gen.map (
        List.mapi (fun i lines ->
            { Path = sprintf "f%d" i
              Lines = lines })
    )

let private nl = "\n"

/// Render one synthetic `GenFile` as a valid `diff --git` section. The `@@` header's
/// counts are cosmetic (the parser never cross-checks them against the body), so they
/// only need to look plausible.
let private renderFile (f: GenFile) : string =
    let oldCount = f.Lines |> List.filter (fun (m, _) -> m <> '+') |> List.length
    let newCount = f.Lines |> List.filter (fun (m, _) -> m <> '-') |> List.length

    [ sprintf "diff --git a/%s b/%s" f.Path f.Path
      sprintf "--- a/%s" f.Path
      sprintf "+++ b/%s" f.Path
      sprintf "@@ -1,%d +1,%d @@" oldCount newCount ]
    @ (f.Lines |> List.map (fun (m, c) -> string m + c))
    |> List.map (fun l -> l + nl)
    |> String.concat ""

let private renderDiff (files: GenFile list) : string =
    files |> List.map renderFile |> String.concat ""

[<TestFixture>]
type DiffPropertyTests() =

    [<Property>]
    member _.ParseDiffIsTotal() =
        Prop.forAll arbAnyString (fun s -> parseDiff s |> ignore)

    [<Property>]
    member _.ParseDottedVersionIsTotal() =
        Prop.forAll arbAnyString (fun s -> parseDottedVersion s |> ignore)

    /// The invariant example-tested piecemeal in `DiffTests` (hunk line counts feed the
    /// `DiffStat` aggregate) generalised over generated valid unified diffs: the number
    /// of parsed files, and the total `+`/`-` lines across all their hunks, match
    /// exactly what was generated.
    [<Property>]
    member _.ParseDiffLineCountsMatchGenerated() =
        Prop.forAll (Arb.fromGen filesGen) (fun files ->
            let parsed = parseDiff (renderDiff files)

            let countMarker marker =
                files
                |> List.sumBy (fun f -> f.Lines |> List.filter (fun (m, _) -> m = marker) |> List.length)

            let isAdded =
                function
                | DiffLine.Added _ -> true
                | _ -> false

            let isRemoved =
                function
                | DiffLine.Removed _ -> true
                | _ -> false

            let countParsed isKind =
                parsed
                |> List.sumBy (fun fd ->
                    fd.Hunks |> List.sumBy (fun h -> h.Lines |> List.filter isKind |> List.length))

            parsed.Length = files.Length
            && countParsed isAdded = countMarker '+'
            && countParsed isRemoved = countMarker '-')

    /// Round-trip invariant: any generated `major.minor.patch` triple survives
    /// `parseDottedVersion` intact (the discriminating numeric-vs-lexicographic case
    /// from `VersionTests.OrdersNumerically` generalised to arbitrary triples).
    [<Property>]
    member _.ParseDottedVersionRoundtripsGeneratedTriples() =
        Prop.forAll
            (Arb.fromGen (
                Gen.map3
                    (fun major minor patch -> major, minor, patch)
                    (Gen.choose (0, 999))
                    (Gen.choose (0, 999))
                    (Gen.choose (0, 999))
            ))
            (fun (major, minor, patch) ->
                let v = parseDottedVersion (sprintf "%d.%d.%d" major minor patch) |> Option.get
                v.Major = uint64 major && v.Minor = uint64 minor && v.Patch = uint64 patch)
