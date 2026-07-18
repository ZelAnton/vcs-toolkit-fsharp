namespace VcsToolkit.Mcp

// A Model Context Protocol server exposing the toolkit's typed git/jj + forge operations as
// agent-callable tools. An agent harness (Claude Code, an IDE assistant, any MCP client)
// drives a repository — and its forge — through structured, schema-validated calls instead
// of raw shell. Read tools are always available; mutating tools are write-gated.

/// The canonical names of every **mutating** (write-gated) tool, in registration order. The
/// single source of truth for which names `--allow-tools` accepts: a front-end validates
/// its allowlist against this set and rejects a typo up front (a misspelled name would
/// otherwise be silently inert — it never matches a real tool, so the intended write would
/// stay disabled).
[<RequireQualifiedAccess>]
module WriteTools =

    /// Every write-gated tool name.
    let all =
        [ "repo_try_merge"
          "repo_commit"
          "repo_checkout"
          "repo_fetch"
          "repo_push"
          "repo_create_worktree"
          "repo_remove_worktree"
          "repo_rebase"
          "repo_abort_in_progress"
          "repo_continue_in_progress"
          "repo_delete_branch"
          "repo_rename_branch"
          "repo_new_child"
          "forge_issue_create"
          "forge_pr_create"
          "forge_pr_merge"
          "forge_pr_close"
          "forge_pr_mark_ready"
          "forge_pr_comment"
          "forge_pr_edit"
          "forge_pr_checkout" ]

    /// The same set as a `Set` for membership tests.
    let asSet = Set.ofList all

/// Which mutating tools are callable — the server's write policy. Read tools are always
/// available; every mutating tool checks this gate by its own tool name before doing
/// anything.
[<RequireQualifiedAccess>]
type WriteGate =
    /// No mutating tool is callable (the default).
    | None
    /// Every mutating tool is callable (`--allow-write`).
    | All
    /// Only the named mutating tools are callable (`--allow-tools a,b,c`). Tool names are the
    /// `WriteTools.all` set; read tools are unaffected (always available). At the gate an
    /// unknown name simply never matches; the binary additionally rejects an unknown
    /// `--allow-tools` name up front rather than building an inert entry.
    | Set of Set<string>

    /// Whether the mutating tool `name` may run under this gate.
    member this.Allows(name: string) =
        match this with
        | WriteGate.All -> true
        | WriteGate.None -> false
        | WriteGate.Set tools -> tools.Contains name
