module Main

open System
open System.Reflection
open VcsToolkit.Agent

type private ParsedCommand =
    { Operation: AgentOperation
      OutputLimitBytes: int }

let internal readVersionFromAssembly (assembly: Assembly | null) =
    match assembly with
    | null -> "0.0.0-unknown"
    | assembly ->
        match assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null -> "0.0.0-unknown"
        | attribute when String.IsNullOrWhiteSpace attribute.InformationalVersion -> "0.0.0-unknown"
        | attribute -> attribute.InformationalVersion

let internal toolVersion () =
    Assembly.GetEntryAssembly() |> readVersionFromAssembly

let private tryOperation (args: string list) =
    match args with
    | [ "probe" ] -> Some AgentOperation.Probe
    | [ "inspect" ] -> Some AgentOperation.Inspect
    | [ "changes" ] -> Some AgentOperation.Changes
    | [ "commit" ] -> Some AgentOperation.Commit
    | [ "publish" ] -> Some AgentOperation.Publish
    | [ "ci"; "status" ] -> Some AgentOperation.CiStatus
    | [ "ci"; "wait" ] -> Some AgentOperation.CiWait
    | _ -> None

let private parseOutputLimit args =
    let rec loop commandArgs outputLimit remaining =
        match remaining with
        | [] -> Ok(List.rev commandArgs, outputLimit)
        | "--output-budget" :: value :: rest ->
            match Int32.TryParse value with
            | true, parsed when parsed >= Agent.MinimumOutputLimitBytes -> loop commandArgs parsed rest
            | _ ->
                Error(
                    Agent.invalidInput
                        "command"
                        $"--output-budget must be an integer of at least {Agent.MinimumOutputLimitBytes} bytes"
                )
        | "--output-budget" :: [] -> Error(Agent.invalidInput "command" "--output-budget requires an integer value")
        | token :: rest -> loop (token :: commandArgs) outputLimit rest

    loop [] Agent.DefaultOutputLimitBytes args

let private parse args =
    match parseOutputLimit (List.ofArray args) with
    | Error envelope -> Error envelope
    | Ok(commandArgs, outputLimit) ->
        match tryOperation commandArgs with
        | Some operation ->
            Ok
                { Operation = operation
                  OutputLimitBytes = outputLimit }
        | None -> Error(Agent.invalidInput "command" "unknown or incomplete operation")

let internal run args =
    match parse args with
    | Error envelope -> AgentWire.render Agent.DefaultOutputLimitBytes envelope
    | Ok command ->
        let envelope =
            match command.Operation with
            | AgentOperation.Probe -> Agent.probe (toolVersion ())
            | operation -> Agent.unsupported operation

        AgentWire.render command.OutputLimitBytes envelope

[<EntryPoint>]
let main args =
    let result = run args
    Console.Out.Write result.Stdout

    if result.Stderr.Length > 0 then
        Console.Error.Write result.Stderr

    result.ExitCode
