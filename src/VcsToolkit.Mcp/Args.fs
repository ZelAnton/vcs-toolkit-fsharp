namespace VcsToolkit.Mcp

open System
open VcsToolkit.Forge

/// Parsed command-line arguments for the `vcs-mcp` server.
type McpArgs =
    {
        /// Repository to serve (default: current directory).
        Repo: string
        /// The forge forced for PR/MR tools; `None` = auto-detect from the `origin` remote.
        Forge: ForgeKind option
        /// The server's write policy.
        Writes: WriteGate
        /// Per-command deadline; `None` means no timeout (`--timeout 0`).
        Timeout: TimeSpan option
    }

/// Command-line parsing for the `vcs-mcp` binary.
[<RequireQualifiedAccess>]
module Args =

    /// Default per-command timeout (seconds): a generous ceiling so a stalled fetch/forge
    /// call can't hang a request forever. Override with `--timeout`; `--timeout 0` disables.
    let defaultTimeoutSecs = 120.0

    /// The `--help` text.
    let usage =
        "vcs-mcp — a Model Context Protocol server over a git/jj repository.\n\
         \n\
         USAGE:\n\
             vcs-mcp [OPTIONS]\n\
         \n\
         OPTIONS:\n\
             --repo <path>             Repository to serve (default: current directory)\n\
             --forge <github|gitlab|gitea>\n\
                                       Force the forge for PR/MR tools (default: detect\n\
                                       from the `origin` remote)\n\
             --allow-write             Enable ALL mutating tools (off by default)\n\
             --allow-tools <name,...>  Enable only the named mutating tools (comma-\n\
                                       separated; repeatable). Read tools are always\n\
                                       available. --allow-write wins when both are given.\n\
             --timeout <seconds>       Per-command timeout (default: 120; 0 disables)\n\
             -h, --help                Print this help\n\
         \n\
         The server speaks MCP over stdio; point an agent harness at it via a `mcpServers`\n\
         config entry. The git client is hardened (repo hooks and config disabled)."

    let private parseForge (value: string) : Result<ForgeKind, string> =
        match value with
        | "github" -> Ok ForgeKind.GitHub
        | "gitlab" -> Ok ForgeKind.GitLab
        | "gitea" -> Ok ForgeKind.Gitea
        | other -> Error(sprintf "unknown forge %A (expected github, gitlab, or gitea)" other)

    /// Parse argv (the args AFTER the program name). Returns `Ok None` when `--help` was
    /// requested (the caller prints `usage` and exits 0), `Ok (Some args)` on success, or
    /// `Error msg` on a bad flag/value.
    let parse (argv: string list) : Result<McpArgs option, string> =
        let mutable repo = "."
        let mutable forge = None
        let mutable allowWrite = false
        let mutable allowTools = Set.empty
        let mutable timeout = Some(TimeSpan.FromSeconds defaultTimeoutSecs)
        let mutable rest = argv
        let mutable helpRequested = false
        let mutable error = None

        while (not helpRequested) && Option.isNone error && not (List.isEmpty rest) do
            match rest with
            | ("-h" | "--help") :: _ -> helpRequested <- true
            | "--allow-write" :: tl ->
                allowWrite <- true
                rest <- tl
            | "--allow-tools" :: value :: tl ->
                let names =
                    value.Split(',')
                    |> Array.map (fun s -> s.Trim())
                    |> Array.filter (fun s -> s <> "")
                    |> Array.toList

                if List.isEmpty names then
                    error <-
                        Some(
                            sprintf "--allow-tools %A names no tools (expected e.g. repo_commit,forge_pr_create)" value
                        )
                else
                    match names |> List.tryFind (fun n -> not (WriteTools.asSet.Contains n)) with
                    | Some unknown ->
                        error <-
                            Some(
                                sprintf
                                    "--allow-tools: unknown tool %A; valid write tools are: %s"
                                    unknown
                                    (String.Join(", ", WriteTools.all))
                            )
                    | None ->
                        allowTools <- names |> List.fold (fun acc n -> Set.add n acc) allowTools
                        rest <- tl
            | "--allow-tools" :: [] -> error <- Some "--allow-tools needs a comma-separated list of tool names"
            | "--repo" :: value :: tl ->
                repo <- value
                rest <- tl
            | "--repo" :: [] -> error <- Some "--repo needs a path argument"
            | "--forge" :: value :: tl ->
                match parseForge value with
                | Ok k ->
                    forge <- Some k
                    rest <- tl
                | Error e -> error <- Some e
            | "--forge" :: [] -> error <- Some "--forge needs a value"
            | "--timeout" :: value :: tl ->
                match UInt64.TryParse value with
                | true, secs ->
                    timeout <-
                        (if secs > 0UL then
                             Some(TimeSpan.FromSeconds(float secs))
                         else
                             None)

                    rest <- tl
                | false, _ -> error <- Some(sprintf "invalid --timeout %A (expected a whole number of seconds)" value)
            | "--timeout" :: [] -> error <- Some "--timeout needs a value (whole seconds)"
            | other :: _ -> error <- Some(sprintf "unknown argument: %s (try --help)" other)
            | [] -> ()

        match error with
        | Some e -> Error e
        | None ->
            if helpRequested then
                Ok None
            else
                let writes =
                    if allowWrite then
                        WriteGate.All
                    elif not (Set.isEmpty allowTools) then
                        WriteGate.Set allowTools
                    else
                        WriteGate.None

                Ok(
                    Some
                        { Repo = repo
                          Forge = forge
                          Writes = writes
                          Timeout = timeout }
                )
