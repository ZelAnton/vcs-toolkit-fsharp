module Main

open System
open System.IO
open System.Reflection
open System.Threading
open VcsToolkit.Agent

type private ParsedCommand =
    { Operation: AgentOperation
      OutputLimitBytes: int
      RepositoryPath: string
      ChangesMode: ChangesMode }

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

let private parseOptions args =
    let rec loop commandArgs outputLimit repositoryPath changesMode remaining =
        match remaining with
        | [] -> Ok(List.rev commandArgs, outputLimit, repositoryPath, changesMode)
        | "--output-budget" :: value :: rest ->
            match Int32.TryParse value with
            | true, parsed when parsed >= Agent.MinimumOutputLimitBytes ->
                loop commandArgs parsed repositoryPath changesMode rest
            | _ ->
                Error(
                    Agent.invalidInput
                        "command"
                        $"--output-budget must be an integer of at least {Agent.MinimumOutputLimitBytes} bytes"
                )
        | "--output-budget" :: [] -> Error(Agent.invalidInput "command" "--output-budget requires an integer value")
        | "--repo" :: value :: rest -> loop commandArgs outputLimit value changesMode rest
        | "--repo" :: [] -> Error(Agent.invalidInput "command" "--repo requires a path value")
        | "--view" :: value :: rest ->
            match value with
            | "summary" -> loop commandArgs outputLimit repositoryPath ChangesMode.Summary rest
            | "diff"
            | "structured-diff" -> loop commandArgs outputLimit repositoryPath ChangesMode.StructuredDiff rest
            | _ -> Error(Agent.invalidInput "command" "--view must be 'summary' or 'diff'")
        | "--view" :: [] -> Error(Agent.invalidInput "command" "--view requires a value")
        | token :: rest -> loop (token :: commandArgs) outputLimit repositoryPath changesMode rest

    loop [] Agent.DefaultOutputLimitBytes (Directory.GetCurrentDirectory()) ChangesMode.Summary args

let private parse args =
    match parseOptions (List.ofArray args) with
    | Error envelope -> Error envelope
    | Ok(commandArgs, outputLimit, repositoryPath, changesMode) ->
        match tryOperation commandArgs with
        | Some operation ->
            Ok
                { Operation = operation
                  OutputLimitBytes = outputLimit
                  RepositoryPath = repositoryPath
                  ChangesMode = changesMode }
        | None -> Error(Agent.invalidInput "command" "unknown or incomplete operation")

let internal runWithCancellation args cancellationToken =
    task {
        match parse args with
        | Error envelope -> return AgentWire.render Agent.DefaultOutputLimitBytes envelope
        | Ok command ->
            let! envelope =
                match command.Operation with
                | AgentOperation.Probe -> task { return Agent.probe (toolVersion ()) }
                | AgentOperation.Inspect ->
                    Agent.inspect
                        { RepositoryPath = command.RepositoryPath
                          OutputLimitBytes = command.OutputLimitBytes }
                        cancellationToken
                | AgentOperation.Changes ->
                    Agent.changes
                        { RepositoryPath = command.RepositoryPath
                          Mode = command.ChangesMode
                          OutputLimitBytes = command.OutputLimitBytes }
                        cancellationToken
                | operation -> task { return Agent.unsupported operation }

            return AgentWire.render command.OutputLimitBytes envelope
    }

let internal run args =
    runWithCancellation args CancellationToken.None
    |> fun pending -> pending.GetAwaiter().GetResult()

[<EntryPoint>]
let main args =
    use cts = new CancellationTokenSource()

    let cancelHandler =
        ConsoleCancelEventHandler(fun _ eventArgs ->
            eventArgs.Cancel <- true
            cts.Cancel())

    Console.CancelKeyPress.AddHandler cancelHandler

    let result =
        runWithCancellation args cts.Token
        |> fun pending -> pending.GetAwaiter().GetResult()

    Console.CancelKeyPress.RemoveHandler cancelHandler
    Console.Out.Write result.Stdout

    if result.Stderr.Length > 0 then
        Console.Error.Write result.Stderr

    result.ExitCode
