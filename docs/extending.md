# Extending VcsToolkit

This is the contributor workflow for adding a new capability: validate the real CLI
contract, add the typed implementation, prove it hermetically, document it, and update
the changelog and the approved public-API baseline — all in the same change set. Read
[docs/architecture.md](architecture.md) first to pick the right layer: a CLI wrapper
(`VcsToolkit.Git`/`Jj`/`GitHub`/`GitLab`/`Gitea`) owns one CLI's argv and parsing; a
facade (`VcsToolkit.Core` for git/jj, `VcsToolkit.Forge` for the three forges) unifies
the wrappers behind one backend-agnostic surface; `VcsToolkit.Mcp` exposes a facade
operation as an agent-callable tool. Most new capabilities touch all three layers, from
the bottom up — that is the order below.

## 1. Adding a typed method to a CLI wrapper

### Validate the CLI before designing the API — mandatory first step

Before writing a signature, run the installed binary (`git`/`jj`/`gh`/`glab`/`tea`)
against a disposable, one-shot repository and record what it actually does:

- The CLI's version (`--version`) — behaviour genuinely changes across versions (see
  `GitHubVersionProbe`/`GitLabVersionProbe`/`GiteaVersionProbe` in
  `src/VcsToolkit.Forge/`, which gate a facade call on a minimum version before ever
  spawning it).
- The complete argv, in order — every flag and every positional, exactly as the CLI
  parses it.
- For **each** input: is it a flag (`--head <value>`) or a bare positional
  (`gh pr view <number>`)? These have different injection-safety rules (see
  "Guard positional argv" below).
