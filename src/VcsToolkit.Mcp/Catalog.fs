namespace VcsToolkit.Mcp

open System.Text.Json
open System.Threading.Tasks

/// One argument of a tool, for the advertised input schema.
type internal ToolParam =
    {
        /// Property name (the JSON key the client sends).
        Name: string
        /// JSON type (`"string"`, `"integer"`, `"boolean"`, or `"array"` of strings).
        JsonType: string
        /// Human-readable description.
        Description: string
        /// Whether the argument is required.
        Required: bool
    }

/// A tool's advertised metadata: name, description, the read-only/destructive/idempotent
/// hints, and its parameters (→ input schema). The handler dispatch lives in `Catalog`.
type internal ToolSpec =
    { Name: string
      Description: string
      ReadOnly: bool
      Destructive: bool
      Idempotent: bool
      Params: ToolParam list }

/// The tool catalogue (metadata + input schemas) and the `callTool` dispatcher that parses a
/// tool call's JSON arguments and invokes the matching `VcsMcpServer` method. This is the
/// seam the `vcs-mcp` binary wires to the MCP SDK's list-tools / call-tool handlers — and
/// it's fully testable without the SDK.
[<RequireQualifiedAccess>]
module internal Catalog =

    // --- argument extraction ----------------------------------------------

    let private missing (name: string) : McpError =
        McpError.InvalidParams(sprintf "missing or wrong-typed required argument %A" name)

    let private reqStr (args: JsonElement) (name: string) : Result<string, McpError> =
        match args.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String ->
            match v.GetString() with
            | null -> Error(missing name)
            | s -> Ok s
        | _ -> Error(missing name)

    let private optStr (args: JsonElement) (name: string) : string option =
        match args.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj
        | _ -> None

    let private reqU64 (args: JsonElement) (name: string) : Result<uint64, McpError> =
        match args.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetUInt64() with
            | true, n -> Ok n
            | false, _ -> Error(McpError.InvalidParams(sprintf "argument %A must be a non-negative integer" name))
        | _ -> Error(missing name)

    let private reqStrArray (args: JsonElement) (name: string) : Result<string list, McpError> =
        match args.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            let items = [ for e in v.EnumerateArray() -> e ]

            if items |> List.forall (fun e -> e.ValueKind = JsonValueKind.String) then
                // Each element is String-kind, so GetString() is non-null; coalesce to satisfy
                // the nullness checker.
                Ok(
                    items
                    |> List.map (fun e ->
                        match e.GetString() with
                        | null -> ""
                        | s -> s)
                )
            else
                Error(McpError.InvalidParams(sprintf "argument %A must be an array of strings" name))
        | _ -> Error(missing name)

    /// `optBool` — a missing/absent boolean defaults to `false` (matches the Rust
    /// `#[serde(default)]` on `force`/`delete_branch`).
    let private optBool (args: JsonElement) (name: string) : bool =
        match args.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.True -> true
        | _ -> false

    // --- reusable param specs ---------------------------------------------

    let private pReference =
        { Name = "reference"
          JsonType = "string"
          Description = "The branch, bookmark, or revision to switch to."
          Required = true }

    let private pNumber =
        { Name = "number"
          JsonType = "integer"
          Description = "The PR/MR number (GitLab uses the project-scoped iid)."
          Required = true }

    let private pIssueNumber =
        { Name = "number"
          JsonType = "integer"
          Description = "The issue number (GitLab uses the project-scoped iid)."
          Required = true }

    // --- the catalogue -----------------------------------------------------

    /// Every tool, in registration order (read tools first per group, then gated writes).
    let all: ToolSpec list =
        let read name desc ps =
            { Name = name
              Description = desc
              ReadOnly = true
              Destructive = false
              Idempotent = false
              Params = ps }

        let write name desc ps =
            { Name = name
              Description =
                desc
                + " Requires write access (--allow-write, or --allow-tools naming this tool)."
              ReadOnly = false
              Destructive = true
              Idempotent = false
              Params = ps }

        [ read
              "repo_snapshot"
              "A batched snapshot of the repo state: branch, upstream, ahead/behind, HEAD, dirtiness, change count, conflict, and operation state."
              []
          read
              "repo_info"
              "Which backend (git/jj), the repository root, the working directory, and the configured forge (if any)."
              []
          read "repo_status" "The working-copy changes (added/modified/deleted/renamed paths)." []
          read "repo_diff_stat" "Aggregate insertion/deletion/file counts for the working copy." []
          read "repo_branches" "Local branch (git) / bookmark (jj) names." []
          read "repo_current_branch" "The current branch/bookmark (null when detached/unset)." []
          read "repo_conflicts" "Paths with unresolved merge conflicts (repo-relative, '/'-separated)." []
          read "repo_worktrees" "Attached worktrees (git) / workspaces (jj)." []
          read
              "repo_show_file"
              "The content of a file as it exists at a revision (untrimmed). `rev` is passed through as-is to the backend — a git commit-ish or a jj revset; the two syntaxes are NOT cross-backend portable."
              [ { Name = "rev"
                  JsonType = "string"
                  Description = "The revision (git: commit-ish) or revset (jj) to read the file at."
                  Required = true }
                { Name = "path"
                  JsonType = "string"
                  Description = "Repo-relative path of the file to read."
                  Required = true } ]

          // repo_try_merge is write-gated (a real, rolled-back trial merge) but non-destructive/idempotent.
          { Name = "repo_try_merge"
            Description =
              "Probe whether merging `source` into the current work would conflict, WITHOUT leaving a trace (always rolled back). It spawns a REAL trial merge that materializes content, so — like checkout — it is write-gated. Requires write access (--allow-write, or --allow-tools naming this tool)."
            ReadOnly = false
            Destructive = false
            Idempotent = true
            Params =
              [ { Name = "source"
                  JsonType = "string"
                  Description = "The branch/revision to probe merging into the current work."
                  Required = true } ] }

          write
              "repo_commit"
              "Commit exactly the given paths with a message."
              [ { Name = "paths"
                  JsonType = "array"
                  Description = "Repo-relative paths to commit (and nothing else)."
                  Required = true }
                { Name = "message"
                  JsonType = "string"
                  Description = "The commit message."
                  Required = true } ]
          write
              "repo_checkout"
              "Switch the working copy to a branch/bookmark/revision (git checkout / jj edit)."
              [ pReference ]
          write "repo_fetch" "Fetch from the default remote (git fetch / jj git fetch)." []
          write
              "repo_push"
              "Push an existing branch/bookmark to origin."
              [ { Name = "branch"
                  JsonType = "string"
                  Description = "The existing local branch (git) / bookmark (jj) to push."
                  Required = true } ]
          write
              "repo_create_worktree"
              "Create a worktree/workspace at `path` on a new `branch` from `base`."
              [ { Name = "path"
                  JsonType = "string"
                  Description = "Filesystem path for the new worktree/workspace."
                  Required = true }
                { Name = "branch"
                  JsonType = "string"
                  Description = "The new branch/bookmark to create on it."
                  Required = true }
                { Name = "base"
                  JsonType = "string"
                  Description = "The base revision to start it from."
                  Required = true } ]
          write
              "repo_remove_worktree"
              "Remove the worktree/workspace at `path` (the main worktree is always refused)."
              [ { Name = "path"
                  JsonType = "string"
                  Description = "Filesystem path of the worktree/workspace to remove."
                  Required = true }
                { Name = "force"
                  JsonType = "boolean"
                  Description = "Force removal even when the worktree has uncommitted changes."
                  Required = false } ]

          read "forge_auth_status" "Whether the forge CLI reports an authenticated session." []
          read "forge_repo_view" "The repository/project on the configured forge (Unsupported on Gitea)." []
          read "forge_info" "The forge's identity and flat capability map." []
          read "forge_pr_list" "Open pull/merge requests on the configured forge (up to 100)." []
          read "forge_pr_view" "A single pull/merge request by number." [ pNumber ]
          read "forge_pr_checks" "The PR/MR's coarse CI status (Unsupported on Gitea)." [ pNumber ]
          read "forge_issue_list" "Open issues on the configured forge (up to 100)." []
          read "forge_issue_view" "A single issue by number, with body and URL filled." [ pIssueNumber ]
          read "forge_release_list" "Releases on the configured forge, newest first (up to 100)." []
          read
              "forge_release_view"
              "A single release by tag (Unsupported on Gitea — filter forge_release_list instead)."
              [ { Name = "tag"
                  JsonType = "string"
                  Description = "The release's Git tag."
                  Required = true } ]

          write
              "forge_issue_create"
              "Open an issue, returning the CLI's output (the URL on success)."
              [ { Name = "title"
                  JsonType = "string"
                  Description = "Title."
                  Required = true }
                { Name = "body"
                  JsonType = "string"
                  Description = "Body / description."
                  Required = true } ]
          write
              "forge_pr_create"
              "Open a pull/merge request, returning the CLI's output (the URL on success)."
              [ { Name = "title"
                  JsonType = "string"
                  Description = "Title."
                  Required = true }
                { Name = "body"
                  JsonType = "string"
                  Description = "Body / description."
                  Required = true }
                { Name = "source"
                  JsonType = "string"
                  Description = "Source/head branch; omit for the current branch."
                  Required = false }
                { Name = "target"
                  JsonType = "string"
                  Description = "Target/base branch; omit for the repo default."
                  Required = false } ]
          write
              "forge_pr_merge"
              "Merge a pull/merge request with a strategy (merge|squash|rebase)."
              [ pNumber
                { Name = "strategy"
                  JsonType = "string"
                  Description = "Merge strategy: merge, squash, or rebase."
                  Required = true } ]
          write
              "forge_pr_close"
              "Close a pull/merge request without merging."
              [ pNumber
                { Name = "delete_branch"
                  JsonType = "boolean"
                  Description = "Also delete the source branch (GitHub only)."
                  Required = false } ]
          write
              "forge_pr_mark_ready"
              "Mark a draft pull/merge request as ready for review (Unsupported on Gitea)."
              [ pNumber ]
          write
              "forge_pr_comment"
              "Post a comment to an existing pull/merge request, returning the CLI's output."
              [ pNumber
                { Name = "body"
                  JsonType = "string"
                  Description = "The markdown comment body."
                  Required = true } ]
          write
              "forge_pr_edit"
              "Edit a pull/merge request's title and/or body (at least one required)."
              [ pNumber
                { Name = "title"
                  JsonType = "string"
                  Description = "The new title; omit to leave it alone."
                  Required = false }
                { Name = "body"
                  JsonType = "string"
                  Description = "The new body; omit to leave it alone."
                  Required = false } ] ]

    /// The JSON-Schema `inputSchema` object for a tool spec.
    let inputSchema (spec: ToolSpec) : string =
        let jsonStr (s: string) = JsonSerializer.Serialize s

        let propSchema (p: ToolParam) =
            match p.JsonType with
            | "array" ->
                sprintf
                    "{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":%s}"
                    (jsonStr p.Description)
            | t -> sprintf "{\"type\":%s,\"description\":%s}" (jsonStr t) (jsonStr p.Description)

        let props =
            spec.Params
            |> List.map (fun p -> sprintf "%s:%s" (jsonStr p.Name) (propSchema p))
            |> String.concat ","

        let required =
            spec.Params
            |> List.filter (fun p -> p.Required)
            |> List.map (fun p -> jsonStr p.Name)
            |> String.concat ","

        sprintf "{\"type\":\"object\",\"properties\":{%s},\"required\":[%s]}" props required

    // --- dispatch ----------------------------------------------------------

    /// Bind an arg-extraction `Result` and, on success, run the server call.
    let private bind (r: Result<'a, McpError>) (f: 'a -> Task<Result<string, McpError>>) =
        task {
            match r with
            | Error e -> return Error e
            | Ok v -> return! f v
        }

    /// Invoke the tool `name` on `server`, parsing `args` (the call's `arguments` object).
    /// An unknown tool or a bad/missing argument is an `InvalidParams` error.
    let callTool (server: VcsMcpServer) (name: string) (args: JsonElement) : Task<Result<string, McpError>> =
        match name with
        | "repo_snapshot" -> server.RepoSnapshot()
        | "repo_info" -> server.RepoInfo()
        | "repo_status" -> server.RepoStatus()
        | "repo_diff_stat" -> server.RepoDiffStat()
        | "repo_branches" -> server.RepoBranches()
        | "repo_current_branch" -> server.RepoCurrentBranch()
        | "repo_conflicts" -> server.RepoConflicts()
        | "repo_worktrees" -> server.RepoWorktrees()
        | "repo_show_file" ->
            bind (reqStr args "rev") (fun rev -> bind (reqStr args "path") (fun path -> server.RepoShowFile(rev, path)))
        | "repo_try_merge" -> bind (reqStr args "source") server.RepoTryMerge
        | "repo_commit" ->
            bind (reqStrArray args "paths") (fun paths ->
                bind (reqStr args "message") (fun message -> server.RepoCommit(paths, message)))
        | "repo_checkout" -> bind (reqStr args "reference") server.RepoCheckout
        | "repo_fetch" -> server.RepoFetch()
        | "repo_push" -> bind (reqStr args "branch") server.RepoPush
        | "repo_create_worktree" ->
            bind (reqStr args "path") (fun path ->
                bind (reqStr args "branch") (fun branch ->
                    bind (reqStr args "base") (fun baseRef -> server.RepoCreateWorktree(path, branch, baseRef))))
        | "repo_remove_worktree" ->
            bind (reqStr args "path") (fun path -> server.RepoRemoveWorktree(path, optBool args "force"))
        | "forge_auth_status" -> server.ForgeAuthStatus()
        | "forge_repo_view" -> server.ForgeRepoView()
        | "forge_info" -> server.ForgeInfo()
        | "forge_pr_list" -> server.ForgePrList()
        | "forge_pr_view" -> bind (reqU64 args "number") server.ForgePrView
        | "forge_pr_checks" -> bind (reqU64 args "number") server.ForgePrChecks
        | "forge_issue_list" -> server.ForgeIssueList()
        | "forge_issue_view" -> bind (reqU64 args "number") server.ForgeIssueView
        | "forge_release_list" -> server.ForgeReleaseList()
        | "forge_release_view" -> bind (reqStr args "tag") server.ForgeReleaseView
        | "forge_issue_create" ->
            bind (reqStr args "title") (fun title ->
                bind (reqStr args "body") (fun body -> server.ForgeIssueCreate(title, body)))
        | "forge_pr_create" ->
            bind (reqStr args "title") (fun title ->
                bind (reqStr args "body") (fun body ->
                    server.ForgePrCreate(title, body, optStr args "source", optStr args "target")))
        | "forge_pr_merge" ->
            bind (reqU64 args "number") (fun number ->
                bind (reqStr args "strategy") (fun strategy -> server.ForgePrMerge(number, strategy)))
        | "forge_pr_close" ->
            bind (reqU64 args "number") (fun number -> server.ForgePrClose(number, optBool args "delete_branch"))
        | "forge_pr_mark_ready" -> bind (reqU64 args "number") server.ForgePrMarkReady
        | "forge_pr_comment" ->
            bind (reqU64 args "number") (fun number ->
                bind (reqStr args "body") (fun body -> server.ForgePrComment(number, body)))
        | "forge_pr_edit" ->
            bind (reqU64 args "number") (fun number ->
                server.ForgePrEdit(number, optStr args "title", optStr args "body"))
        | unknown -> task { return Error(McpError.InvalidParams(sprintf "unknown tool %A" unknown)) }
