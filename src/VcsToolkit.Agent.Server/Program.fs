module Main

open System
open System.Globalization
open System.IO
open System.Reflection
open System.Threading
open VcsToolkit.Agent

type private ParsedCommand =
    { Operation: AgentOperation
      OutputLimitBytes: int
      RepositoryPath: string
      ChangesMode: ChangesMode
      CommitPaths: string list
      CommitMessage: string
      Branch: string
      Remote: string
      Revision: string
      Forge: AgentForgeKind
      Account: string
      TargetBranch: string
      Title: string
      Body: string
      PollInterval: TimeSpan
      Deadline: TimeSpan
      InactivityDeadline: TimeSpan }

type private ParsedOptions =
    { CommandArgs: string list
      SpecifiedOptions: Set<string>
      OutputLimitBytes: int
      RepositoryPath: string
      RepositoryExplicit: bool
      ChangesMode: ChangesMode
      CommitPaths: string list
      CommitMessage: string option
      Branch: string option
      Remote: string option
      Revision: string option
      Forge: AgentForgeKind option
      Account: string option
      TargetBranch: string option
      Title: string option
      Body: string option
      PollInterval: TimeSpan option
      Deadline: TimeSpan option
      InactivityDeadline: TimeSpan option }

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
    let duplicate optionName =
        Error(Agent.invalidInput "command" $"{optionName} may be specified only once")

    let parseSeconds optionName (value: string) =
        match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, seconds when
            Double.IsFinite seconds
            && seconds > 0.0
            && seconds <= Agent.MaxWaitDuration.TotalSeconds
            ->
            Ok(TimeSpan.FromSeconds seconds)
        | _ ->
            Error(
                Agent.invalidInput
                    "command"
                    $"{optionName} must be a positive number of seconds no greater than {Agent.MaxWaitDuration.TotalSeconds}"
            )

    let rec loop (state: ParsedOptions) remaining =
        match remaining with
        | [] -> Ok state
        | "--output-budget" :: value :: rest ->
            match Int32.TryParse value with
            | true, parsed when parsed >= Agent.MinimumOutputLimitBytes ->
                loop
                    { state with
                        OutputLimitBytes = parsed
                        SpecifiedOptions = state.SpecifiedOptions.Add "--output-budget" }
                    rest
            | _ ->
                Error(
                    Agent.invalidInput
                        "command"
                        $"--output-budget must be an integer of at least {Agent.MinimumOutputLimitBytes} bytes"
                )
        | "--output-budget" :: [] -> Error(Agent.invalidInput "command" "--output-budget requires an integer value")
        | "--repo" :: value :: rest ->
            if state.RepositoryExplicit then
                duplicate "--repo"
            else
                loop
                    { state with
                        RepositoryPath = value
                        RepositoryExplicit = true
                        SpecifiedOptions = state.SpecifiedOptions.Add "--repo" }
                    rest
        | "--repo" :: [] -> Error(Agent.invalidInput "command" "--repo requires a path value")
        | "--view" :: value :: rest ->
            match value with
            | "summary" ->
                loop
                    { state with
                        ChangesMode = ChangesMode.Summary
                        SpecifiedOptions = state.SpecifiedOptions.Add "--view" }
                    rest
            | "diff"
            | "structured-diff" ->
                loop
                    { state with
                        ChangesMode = ChangesMode.StructuredDiff
                        SpecifiedOptions = state.SpecifiedOptions.Add "--view" }
                    rest
            | _ -> Error(Agent.invalidInput "command" "--view must be 'summary' or 'diff'")
        | "--view" :: [] -> Error(Agent.invalidInput "command" "--view requires a value")
        | "--path" :: value :: rest ->
            loop
                { state with
                    CommitPaths = value :: state.CommitPaths
                    SpecifiedOptions = state.SpecifiedOptions.Add "--path" }
                rest
        | "--path" :: [] -> Error(Agent.invalidInput "command" "--path requires a repo-relative path value")
        | "--message" :: value :: rest ->
            match state.CommitMessage with
            | Some _ -> duplicate "--message"
            | None ->
                loop
                    { state with
                        CommitMessage = Some value
                        SpecifiedOptions = state.SpecifiedOptions.Add "--message" }
                    rest
        | "--message" :: [] -> Error(Agent.invalidInput "command" "--message requires a value")
        | "--branch" :: value :: rest ->
            match state.Branch with
            | Some _ -> duplicate "--branch"
            | None ->
                loop
                    { state with
                        Branch = Some value
                        SpecifiedOptions = state.SpecifiedOptions.Add "--branch" }
                    rest
        | "--branch" :: [] -> Error(Agent.invalidInput "command" "--branch requires a value")
        | "--remote" :: value :: rest ->
            match state.Remote with
            | Some _ -> duplicate "--remote"
            | None ->
                loop
                    { state with
                        Remote = Some value
                        SpecifiedOptions = state.SpecifiedOptions.Add "--remote" }
                    rest
        | "--remote" :: [] -> Error(Agent.invalidInput "command" "--remote requires a value")
        | "--revision" :: value :: rest ->
            match state.Revision with
            | Some _ -> duplicate "--revision"
            | None ->
                loop
                    { state with
                        Revision = Some value
                        SpecifiedOptions = state.SpecifiedOptions.Add "--revision" }
                    rest
        | "--revision" :: [] -> Error(Agent.invalidInput "command" "--revision requires a value")
        | "--forge" :: value :: rest ->
            match state.Forge with
            | Some _ -> duplicate "--forge"
            | None ->
                match value.ToLowerInvariant() with
                | "github" ->
                    loop
                        { state with
                            Forge = Some AgentForgeKind.GitHub
                            SpecifiedOptions = state.SpecifiedOptions.Add "--forge" }
                        rest
                | "gitlab" ->
                    loop
                        { state with
                            Forge = Some AgentForgeKind.GitLab
                            SpecifiedOptions = state.SpecifiedOptions.Add "--forge" }
                        rest
                | "gitea" ->
                    loop
                        { state with
                            Forge = Some AgentForgeKind.Gitea
                            SpecifiedOptions = state.SpecifiedOptions.Add "--forge" }
                        rest
                | _ -> Error(Agent.invalidInput "command" "--forge must be 'github', 'gitlab', or 'gitea'")
        | "--forge" :: [] -> Error(Agent.invalidInput "command" "--forge requires a value")
        | "--account" :: value :: rest ->
            match state.Account with
            | Some _ -> duplicate "--account"
            | None ->
                loop
                    { state with
                        Account = Some value
                        SpecifiedOptions = state.SpecifiedOptions.Add "--account" }
                    rest
        | "--account" :: [] -> Error(Agent.invalidInput "command" "--account requires a value")
        | "--target" :: value :: rest ->
            match state.TargetBranch with
            | Some _ -> duplicate "--target"
            | None ->
                loop
                    { state with
                        TargetBranch = Some value
                        SpecifiedOptions = state.SpecifiedOptions.Add "--target" }
                    rest
        | "--target" :: [] -> Error(Agent.invalidInput "command" "--target requires a value")
        | "--title" :: value :: rest ->
            match state.Title with
            | Some _ -> duplicate "--title"
            | None ->
                loop
                    { state with
                        Title = Some value
                        SpecifiedOptions = state.SpecifiedOptions.Add "--title" }
                    rest
        | "--title" :: [] -> Error(Agent.invalidInput "command" "--title requires a value")
        | "--body" :: value :: rest ->
            match state.Body with
            | Some _ -> duplicate "--body"
            | None ->
                loop
                    { state with
                        Body = Some value
                        SpecifiedOptions = state.SpecifiedOptions.Add "--body" }
                    rest
        | "--body" :: [] -> Error(Agent.invalidInput "command" "--body requires a value")
        | "--poll-seconds" :: value :: rest ->
            match state.PollInterval with
            | Some _ -> duplicate "--poll-seconds"
            | None ->
                match parseSeconds "--poll-seconds" value with
                | Ok parsed ->
                    loop
                        { state with
                            PollInterval = Some parsed
                            SpecifiedOptions = state.SpecifiedOptions.Add "--poll-seconds" }
                        rest
                | Error error -> Error error
        | "--poll-seconds" :: [] -> Error(Agent.invalidInput "command" "--poll-seconds requires a value")
        | "--deadline-seconds" :: value :: rest ->
            match state.Deadline with
            | Some _ -> duplicate "--deadline-seconds"
            | None ->
                match parseSeconds "--deadline-seconds" value with
                | Ok parsed ->
                    loop
                        { state with
                            Deadline = Some parsed
                            SpecifiedOptions = state.SpecifiedOptions.Add "--deadline-seconds" }
                        rest
                | Error error -> Error error
        | "--deadline-seconds" :: [] -> Error(Agent.invalidInput "command" "--deadline-seconds requires a value")
        | "--inactivity-seconds" :: value :: rest ->
            match state.InactivityDeadline with
            | Some _ -> duplicate "--inactivity-seconds"
            | None ->
                match parseSeconds "--inactivity-seconds" value with
                | Ok parsed ->
                    loop
                        { state with
                            InactivityDeadline = Some parsed
                            SpecifiedOptions = state.SpecifiedOptions.Add "--inactivity-seconds" }
                        rest
                | Error error -> Error error
        | "--inactivity-seconds" :: [] -> Error(Agent.invalidInput "command" "--inactivity-seconds requires a value")
        | token :: rest ->
            loop
                { state with
                    CommandArgs = token :: state.CommandArgs }
                rest

    loop
        { CommandArgs = []
          SpecifiedOptions = Set.empty
          OutputLimitBytes = Agent.DefaultOutputLimitBytes
          RepositoryPath = Directory.GetCurrentDirectory()
          RepositoryExplicit = false
          ChangesMode = ChangesMode.Summary
          CommitPaths = []
          CommitMessage = None
          Branch = None
          Remote = None
          Revision = None
          Forge = None
          Account = None
          TargetBranch = None
          Title = None
          Body = None
          PollInterval = None
          Deadline = None
          InactivityDeadline = None }
        args

