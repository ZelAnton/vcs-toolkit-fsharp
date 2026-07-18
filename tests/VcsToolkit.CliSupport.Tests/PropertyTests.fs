// Flat (unqualified) module name: `CliSupportTests.fs` already claims the top-level
// module `VcsToolkit.CliSupport.Tests`, so nesting under that same qualified path here
// would clash (F# forbids a name being both a module and a namespace prefix in one
// assembly).
module PropertyTests

open System.Text
open System.Text.Json
open FsCheck
open FsCheck.FSharp
open NUnit.Framework
// Opened last: NUnit.Framework's own `PropertyAttribute` (test metadata, 2-arg
// constructor) would otherwise shadow FsCheck.NUnit's parameterless `[<Property>]`.
open FsCheck.NUnit
open VcsToolkit.CliSupport

/// Printable ASCII plus a curated set of characters the JSON helpers and `RemoteUrl`
/// treat specially (CRLF, NUL, quote/backslash, non-ASCII, URL/JSON punctuation) —
/// hand-rolled instead of the default `Arbitrary<char>` so these edge cases show up
/// often, not just eventually.
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
      ':'
      '/'
      '@'
      '['
      ']'
      '?'
      '#'
      '.'
      'é'
      '中'
      '\u001f' ]

let private charGen: Gen<char> =
    Gen.oneof [ Gen.elements edgeChars; Gen.choose (32, 126) |> Gen.map char ]

/// Any string over the char pool above — the "arbitrary garbage" generator.
let private genArbitraryString: Gen<string> =
    Gen.arrayOf charGen |> Gen.map (fun arr -> System.String(arr))

/// Strings built from the literal tokens the JSON helpers and `RemoteUrl` key off
/// (braces/brackets/quotes/colons, scheme separators, userinfo `@`, IPv6 brackets),
/// concatenated in random order and quantity — the "nearly valid but adversarial"
/// generator.
let private edgeTokens =
    [ "{"
      "}"
      "["
      "]"
      "\""
      ":"
      ","
      "null"
      "true"
      "false"
      "-1"
      "0"
      "1e999"
      "\\u0000"
      "\\\""
      "https://"
      "http://"
      "ssh://"
      "://"
      "user:pass@"
      "@"
      "[::1]"
      ":8443"
      "github.com"
      "/o/r.git"
      "?a=b"
      "#frag"
      "\r\n"
      "\n"
      "\t" ]

let private genEdgeString: Gen<string> =
    Gen.listOf (Gen.elements edgeTokens) |> Gen.map (String.concat "")

let private arbAnyString: Arbitrary<string> =
    Arb.fromGen (Gen.oneof [ genArbitraryString; genEdgeString ])

/// Escape `s` as a JSON string literal, so a piece of `genArbitraryString` (which
/// freely contains control bytes / quotes / backslashes) can be embedded in
/// otherwise-syntactically-valid generated JSON text.
let private jsonStringLiteral (s: string) : string =
    let sb = StringBuilder()
    sb.Append '"' |> ignore

    for ch in s do
        match ch with
        | '"' -> sb.Append "\\\"" |> ignore
        | '\\' -> sb.Append "\\\\" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | c when int c < 0x20 -> sb.Append(sprintf "\\u%04x" (int c)) |> ignore
        | c -> sb.Append c |> ignore

    sb.Append '"' |> ignore
    sb.ToString()

let private genJsonLeaf: Gen<string> =
    Gen.oneof
        [ Gen.constant "null"
          Gen.constant "true"
          Gen.constant "false"
          Gen.choose (-1000, 1000) |> Gen.map string
          genArbitraryString |> Gen.map jsonStringLiteral ]

/// A small, depth-limited generator of syntactically valid JSON text, so the
/// field-reader properties exercise every `JsonValueKind` (including arrays and
/// nested objects) without ever needing to special-case a parse failure.
let rec private genJsonValue (depth: int) : Gen<string> =
    if depth <= 0 then
        genJsonLeaf
    else
        Gen.oneof [ genJsonLeaf; genJsonArray depth; genJsonObject depth ]

and private genJsonArray (depth: int) : Gen<string> =
    Gen.listOf (genJsonValue (depth - 1))
    |> Gen.map (fun items -> "[" + String.concat "," items + "]")

and private genJsonObject (depth: int) : Gen<string> =
    let genPair =
        Gen.map2
            (fun k v -> jsonStringLiteral k + ":" + v)
            (Gen.elements [ "a"; "b"; "name"; "id"; "value"; "owner"; "author"; "" ])
            (genJsonValue (depth - 1))

    Gen.listOf genPair |> Gen.map (fun pairs -> "{" + String.concat "," pairs + "}")

/// A generator whose root is always a JSON object — the shape every `Json` field
/// reader below is documented to accept.
let private genJsonObjectText: Gen<string> = genJsonObject 2

let private candidateFieldNames =
    [ "a"; "b"; "name"; "id"; "value"; "owner"; "author"; "missing"; "" ]

[<TestFixture>]
type JsonAndRemoteUrlPropertyTests() =

    /// `withDoc`/`parseArray`/`parseObject` are documented to turn malformed or
    /// wrong-shaped JSON into an `Error`, never an exception — verify that holds for
    /// arbitrary (mostly non-JSON) input, not just the malformed examples in
    /// `CliSupportTests`.
    [<Property>]
    member _.WithDocIsTotal() =
        Prop.forAll arbAnyString (fun s -> Json.withDoc s (fun (_: JsonElement) -> Ok()) |> ignore)

    [<Property>]
    member _.ParseArrayIsTotal() =
        Prop.forAll arbAnyString (fun s -> Json.parseArray (fun (_: JsonElement) -> ()) s |> ignore)

    [<Property>]
    member _.ParseObjectIsTotal() =
        Prop.forAll arbAnyString (fun s -> Json.parseObject (fun (_: JsonElement) -> ()) s |> ignore)

    /// The field readers (`strOr`/`strOpt`/`strFirst`/`u64Or`/`boolOr`/`nestedStr`/
    /// `arrayOf`) are documented to read an absent / `null` / wrong-kind field as
    /// empty rather than throwing, given an object element — verify on generated
    /// objects whose fields cover every `JsonValueKind`, not just the hand-picked
    /// shapes in `CliSupportTests`.
    [<Property>]
    member _.FieldReadersAreTotalOnAnyShapeElement() =
        Prop.forAll (Arb.fromGen genJsonObjectText) (fun json ->
            use doc = JsonDocument.Parse json
            let el = doc.RootElement

            for name in candidateFieldNames do
                Json.strOr el name |> ignore
                Json.strOpt el name |> ignore
                Json.u64Or el name |> ignore
                Json.boolOr el name |> ignore
                Json.nestedStr el name "inner" |> ignore
                Json.arrayOf el name |> ignore

            Json.strFirst el candidateFieldNames |> ignore)

    /// The mechanics `RemoteUrl` exposes (`dropUserinfo`/`afterScheme`/`authority`/
    /// `stripPort`) are pure string slicing with no expectation of well-formed input —
    /// verify none of them ever throws on arbitrary text.
    [<Property>]
    member _.RemoteUrlHelpersAreTotal() =
        Prop.forAll arbAnyString (fun s ->
            RemoteUrl.dropUserinfo s |> ignore
            RemoteUrl.afterScheme s |> ignore
            RemoteUrl.authority s |> ignore
            RemoteUrl.stripPort s |> ignore)
