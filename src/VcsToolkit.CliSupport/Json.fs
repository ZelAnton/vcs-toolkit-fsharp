namespace VcsToolkit.CliSupport

open System
open System.Text.Json

/// Tolerant, total helpers for parsing a CLI's JSON output over `System.Text.Json`.
/// Every field reader is total: an absent / `null` / wrong-kind field reads as empty
/// (`""` / `None` / `false` / `0`), and `withDoc` / `parseArray` / `parseObject` turn
/// a malformed or wrong-shaped document into an `Error`, never an exception — the
/// contract the forge DTO parsers (GitHub / GitLab / Gitea) build on. Mirrors the
/// Rust `vcs_cli_support::json` module.
[<RequireQualifiedAccess>]
module Json =

    /// A string property, or `""` when absent / `null` / not a string (a CLI often
    /// sends a present `null` for an empty optional string).
    let strOr (el: JsonElement) (name: string) : string =
        match el.TryGetProperty name with
        | true, p when p.ValueKind = JsonValueKind.String -> p.GetString() |> Option.ofObj |> Option.defaultValue ""
        | _ -> ""

    /// A string property as an option: `Some` only for a present non-null string
    /// (both absent and a present `null` read as `None`).
    let strOpt (el: JsonElement) (name: string) : string option =
        match el.TryGetProperty name with
        | true, p when p.ValueKind = JsonValueKind.String -> p.GetString() |> Option.ofObj
        | _ -> None

    /// The first present non-null string among `names`, or `""` if none match. Lets a
    /// parser tolerate several spellings of a key (e.g. a CLI's snake-case quirk and
    /// the plainer forms a future version might switch to).
    let strFirst (el: JsonElement) (names: string list) : string =
        names
        |> List.tryPick (fun name ->
            match el.TryGetProperty name with
            | true, p when p.ValueKind = JsonValueKind.String -> p.GetString() |> Option.ofObj
            | _ -> None)
        |> Option.defaultValue ""

    /// A numeric property as `uint64`, or `0` when absent / not a number / not
    /// representable as a `uint64` (`TryGetUInt64` returns false rather than throwing
    /// on an overflowing or fractional or negative number).
    let u64Or (el: JsonElement) (name: string) : uint64 =
        match el.TryGetProperty name with
        | true, p when p.ValueKind = JsonValueKind.Number ->
            match p.TryGetUInt64() with
            | true, n -> n
            | _ -> 0UL
        | _ -> 0UL

    /// A boolean property, or `false` when absent / not a boolean.
    let boolOr (el: JsonElement) (name: string) : bool =
        match el.TryGetProperty name with
        | true, p when p.ValueKind = JsonValueKind.True || p.ValueKind = JsonValueKind.False -> p.GetBoolean()
        | _ -> false

    /// A string `field` read from a nested object property `objName`, or `""` when the
    /// object is absent / `null` / not an object (CLIs nest `owner` / `author` /
    /// `_links` and can send a `null` for a missing one).
    let nestedStr (el: JsonElement) (objName: string) (field: string) : string =
        match el.TryGetProperty objName with
        | true, o when o.ValueKind = JsonValueKind.Object -> strOr o field
        | _ -> ""

    /// The elements of an array property (empty when absent / not an array).
    let arrayOf (el: JsonElement) (name: string) : JsonElement list =
        match el.TryGetProperty name with
        | true, a when a.ValueKind = JsonValueKind.Array -> [ for x in a.EnumerateArray() -> x ]
        | _ -> []

    /// Run `parse` over the parsed document's root, mapping a malformed or wrong-kind
    /// document to an `Error` — never an exception. Three exception families are
    /// expected and all mean "this isn't the shape I model": `JsonException`
    /// (syntactically malformed JSON), `ArgumentNullException` (a `null` input string),
    /// and `InvalidOperationException` (a field reader hit a `JsonElement` of the wrong
    /// kind — e.g. `TryGetProperty` on a non-object array element). Catching all three
    /// keeps the parsers total.
    let withDoc (json: string) (parse: JsonElement -> Result<'T, string>) : Result<'T, string> =
        try
            use doc = JsonDocument.Parse json
            parse doc.RootElement
        with
        | :? JsonException as ex -> Error ex.Message
        | :? ArgumentNullException -> Error "no JSON to parse (null input)"
        | :? InvalidOperationException as ex -> Error ex.Message

    /// Parse a JSON array of objects, mapping each element with the total `toItem`.
    /// A non-array root, or any non-object element, is an `Error` (serde rejects the
    /// same shapes) rather than a silent empty / defaulted record.
    let parseArray (toItem: JsonElement -> 'T) (json: string) : Result<'T list, string> =
        withDoc json (fun root ->
            if root.ValueKind <> JsonValueKind.Array then
                Error "expected a JSON array"
            elif
                root.EnumerateArray()
                |> Seq.forall (fun e -> e.ValueKind = JsonValueKind.Object)
            then
                Ok [ for item in root.EnumerateArray() -> toItem item ]
            else
                Error "expected a JSON array of objects")

    /// Like `parseArray`, but each element parse may itself fail (`toItem` returns a
    /// `Result`); the first failing element aborts the whole parse. For list rows that
    /// carry a required, convert-or-fail field (e.g. a stringly-typed numeric id).
    let parseArrayResult (toItem: JsonElement -> Result<'T, string>) (json: string) : Result<'T list, string> =
        withDoc json (fun root ->
            if root.ValueKind <> JsonValueKind.Array then
                Error "expected a JSON array"
            elif
                root.EnumerateArray()
                |> Seq.forall (fun e -> e.ValueKind = JsonValueKind.Object)
            then
                let folder (acc: Result<'T list, string>) (el: JsonElement) =
                    match acc with
                    | Error _ -> acc
                    | Ok items ->
                        match toItem el with
                        | Ok item -> Ok(item :: items)
                        | Error e -> Error e

                match Seq.fold folder (Ok []) (root.EnumerateArray()) with
                | Ok items -> Ok(List.rev items)
                | Error e -> Error e
            else
                Error "expected a JSON array of objects")

    /// Parse a single JSON object with the total `toItem`. A non-object root is an
    /// `Error` rather than a silently all-defaulted record.
    let parseObject (toItem: JsonElement -> 'T) (json: string) : Result<'T, string> =
        withDoc json (fun root ->
            if root.ValueKind = JsonValueKind.Object then
                Ok(toItem root)
            else
                Error "expected a JSON object")
