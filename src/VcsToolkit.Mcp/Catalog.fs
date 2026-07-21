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

    /// `optStr` — a missing/absent string is optional; a present value must be a string.
    let private optStr (args: JsonElement) (name: string) : Result<string option, McpError> =
        match args.TryGetProperty name with
        | false, _ -> Ok Option.None
        | true, v when v.ValueKind = JsonValueKind.String ->
            match v.GetString() with
            | null -> Error(McpError.InvalidParams(sprintf "argument %A must be a string" name))
            | s -> Ok(Some s)
        | true, _ -> Error(McpError.InvalidParams(sprintf "argument %A must be a string" name))

    /// `optInt` — a missing/absent integer is optional; a present value must be a JSON
    /// integer that fits `int32`.
    let private optInt (args: JsonElement) (name: string) : Result<int option, McpError> =
        match args.TryGetProperty name with
        | false, _ -> Ok Option.None
        | true, v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetInt32() with
            | true, n -> Ok(Some n)
            | false, _ -> Error(McpError.InvalidParams(sprintf "argument %A must be an integer" name))
        | true, _ -> Error(McpError.InvalidParams(sprintf "argument %A must be an integer" name))

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
    /// `#[serde(default)]` on `force`/`delete_branch`); a present value must be boolean.
    let private optBool (args: JsonElement) (name: string) : Result<bool, McpError> =
        match args.TryGetProperty name with
        | false, _ -> Ok false
        | true, v when v.ValueKind = JsonValueKind.True -> Ok true
        | true, v when v.ValueKind = JsonValueKind.False -> Ok false
        | true, _ -> Error(McpError.InvalidParams(sprintf "argument %A must be a boolean" name))

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

    let private pPrListState =
        { Name = "state"
          JsonType = "string"
          Description = "Filter by state: open, closed, merged, or all. Defaults to open."
          Required = false }

    let private pIssueListState =
        { Name = "state"
          JsonType = "string"
          Description = "Filter by state: open, closed, or all. Defaults to open."
          Required = false }

    let private pSourceBranch =
        { Name = "source_branch"
          JsonType = "string"
          Description = "The PR/MR's source (head) branch to search for."
          Required = true }

    let private pListLimit =
        { Name = "limit"
          JsonType = "integer"
          Description = "Maximum number of results (must be positive). Defaults to 100."
          Required = false }

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

        // `destructive`/`idempotent` are per-tool, evaluated on the tool's actual worst-case
        // semantics rather than a single shared default: destructive = the call (or one of its
        // optional parameters, e.g. `force`/`delete_branch`) can irrecoverably discard data;
        // idempotent = calling twice with the same arguments leaves the same end state as
        // calling once (a creating call is never idempotent — it produces a new entity/commit
        // each time).
        let write name desc destructive idempotent ps =
            { Name = name
              Description =
                desc
                + " Requires write access (--allow-write, or --allow-tools naming this tool)."
              ReadOnly = false
              Destructive = destructive
              Idempotent = idempotent
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
              "repo_remotes"
              "The configured remotes (name and URL) — git `remote -v` (deduplicated to one entry per remote, carrying its fetch URL) / jj `jj git remote list`."
              []
          read
              "repo_show_file"
              "The content of a file as it exists at a revision, untrimmed up to the server's output budget (--output-budget; default 200000 bytes, 0 disables). Content beyond the budget is truncated with a trailing '[truncated: showing N of M bytes]' marker. The content is UTF-8-decoded text: a non-UTF-8 byte (a binary or legacy-encoded blob) is replaced with U+FFFD and does NOT round-trip, so this tool is for text files — a byte-exact read of arbitrary binary content is a library-level concern (VcsToolkit.Core Repo.ShowFileBytes), not exposed over this text-only MCP surface. `rev` is passed through as-is to the backend — a git commit-ish or a jj revset; the two syntaxes are NOT cross-backend portable."
              [ { Name = "rev"
                  JsonType = "string"
                  Description = "The revision (git: commit-ish) or revset (jj) to read the file at."
                  Required = true }
                { Name = "path"
                  JsonType = "string"
                  Description = "Repo-relative path of the file to read."
                  Required = true } ]
          read
              "repo_log"
              "Recent history: up to `max` commits reachable from `revspec_or_revset` (a git revspec, e.g. \"HEAD\", or a jj revset, e.g. \"@\"), most-recent-first. `author`/`date` are null on jj — its typed log doesn't currently surface authorship or a timestamp."
              [ { Name = "revspec_or_revset"
                  JsonType = "string"
                  Description =
                    "The revspec (git) / revset (jj) to list history from, e.g. \"HEAD\" (git) or \"@\" (jj)."
                  Required = true }
                { Name = "max"
                  JsonType = "integer"
                  Description = "Maximum number of commits to return."
                  Required = true } ]
          read
              "repo_annotate"
              "Per-line authorship of a file at a revision — who last touched each line, and when (git blame --line-porcelain / jj file annotate). `path` is anchored at the repository root on both backends, not relative to the server's working directory. The result is a JSON array of lines, truncated to the server's output budget the same way repo_show_file is (--output-budget; default 200000 bytes, 0 disables), with a trailing '[truncated: showing N of M bytes]' marker when it is. `rev` is passed through as-is to the backend — a git commit-ish or a jj revset; the two syntaxes are NOT cross-backend portable."
              [ { Name = "path"
                  JsonType = "string"
                  Description = "Repo-relative path of the file to annotate."
                  Required = true }
                { Name = "rev"
                  JsonType = "string"
                  Description =
                    "The revision (git commit-ish) or revset (jj) to annotate at; omit to annotate the working copy / `@`."
                  Required = false } ]

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
              false
              false
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
              false
              true
              [ pReference ]
          write "repo_fetch" "Fetch from the default remote (git fetch / jj git fetch)." false true []
          // Non-destructive: fast-forward-only (no `force` param, so a diverged remote is
          // refused rather than overwritten); idempotent: re-pushing an already-up-to-date
          // branch is a no-op.
          write
              "repo_push"
              "Push an existing branch/bookmark to origin."
              false
              true
              [ { Name = "branch"
                  JsonType = "string"
                  Description = "The existing local branch (git) / bookmark (jj) to push."
                  Required = true } ]
          write
              "repo_create_worktree"
              "Create a worktree/workspace at `path` on a new `branch` from `base`."
              false
              false
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
              true
              false
              [ { Name = "path"
                  JsonType = "string"
                  Description = "Filesystem path of the worktree/workspace to remove."
                  Required = true }
                { Name = "force"
                  JsonType = "boolean"
                  Description = "Force removal even when the worktree has uncommitted changes."
                  Required = false } ]
          // Destructive: rewrites the current work's commits onto a new base; not idempotent:
          // rebasing again onto the same target is not a guaranteed no-op (the work has already
          // moved, and a second run can conflict or re-rewrite).
          write
              "repo_rebase"
              "Rebase the current work onto `onto` (git rebase / jj rebase -d). Rewrites the current branch's commits onto a new base."
              true
              false
              [ { Name = "onto"
                  JsonType = "string"
                  Description = "The branch/revision to rebase the current work onto."
                  Required = true } ]
          // Destructive: discards the in-progress operation's partial progress (git merge/rebase
          // --abort throws away the paused state); idempotent: aborting when nothing is in
          // progress is a no-op that reports the same Clear state.
          write
              "repo_abort_in_progress"
              "Abort the in-progress operation, if any (git: merge/rebase --abort; jj: a no-op). Reports the fresh post-call operation state (Clear once nothing is in progress)."
              true
              true
              []
          // Non-destructive: completes the paused operation (commits the merge / advances the
          // rebase) without discarding commits; not idempotent: a second call on an
          // already-finished operation reports the (Clear/Conflict) state rather than re-running it.
          write
              "repo_continue_in_progress"
              "Continue the in-progress operation after conflict resolution (git: commit --no-edit for a merge / rebase --continue; jj: a no-op). Reports the fresh post-call operation state (Conflict when unresolved paths still block, Clear when finished)."
              false
              false
              []
          // Destructive: with `force` (git only) it deletes even an unmerged branch, discarding
          // its unique commits; not idempotent: deleting an already-absent branch errors.
          write
              "repo_delete_branch"
              "Delete a local branch (git) / bookmark (jj)."
              true
              false
              [ { Name = "name"
                  JsonType = "string"
                  Description = "The local branch (git) / bookmark (jj) to delete."
                  Required = true }
                { Name = "force"
                  JsonType = "boolean"
                  Description =
                    "Delete even an unmerged branch, discarding its unique commits (git only; jj ignores it)."
                  Required = false } ]
          // Non-destructive: moves the ref name, keeping the commits; not idempotent: renaming an
          // already-renamed (now-absent) branch errors.
          write
              "repo_rename_branch"
              "Rename a local branch (git) / bookmark (jj). Preserves the commits."
              false
              false
              [ { Name = "old_name"
                  JsonType = "string"
                  Description = "The existing local branch (git) / bookmark (jj) to rename."
                  Required = true }
                { Name = "new_name"
                  JsonType = "string"
                  Description = "The new name."
                  Required = true } ]
          // Non-destructive: starts fresh work on top of `reference` and leaves it untouched; not
          // idempotent: on jj each call creates a new child change.
          write
              "repo_new_child"
              "Start new work on top of `reference` WITHOUT modifying it (git checkout <reference> / jj new <reference>) — the backend-agnostic 'start fresh on top of main' that, unlike repo_checkout, does not rewrite `reference` in place on jj."
              false
              false
              [ { Name = "reference"
                  JsonType = "string"
                  Description = "The branch, bookmark, or revision to start the new child work on top of."
                  Required = true } ]

          read "forge_auth_status" "Whether the forge CLI reports an authenticated session." []
          read "forge_repo_view" "The repository/project on the configured forge (Unsupported on Gitea)." []
          read "forge_info" "The forge's identity and flat capability map." []
          read
              "forge_pr_list"
              "Pull/merge requests on the configured forge, open by default and capped at 100 by default. Optional state/limit filter and cap the results. Unsupported on Gitea for every state (tea's `pr list --output json` does not work against the real CLI)."
              [ pPrListState; pListLimit ]
          read "forge_pr_view" "A single pull/merge request by number." [ pNumber ]
          read
              "forge_pr_for_branch"
              "Pull/merge requests whose source branch is source_branch, in any state, regardless of target branch — the 'after pushing, find my PR' query. Returns a list (a branch can have more than one PR/MR over its lifetime); an empty list means none currently match. Unsupported on Gitea (tea's `pr list --output json` does not work against the real CLI)."
              [ pSourceBranch ]
          read "forge_pr_checks" "The PR/MR's coarse CI status (Unsupported on Gitea)." [ pNumber ]
          read
              "forge_issue_list"
              "Issues on the configured forge, open by default and capped at 100 by default. Optional state/limit filter and cap the results. Unsupported on Gitea for every state (tea's `issues list --output json` does not work against the real CLI)."
              [ pIssueListState; pListLimit ]
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
              false
              false
              [ { Name = "title"
                  JsonType = "string"
                  Description = "Title."
                  Required = true }
                { Name = "body"
                  JsonType = "string"
                  Description = "Body / description."
                  Required = true } ]
          // Non-destructive: closing an issue is a reversible status change (reopenable),
          // discarding no data; idempotent: closing an already-closed issue is a no-op.
          write "forge_issue_close" "Close an issue (reopenable)." false true [ pIssueNumber ]
          write
              "forge_issue_comment"
              "Post a comment to an existing issue, returning the CLI's output."
              false
              false
              [ pIssueNumber
                { Name = "body"
                  JsonType = "string"
                  Description = "The markdown comment body."
                  Required = true } ]
          write
              "forge_pr_create"
              "Open a pull/merge request, returning the CLI's output (the URL on success)."
              false
              false
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
          // Destructive: `delete_branch=true` deletes the source branch; not idempotent:
          // merging an already-merged PR errors, and `auto` makes the outcome non-deterministic.
          write
              "forge_pr_merge"
              "Merge a pull/merge request with a strategy (merge|squash|rebase). auto/delete_branch are GitHub-only; on GitLab/Gitea either is refused as Unsupported."
              true
              false
              [ pNumber
                { Name = "strategy"
                  JsonType = "string"
                  Description = "Merge strategy: merge, squash, or rebase."
                  Required = true }
                { Name = "auto"
                  JsonType = "boolean"
                  Description = "Enable auto-merge — merge once requirements are met (GitHub only)."
                  Required = false }
                { Name = "delete_branch"
                  JsonType = "boolean"
                  Description = "Delete the source branch after merging (GitHub only)."
                  Required = false } ]
          // Destructive: `delete_branch=true` deletes the source branch (closing alone is just
          // a status change); idempotent: closing an already-closed PR/MR is a no-op.
          write
              "forge_pr_close"
              "Close a pull/merge request without merging. delete_branch is GitHub-only; on GitLab/Gitea it is refused as Unsupported."
              true
              true
              [ pNumber
                { Name = "delete_branch"
                  JsonType = "boolean"
                  Description = "Also delete the source branch (GitHub only; refused as Unsupported on GitLab/Gitea)."
                  Required = false } ]
          write
              "forge_pr_mark_ready"
              "Mark a draft pull/merge request as ready for review (Unsupported on Gitea)."
              false
              true
              [ pNumber ]
          write
              "forge_pr_comment"
              "Post a comment to an existing pull/merge request, returning the CLI's output."
              false
              false
              [ pNumber
                { Name = "body"
                  JsonType = "string"
                  Description = "The markdown comment body."
                  Required = true } ]
          // Non-destructive: only overwrites title/body text (no deletion capability), and it's
          // trivially reversible via another edit call; idempotent: re-applying the same
          // title/body leaves the PR/MR unchanged.
          write
              "forge_pr_edit"
              "Edit a pull/merge request's title and/or body (at least one required)."
              false
              true
              [ pNumber
                { Name = "title"
                  JsonType = "string"
                  Description = "The new title; omit to leave it alone."
                  Required = false }
                { Name = "body"
                  JsonType = "string"
                  Description = "The new body; omit to leave it alone."
                  Required = false } ]
          write
              "forge_pr_checkout"
              "Check out a pull/merge request's branch into the local working copy (gh pr checkout / glab mr checkout / tea pr checkout). A local-worktree mutation — it switches the checked-out branch."
              false
              true
              [ pNumber ]
          // Non-destructive: submits a review (approve/request-changes/comment), discarding no
          // data; NOT idempotent: request-changes/comment add a new review record each call (the
          // worst case sets the flag). `kind` support varies by forge: request_changes is
          // Unsupported on GitLab; comment is Unsupported on GitLab and Gitea (refused before any
          // spawn). `body` is required for request_changes/comment, optional for approve.
          write
              "forge_pr_review"
              "Submit a review on a pull/merge request: approve, request_changes, or comment. request_changes is Unsupported on GitLab; a comment review is Unsupported on GitLab and Gitea (use forge_pr_comment for a plain comment there)."
              false
              false
              [ pNumber
                { Name = "kind"
                  JsonType = "string"
                  Description = "Review kind: approve, request_changes, or comment."
                  Required = true }
                { Name = "body"
                  JsonType = "string"
                  Description =
                    "The review body (markdown). Required for request_changes and comment; optional for approve."
                  Required = false } ]
          // A creating call, so never idempotent (each call makes a new release); non-destructive.
          // draft/prerelease are GitHub/Gitea-only — on GitLab either is refused as Unsupported
          // before any spawn.
          write
              "forge_release_create"
              "Create a release on the configured forge for a Git tag, returning the CLI's output (the release URL on GitHub/GitLab). draft/prerelease are GitHub/Gitea-only; on GitLab either is refused as Unsupported."
              false
              false
              [ { Name = "tag"
                  JsonType = "string"
                  Description = "The Git tag to attach the release to."
                  Required = true }
                { Name = "title"
                  JsonType = "string"
                  Description = "Release title; omit to let the forge default it (commonly to the tag)."
                  Required = false }
                { Name = "notes"
                  JsonType = "string"
                  Description = "Release notes (markdown); omit for none."
                  Required = false }
                { Name = "draft"
                  JsonType = "boolean"
                  Description = "Create as an unpublished draft (GitHub/Gitea only; refused as Unsupported on GitLab)."
                  Required = false }
                { Name = "prerelease"
                  JsonType = "boolean"
                  Description = "Mark as a pre-release (GitHub/Gitea only; refused as Unsupported on GitLab)."
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
        | "repo_remotes" -> server.RepoRemotes()
        | "repo_show_file" ->
            bind (reqStr args "rev") (fun rev -> bind (reqStr args "path") (fun path -> server.RepoShowFile(rev, path)))
        | "repo_log" ->
            bind (reqStr args "revspec_or_revset") (fun rev ->
                bind (reqU64 args "max") (fun max -> server.RepoLog(rev, max)))
        | "repo_annotate" ->
            bind (reqStr args "path") (fun path -> bind (optStr args "rev") (fun rev -> server.RepoAnnotate(path, rev)))
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
            bind (reqStr args "path") (fun path ->
                bind (optBool args "force") (fun force -> server.RepoRemoveWorktree(path, force)))
        | "repo_rebase" -> bind (reqStr args "onto") server.RepoRebase
        | "repo_abort_in_progress" -> server.RepoAbortInProgress()
        | "repo_continue_in_progress" -> server.RepoContinueInProgress()
        | "repo_delete_branch" ->
            bind (reqStr args "name") (fun name ->
                bind (optBool args "force") (fun force -> server.RepoDeleteBranch(name, force)))
        | "repo_rename_branch" ->
            bind (reqStr args "old_name") (fun oldName ->
                bind (reqStr args "new_name") (fun newName -> server.RepoRenameBranch(oldName, newName)))
        | "repo_new_child" -> bind (reqStr args "reference") server.RepoNewChild
        | "forge_auth_status" -> server.ForgeAuthStatus()
        | "forge_repo_view" -> server.ForgeRepoView()
        | "forge_info" -> server.ForgeInfo()
        | "forge_pr_list" ->
            bind (optStr args "state") (fun state ->
                bind (optInt args "limit") (fun limit -> server.ForgePrList(state, limit)))
        | "forge_pr_view" -> bind (reqU64 args "number") server.ForgePrView
        | "forge_pr_for_branch" -> bind (reqStr args "source_branch") server.ForgePrForBranch
        | "forge_pr_checks" -> bind (reqU64 args "number") server.ForgePrChecks
        | "forge_issue_list" ->
            bind (optStr args "state") (fun state ->
                bind (optInt args "limit") (fun limit -> server.ForgeIssueList(state, limit)))
        | "forge_issue_view" -> bind (reqU64 args "number") server.ForgeIssueView
        | "forge_release_list" -> server.ForgeReleaseList()
        | "forge_release_view" -> bind (reqStr args "tag") server.ForgeReleaseView
        | "forge_issue_create" ->
            bind (reqStr args "title") (fun title ->
                bind (reqStr args "body") (fun body -> server.ForgeIssueCreate(title, body)))
        | "forge_issue_close" -> bind (reqU64 args "number") server.ForgeIssueClose
        | "forge_issue_comment" ->
            bind (reqU64 args "number") (fun number ->
                bind (reqStr args "body") (fun body -> server.ForgeIssueComment(number, body)))
        | "forge_pr_create" ->
            bind (reqStr args "title") (fun title ->
                bind (reqStr args "body") (fun body ->
                    bind (optStr args "source") (fun source ->
                        bind (optStr args "target") (fun target -> server.ForgePrCreate(title, body, source, target)))))
        | "forge_pr_merge" ->
            bind (reqU64 args "number") (fun number ->
                bind (reqStr args "strategy") (fun strategy ->
                    bind (optBool args "auto") (fun auto ->
                        bind (optBool args "delete_branch") (fun deleteBranch ->
                            server.ForgePrMerge(number, strategy, auto, deleteBranch)))))
        | "forge_pr_close" ->
            bind (reqU64 args "number") (fun number ->
                bind (optBool args "delete_branch") (fun deleteBranch -> server.ForgePrClose(number, deleteBranch)))
        | "forge_pr_mark_ready" -> bind (reqU64 args "number") server.ForgePrMarkReady
        | "forge_pr_comment" ->
            bind (reqU64 args "number") (fun number ->
                bind (reqStr args "body") (fun body -> server.ForgePrComment(number, body)))
        | "forge_pr_edit" ->
            bind (reqU64 args "number") (fun number ->
                bind (optStr args "title") (fun title ->
                    bind (optStr args "body") (fun body -> server.ForgePrEdit(number, title, body))))
        | "forge_pr_checkout" -> bind (reqU64 args "number") server.ForgePrCheckout
        | "forge_pr_review" ->
            bind (reqU64 args "number") (fun number ->
                bind (reqStr args "kind") (fun kind ->
                    bind (optStr args "body") (fun body -> server.ForgePrReview(number, kind, body))))
        | "forge_release_create" ->
            bind (reqStr args "tag") (fun tag ->
                bind (optStr args "title") (fun title ->
                    bind (optStr args "notes") (fun notes ->
                        bind (optBool args "draft") (fun draft ->
                            bind (optBool args "prerelease") (fun prerelease ->
                                server.ForgeReleaseCreate(tag, title, notes, draft, prerelease))))))
        | unknown -> task { return Error(McpError.InvalidParams(sprintf "unknown tool %A" unknown)) }