let private parse args =
    match parseOptions (List.ofArray args) with
    | Error envelope -> Error envelope
    | Ok options ->
        let operation = tryOperation (List.rev options.CommandArgs)

        let hasIdentityOptions =
            [ options.Branch.IsSome
              options.Remote.IsSome
              options.Revision.IsSome
              options.Forge.IsSome
              options.Account.IsSome
              options.TargetBranch.IsSome
              options.Title.IsSome
              options.Body.IsSome
              options.PollInterval.IsSome
              options.Deadline.IsSome
              options.InactivityDeadline.IsSome ]
            |> List.exists id

        let hasCommitOptions =
            not (List.isEmpty options.CommitPaths) || options.CommitMessage.IsSome

        let requiredIdentity operationName =
            match options.Branch, options.Remote, options.Revision, options.Forge, options.Account with
            | Some branch, Some remote, Some revision, Some forge, Some account ->
                Ok(branch, remote, revision, forge, account)
            | _ ->
                Error(
                    Agent.invalidInput
                        operationName
                        "--repo, --branch, --remote, --revision, --forge, and --account are required"
                )

        match operation with
        | Some AgentOperation.Commit when not options.RepositoryExplicit ->
            Error(Agent.invalidInput "commit" "commit requires an explicit --repo path")
        | Some AgentOperation.Publish when not options.RepositoryExplicit ->
            Error(Agent.invalidInput "publish" "publish requires an explicit --repo path")
        | Some AgentOperation.CiStatus when not options.RepositoryExplicit ->
            Error(Agent.invalidInput "ci.status" "ci status requires an explicit --repo path")
        | Some AgentOperation.CiWait when not options.RepositoryExplicit ->
            Error(Agent.invalidInput "ci.wait" "ci wait requires an explicit --repo path")
        | Some AgentOperation.Publish when
            hasCommitOptions
            || options.PollInterval.IsSome
            || options.Deadline.IsSome
            || options.InactivityDeadline.IsSome
            ->
            Error(Agent.invalidInput "publish" "publish received options for another operation")
        | Some AgentOperation.CiStatus when
            hasCommitOptions
            || options.TargetBranch.IsSome
            || options.Title.IsSome
            || options.Body.IsSome
            || options.PollInterval.IsSome
            || options.Deadline.IsSome
            || options.InactivityDeadline.IsSome
            ->
            Error(Agent.invalidInput "ci.status" "ci status received options for another operation")
        | Some AgentOperation.CiWait when
            hasCommitOptions
            || options.TargetBranch.IsSome
            || options.Title.IsSome
            || options.Body.IsSome
            ->
            Error(Agent.invalidInput "ci.wait" "ci wait received options for another operation")
        | Some(AgentOperation.Probe | AgentOperation.Inspect | AgentOperation.Changes | AgentOperation.Commit) when
            hasIdentityOptions
            ->
            Error(Agent.invalidInput "command" "identity options require publish, ci status, or ci wait")
        | Some operation when not (Set.isSubset options.SpecifiedOptions (Agent.cliOptions operation |> Set.ofList)) ->
            let invalid =
                Set.difference options.SpecifiedOptions (Agent.cliOptions operation |> Set.ofList)
                |> Set.toList
                |> String.concat ", "

            Error(Agent.invalidInput (Agent.operationName operation) $"operation does not accept: {invalid}")
        | Some operation ->
            match requiredIdentity (Agent.operationName operation), operation with
            | Error error, (AgentOperation.Publish | AgentOperation.CiStatus | AgentOperation.CiWait) -> Error error
            | identity, _ ->
                let branch, remote, revision, forge, account =
                    match identity with
                    | Ok values -> values
                    | Error _ -> "", "", "", AgentForgeKind.GitHub, ""

                match operation, options.TargetBranch, options.Title with
                | AgentOperation.Publish, None, _ ->
                    Error(Agent.invalidInput "publish" "publish requires an explicit --target branch")
                | AgentOperation.Publish, _, None ->
                    Error(Agent.invalidInput "publish" "publish requires an explicit --title")
                | _ ->
                    Ok
                        { Operation = operation
                          OutputLimitBytes = options.OutputLimitBytes
                          RepositoryPath = options.RepositoryPath
                          ChangesMode = options.ChangesMode
                          CommitPaths = List.rev options.CommitPaths
                          CommitMessage = options.CommitMessage |> Option.defaultValue ""
                          Branch = branch
                          Remote = remote
                          Revision = revision
                          Forge = forge
                          Account = account
                          TargetBranch = options.TargetBranch |> Option.defaultValue ""
                          Title = options.Title |> Option.defaultValue ""
                          Body = options.Body |> Option.defaultValue ""
                          PollInterval = options.PollInterval |> Option.defaultValue (TimeSpan.FromSeconds 5.0)
                          Deadline = options.Deadline |> Option.defaultValue (TimeSpan.FromMinutes 30.0)
                          InactivityDeadline =
                            options.InactivityDeadline |> Option.defaultValue (TimeSpan.FromMinutes 10.0) }
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
                | AgentOperation.Commit ->
                    Agent.commit
                        { RepositoryPath = command.RepositoryPath
                          Paths = command.CommitPaths
                          Message = command.CommitMessage
                          OutputLimitBytes = command.OutputLimitBytes }
                        cancellationToken
                | AgentOperation.Publish ->
                    Agent.publish
                        { RepositoryPath = command.RepositoryPath
                          Branch = command.Branch
                          Remote = command.Remote
                          Revision = command.Revision
                          Forge = command.Forge
                          Account = command.Account
                          TargetBranch = command.TargetBranch
                          Title = command.Title
                          Body = command.Body
                          OutputLimitBytes = command.OutputLimitBytes }
                        cancellationToken
                | AgentOperation.CiStatus ->
                    Agent.ciStatus
                        { RepositoryPath = command.RepositoryPath
                          Forge = command.Forge
                          Account = command.Account
                          Branch = command.Branch
                          Remote = command.Remote
                          Revision = command.Revision
                          OutputLimitBytes = command.OutputLimitBytes }
                        cancellationToken
                | AgentOperation.CiWait ->
                    Agent.ciWait
                        { RepositoryPath = command.RepositoryPath
                          Forge = command.Forge
                          Account = command.Account
                          Branch = command.Branch
                          Remote = command.Remote
                          Revision = command.Revision
                          PollInterval = command.PollInterval
                          Deadline = command.Deadline
                          InactivityDeadline = command.InactivityDeadline
                          OutputLimitBytes = command.OutputLimitBytes }
                        cancellationToken

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