- stdout **and** stderr for a success, for an ordinary failure, and for a
  meaningful non-zero/non-one exit code the CLI uses as a predicate (e.g. "no
  changes" is not the same failure as "not a repository").
- Exact JSON field names and their nullability — does an absent value serialize as a
  missing key, an explicit `null`, or an empty string? Different CLIs (and different
  versions of the same CLI) disagree.
- **Omitted options** — what does the CLI do when an optional flag is left out
  entirely, as opposed to passed empty?
- **Empty input** — an empty string, an empty list, a zero count.
- **A positional argument beginning with `-`** — most CLIs' argument parsers treat
  this as a flag, not the intended value; this is exactly the case the argv-injection
  guards below exist to catch.

**Do not infer `glab`'s or `tea`'s behaviour from a similarly named `gh` command.**
The three forge CLIs are three independently developed tools with genuinely different
argument parsers, flag names, JSON shapes, and command coverage — `tea` in particular
is missing whole commands `gh`/`glab` have (see `ForgeCapabilities` and `ForgeOp` in
`src/VcsToolkit.Forge/Dto.fs`), and `urfave/cli` (`tea`'s CLI framework) even stops
recognising `--flag` tokens once it hits the first bare positional, which `gh`'s and
`glab`'s parsers do not do — a defaults-from-`gh` assumption breaks silently on either.
Validate each of the three independently, live.

A small real example: `GitHub.PrView` (`src/VcsToolkit.GitHub/GitHub.fs`) is exactly
`gh pr view <number> --json <fields>` — a numeric positional (safe without a guard,
see below) plus a fixed field list built from the observed JSON shape:

```fsharp
member _.PrView(dir: string, number: uint64) =
    core.TryParse(core.CommandIn(dir, [ "pr"; "view"; string number; "--json"; PR_FIELDS ]), GitHubParse.parsePr)
```

Its parser (`src/VcsToolkit.GitHub/Parse.fs`) reads exactly the JSON field names
observed on the real CLI, through the shared **total** JSON helpers in
`VcsToolkit.CliSupport.Json` (`src/VcsToolkit.CliSupport/Json.fs`) — `Json.strOr`
returns `""` for an absent, `null`, or wrong-kind field rather than throwing:

```fsharp
let private toPr (el: JsonElement) : PullRequest =
    { Number = Json.u64Or el "number"
      Title = Json.strOr el "title"
      State = Json.strOr el "state"
      HeadRefName = Json.strOr el "headRefName"
      BaseRefName = Json.strOr el "baseRefName"
      Url = Json.strOr el "url"
      Labels = nestedNames el "labels" "name"
      Assignees = nestedNames el "assignees" "login" }
```

Model an ordinary failure, a meaningful predicate exit code, and "the command failed
but still emitted JSON" as three distinct cases — they usually need different handling,
and conflating them is a common source of a wrapper method that "mostly works".

### Where the option type and the parser live

An operation with two or more options, or any bare boolean, gets its own options
record in that client's `Types.fs` rather than a long positional parameter list — an
ambiguous call like `prClose(number, true)` is exactly what this avoids. `PrMerge`
(`src/VcsToolkit.GitHub/Types.fs`) is the pattern: named strategy constructors plus
fluent `With*` members, so a call reads as `PrMerge.Squash.WithAuto()` rather than a
tuple of anonymous booleans:

```fsharp
type PrMerge =
    { Strategy: MergeStrategy
      Auto: bool
      DeleteBranch: bool }

    static member Merge = { Strategy = MergeStrategy.Merge; Auto = false; DeleteBranch = false }
    static member Squash = { Strategy = MergeStrategy.Squash; Auto = false; DeleteBranch = false }
    static member Rebase = { Strategy = MergeStrategy.Rebase; Auto = false; DeleteBranch = false }
    member this.WithAuto() = { this with Auto = true }
    member this.WithDeleteBranch() = { this with DeleteBranch = true }
```

The parser lives in that client's `Parse.fs` as a private `JsonElement -> 'T` mapper
composed with the shared `Json.parseObject`/`Json.parseArray` (`VcsToolkit.CliSupport`),
never inline at the call site — every other method on the client reuses the same
tolerant-parsing contract.

### Guard positional argv before spawning

Any caller-supplied value that lands in a bare positional argv slot must be checked by
`rejectFlagLike` (`src/VcsToolkit.CliSupport/Classify.fs`) before the command is built:
it refuses an empty/whitespace-only value, a value starting with `-` (which the driven
CLI would parse as a flag instead of the intended positional), or a value containing a
NUL byte. `checkFlags` (`src/VcsToolkit.CliSupport/Wrappers.fs`) applies the guard to a
whole `(what, value) list` at once, short-circuiting on the first refusal, so one call
covers every positional a method forwards:

```fsharp
let checkFlags (program: string) (checks: (string * string) list) : Result<unit, ProcessError> =
    let bad = checks |> List.tryPick (fun (what, value) ->
        match rejectFlagLike program what value with
        | Error e -> Some e
        | Ok() -> None)
    match bad with
    | Some e -> Error e
    | None -> Ok()
```

Two exemptions worth knowing before over-applying the guard:

- A value consumed **verbatim as a flag's value** (`-m <message>`, a PR body after
  `--body`) is exempt — it can never be reinterpreted as a flag by the CLI's own
  parser, and rejecting it would wrongly forbid legitimate content (e.g. Markdown
  starting with `- `). Only guard genuine bare positionals.
- A purely numeric positional built from a typed number (`string number` from a
  `uint64`) needs no guard at all — it cannot start with `-` or be empty, as
  `GitHub.PrView` above shows.

For a caller-supplied **path list** rather than a single value, add the CLI's own
`--` terminator as defense in depth, so the driven CLI itself stops parsing flags at
that point regardless of what a path looks like — `Git.Checkout` and `Git.Blame`
(`src/VcsToolkit.Git/Git.fs`) both do this:

```fsharp
core.RunUnit(core.CommandIn(dir, [ "checkout"; reference; "--" ]))
// ...
let args = [ "blame"; "--line-porcelain" ] @ Option.toList rev @ [ "--"; path ]
```

An embedded NUL byte inside a path needs its own guard even after `--` (a NUL cannot
be represented in argv at all on either OS) — see `checkNoEmbeddedNul` in
`src/VcsToolkit.Git/Git.fs` for the pattern on a path list, including the point where
it routes through a NUL-safe stdin transport instead of argv once the guard passes.

**Why this belongs in the wrapper, not the facade or MCP.** Only the wrapper knows the
CLI's actual argv layout — which slot is a flag and which is a bare positional, and
therefore which values need `rejectFlagLike` at all. A blanket "reject leading dash"
rule applied one layer up (the facade or an MCP tool) would incorrectly refuse a
legitimate flag-value like a Markdown body starting with `- `. The same reasoning
applies to exit codes and parsing: only the wrapper has observed the real CLI's exit
code contract and JSON shape, so only it can turn a raw `ProcessResult` into a typed
`Result` — a facade or MCP tool receives an already-typed value and must not
re-interpret CLI-specific process mechanics.

### Test argv, parsing, and failure paths without a process

Inject a `ScriptedRunner` (`ProcessKit.Testing`) so the real command-building and
parsing code runs against a scripted reply instead of a live process. The test helpers
in `tests/VcsToolkit.GitHub.Tests/GitHubTests.fs` are the reusable shapes:

```fsharp
let private scripted (tokens: string list) (reply: Reply) =
    GitHub.WithRunner(ScriptedRunner().On(tokens, reply))

// Answers ANY command Ok "" — proves a guard refused BEFORE anything spawned
// (a refusal returns Error; a leak through the guard would return Ok).
let private permissive () =
    GitHub.WithRunner(ScriptedRunner().Fallback(Reply.Ok ""))

// Records the exact argv the runner was called with, for asserting flag
// PRESENCE, ABSENCE, and order — `.On`'s subsequence match alone can't do that.
let private capturing (reply: Reply) : GitHub * ResizeArray<string> = ...
```

```fsharp
[<Test>]
member _.PrViewBuildsNumberedQuery() : Task =
    task {
        let json = """{"number":42,"title":"t","state":"OPEN","headRefName":"h","baseRefName":"main","url":"u"}"""
        let gh = scripted [ "pr"; "view"; "42"; "--json" ] (Reply.Ok json)
        match! gh.PrView(".", 42UL) with
        | Ok pr -> Assert.That(pr.Number, Is.EqualTo 42UL)
        | Error e -> Assert.Fail $"pr view failed: {e}"
    }
```

For every new method add: a successful-parse test; an exact-argv test via `capturing`
that also asserts a flag is **absent** when its option wasn't requested; every
observed exit-code case using a failing `Reply`; and — for any bare positional — a
`permissive()`-backed test proving `rejectFlagLike` refuses a flag-like value, an empty
value, and a NUL byte **before** the scripted runner is ever reached (assert `Error`,
never inspect argv, since the refusal must happen pre-spawn). Add a real-binary
integration test only for a behaviour the hermetic seam genuinely cannot prove (e.g. an
actual exit code from a real repository state).

### Finish the wrapper layer

- Add the method's doc comment describing the exact CLI invocation and any observed
  quirk (nullability, a version floor, an `Unsupported`-worthy gap on one CLI).
- If the method is user-visible, add the `CHANGELOG.md` bullet under `## [Unreleased]`
  in the **same** change set (see "Cross-cutting F# port requirements" below).
- If the wrapper's own package has an approved public-API baseline
  (`tests/VcsToolkit.PublicApi.Tests/ApprovedApi/VcsToolkit.<Name>.approved.txt`),
  update it in the same change set too.

## 2. Adding a facade operation

Add an operation to `VcsToolkit.Core` (`Repo`, unifying `Git`/`Jj`) or
`VcsToolkit.Forge` (`Forge`, unifying `GitHub`/`GitLab`/`Gitea`) only when it has a
genuinely portable meaning across every backend it claims to unify. A facade is the
**least common denominator**, not one backend's CLI renamed — an operation that only
one backend can do at all belongs on that backend's own escape hatch (`Repo.Git`/
`Repo.Jj`), not forced onto the shared surface.

### Dispatch over the backend

`Repo`'s members pattern-match the bound `Backend` and forward to a same-named
function in `GitBackend`/`JjBackend` — `Repo.Fetch` (`src/VcsToolkit.Core/Repo.fs`) is
the minimal shape:

```fsharp
member _.Fetch() =
    match backend with
    | Backend.Git g -> GitBackend.fetch g cwd
    | Backend.Jj j -> JjBackend.fetch j cwd
```

`Forge`'s members follow the same shape one layer wider, over three backends instead
of two, plus the `Unsupported` case for a backend whose CLI has no equivalent command
at all — `Forge.PrMarkReady` (`src/VcsToolkit.Forge/Forge.fs`):

```fsharp
member _.PrMarkReady(number: uint64) =
    match backend with
    | Backend.GitHub(c, _) -> GitHubForge.prMarkReady c cwd number
    | Backend.GitLab(c, _) -> GitLabForge.prMarkReady c cwd number
    | Backend.Gitea _ -> task { return Error(ForgeError.Unsupported(ForgeKind.Gitea, "prMarkReady")) }
    | Backend.Unknown -> task { return Error(ForgeError.Unsupported(ForgeKind.Unknown, "prMarkReady")) }
```

Add the dispatch arm for every backend explicitly — there is deliberately no catch-all
`_ -> Unsupported` arm, so a new backend (or a new option on an existing operation)
forces every call site to state its support decision rather than silently inheriting
one.

### Make divergence explicit — never silently drop a requested feature

Keep only the fields/requirements that mean the same thing on every backend on the
facade's DTO; use an `option` for information one backend cannot supply, and refuse
structurally with `Unsupported` for an operation or option one backend cannot perform
**before any spawn** — never perform a different, silently-degraded operation instead
of what was actually requested.

`Forge.PrMerge` (`src/VcsToolkit.Forge/Forge.fs`) is the real example: every backend
maps the merge strategy, but `Auto`/`DeleteBranch` are GitHub-only (`gh`'s `--auto`/
`--delete-branch`; `glab`/`tea` have no confirmed equivalent). The unsupported check
runs, and can short-circuit, **before** the version-gated dispatch below it:

```fsharp
member _.PrMerge(number: uint64, merge: PrMerge) =
    let unsupported =
        match backend with
        | Backend.GitLab _ -> ForgeSupport.unsupportedMerge ForgeKind.GitLab merge
        | Backend.Gitea _ -> ForgeSupport.unsupportedMerge ForgeKind.Gitea merge
        | Backend.GitHub _
        | Backend.Unknown -> None

    match unsupported with
    | Some e -> task { return Error e }
    | None -> gated backend "prMerge" (fun () -> (* dispatch *))
```

Two different granularities of "unsupported" exist on purpose, and a new operation
should pick the right one:

- **`ForgeOp`** (`src/VcsToolkit.Forge/Dto.fs`) is for an operation *entirely absent*
  on some backend's CLI, queryable up front via `Forge.Supports(op)` — e.g.
  `ForgeOp.PrChecks` (`tea` has no checks command at all).
- **A dedicated `Supports*` member** (`Forge.SupportsMergeOptions`,
  `SupportsCloseDeleteBranch`, `SupportsReview`) is for an operation that exists
  everywhere but refuses a specific *variant* of its input — these are **not** in
  `ForgeOp`, because the operation itself is supported; only some argument values are.

Document the equivalent support matrix (which backend does what, and every
`Unsupported` case) in the owning facade type's doc comments — there is no separate
per-facade guide in this repository the way the wrapper clients have none either;
the facade member's own doc comment is the source of truth `docs/mcp-server.md`'s
tool descriptions must stay consistent with (see part 3).

### Test through the facade's construction seam

Build a `Repo`/`Forge` handle directly over a scripted wrapper client — `Repo.FromGit`/
`Repo.FromJj`, or `Forge`'s equivalent GitHub/GitLab/Gitea constructors — rather than
re-testing the wrapper's own argv/parsing a second time. The facade-level test asserts
that dispatch reaches the right backend and that `Unsupported`/`Supports` behave as
documented; the wrapper-level tests (part 1) already own argv and parsing correctness.

## 3. Exposing an operation in MCP

An MCP tool in `VcsToolkit.Mcp` (`src/VcsToolkit.Mcp/`) is a thin, policy-enforcing
adapter over an existing `Repo`/`Forge` operation — it must never assemble CLI argv or
duplicate wrapper-level validation. Add the server method (`Server.fs`), its
`ToolSpec` entry (`Catalog.fs`), and — if it mutates — its `WriteTools.all` entry
(`WriteGate.fs`), together.

### Name and describe it

Use `repo_*` for `VcsToolkit.Core` operations and `forge_*` for `VcsToolkit.Forge`
operations. The name is public MCP API, and for a mutating tool it is also the literal
string a `--allow-tools` value must match — `WriteTools.all` (`src/VcsToolkit.Mcp/WriteGate.fs`)
is the single source of truth both the write gate and `Catalog`'s `ReadOnly`/
`Destructive` hints key off of. Add a `ToolSpec` via `Catalog`'s `read`/`write` helper
(`src/VcsToolkit.Mcp/Catalog.fs`), which fix `ReadOnly`/`Destructive` and append the
write-access sentence automatically:

```fsharp
write
    "forge_pr_merge"
    "Merge a pull/merge request with a strategy (merge|squash|rebase). auto/delete_branch are GitHub-only; on GitLab/Gitea either is refused as Unsupported."
    true   // destructive: an irrecoverable, real state change on the remote
    false  // idempotent: merging twice is not the same as merging once
    [ pNumber; (* ... *) ]
```

`destructive`/`idempotent` are evaluated per tool on its actual worst case (including
what its optional parameters can do, e.g. `force`/`delete_branch`), not defaulted —
a creating call is never idempotent, and a call is destructive the moment any one of
its parameter combinations can irrecoverably discard data.

### Write-gate every mutation before calling the facade

A mutating tool's server method must check the write gate before doing anything.
`VcsMcpServer` (`src/VcsToolkit.Mcp/Server.fs`) has three helpers for this, and picking
the right one matters:

- **`RequireWrite(tool)`** alone is the raw gate check — returns an `InvalidParams`
  error naming the disabled tool when the write policy (`WriteGate`) doesn't allow it.
- **`WithForgeWrite(tool, action)`** — gate, then resolve the configured forge, then
  run `action`. Use this for a forge mutation that only touches the **remote**
  (`forge_pr_create`, `forge_pr_comment`, `forge_issue_create`, …) — it does not touch
  the local working copy, so it does not need the repo lock.
- **`WithForgeRepoWrite(tool, action)`** — the same gate + forge resolution, **plus**
  holding the per-repo write lock (the same lock `repo_*` mutations use) for the
  action's duration. Use this for a forge mutation that can also touch the **local**
  working copy or race a `repo_*` mutation — `forge_pr_merge` (which can flip the
  local checkout), `forge_pr_close` (`--delete-branch` deletes the local branch), and
  `forge_pr_checkout` all use this, unconditionally, even in the code path where the
  destructive option wasn't requested this time — a call that *can* touch the working
  copy must always serialize on the lock, not only when it happens to.

```fsharp
member this.ForgePrMerge(number: uint64, strategy: string, auto: bool, deleteBranch: bool) =
    this.WithForgeRepoWrite "forge_pr_merge" (fun f -> (* ... f.PrMerge(number, merge) ... *))
```

A plain `repo_*` mutation uses the parallel `WithRepoWrite(tool, action)`, which gates
and holds the same lock without resolving a forge.

### Bound content output

A tool returning potentially large content (`repo_show_file`, `repo_annotate`,
`forge_pr_diff`) must apply the server's configured output budget
(`outputBudget`/`applyOutputBudget` in `Server.fs`) rather than returning an unbounded
or silently truncated response — content within budget passes through byte-for-byte;
content over budget is truncated at a character boundary with a trailing
`[truncated: showing N of M bytes]` marker, never silently.

### Finish the MCP layer

- Register the tool in `Catalog.all` and its dispatch arm in `Catalog.callTool`.
- Test: argument parsing/validation, the write-gate disabled/enabled cases, backend
  `Unsupported` passthrough, and (for a bounded tool) both the truncated and
  under-budget cases.
- Reflect the new tool in [docs/mcp-server.md](mcp-server.md)'s "Tool reference"
  table — including its read/write classification and any `Unsupported` note — so the
  published reference and the catalogue never drift apart. A facade doc-comment
  change and this table are two sync points for the same fact, not one; keep both
  current even though they don't need to be worded identically.

## Cross-cutting F# port requirements

These are the conventions a newcomer coming from another language's port of this
toolkit is most likely to violate first, because they have no equivalent in most other
ecosystems:

- **Compile order is significant.** F# resolves declarations strictly top-to-bottom,
  both within a file and across files in a project. The `<Compile Include="..." />`
  order in a `.fsproj` **is** the dependency order — insert a new file after
  everything it depends on and before everything that depends on it; do not rely on
  alphabetical order.
- **Fantomas formatting is mandatory**, and CI enforces it (the F# compiler does not
  check `.editorconfig` style the way Roslyn does for C#):
  ```sh
  dotnet fantomas --check src tests
  ```
  Run this — and `dotnet fantomas src tests` to fix — before considering a change
  done; do not reformat code you didn't otherwise touch.
- **Update the approved public-API baseline in the same change set.** A new or changed
  public member changes the corresponding
  `tests/VcsToolkit.PublicApi.Tests/ApprovedApi/VcsToolkit.<Name>.approved.txt`
  snapshot; running the tests writes a `.received.txt` next to a mismatched
  `.approved.txt` — review that diff, then replace the `.approved.txt` with it.
  Forgetting this turns an intentional API change into a failing test for the next
  contributor, not a signal at the point the change was made.
- **`CHANGELOG.md` in the same change set.** Every user-visible change — new or
  changed public API, a behavioural fix, a removal — gets its `## [Unreleased]`
  bullet in the same change set, written for a consumer of the library, not the
  implementer. See [CONTRIBUTING.md](../CONTRIBUTING.md#changelog) for the exact
  mechanics and the pure-internal-refactor exemption.

## See also

- [docs/architecture.md](architecture.md) — the package dependency graph, what each
  layer is for, and the design principles (argv guards, total parsing, credential
  provisioning, error classification) that repeat across every wrapper client.
- [CONTRIBUTING.md](../CONTRIBUTING.md) — build/test commands, formatting, compile
  order, dependency management, and the changelog mechanics.
- [docs/mcp-server.md](mcp-server.md) — the `vcs-mcp` CLI reference, write policy, and
  full tool table a new MCP tool must be added to.
