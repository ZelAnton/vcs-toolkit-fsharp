namespace VcsToolkit.Jj

open ProcessKit
open VcsToolkit.CliSupport

/// Toolkit-wide constants for the jj wrapper.
[<AutoOpen>]
module internal Constants =

    /// Name of the underlying CLI binary this crate drives.
    [<Literal>]
    let BINARY = "jj"

    /// The validated jj floor: every parser and flag in this crate was verified
    /// empirically against this release. jj's CLI moves fast, so unlike the
    /// git wrapper's major-only gate the jj floor is precise (>= 0.38).
    let MinSupported: VcsToolkit.Diff.Version =
        { Major = 0UL
          Minor = 38UL
          Patch = 0UL }

/// What a `diff` / `diffText` call compares.
[<RequireQualifiedAccess>]
type DiffSpec =
    /// The working-copy change's diff (`jj diff -r @`).
    | WorkingTree
    /// A specific revset, e.g. `@-` or `main..@` (`jj diff -r <revset>`).
    | Rev of string

/// How a new workspace inherits sparse patterns (`jj workspace add --sparse-patterns <mode>`).
[<RequireQualifiedAccess>]
type SparseMode =
    /// Copy all sparse patterns from the current workspace (jj's default).
    | Copy
    /// Include every file in the new workspace.
    | Full
    /// Start with no files — the caller sets patterns afterwards (CoW flow).
    | Empty

    /// The `--sparse-patterns` value jj expects.
    member internal this.AsArg =
        match this with
        | SparseMode.Copy -> "copy"
        | SparseMode.Full -> "full"
        | SparseMode.Empty -> "empty"

/// An exact-path jj fileset (`root-file:"<path>"`), so path metacharacters like `(`,
/// `)`, `|`, `*` are treated literally rather than as fileset operators. Build it
/// with `JjFileset.Path`; the path is **workspace-root-relative** and resolved as
/// such regardless of the command's working directory.
[<Sealed>]
type JjFileset private (value: string) =
    /// The rendered `root-file:"…"` expression.
    member _.Value = value
    override _.ToString() = value

    /// Wrap a workspace-root-relative `path` as an exact-path fileset. Uses jj's
    /// **`root-file:`** anchor (not the cwd-relative `file:`), so the path is
    /// interpreted relative to the workspace root even when the command runs from a
    /// subdirectory (`dir` ≠ root) — a plain `file:` there would silently target a
    /// same-named file under `dir`, or nothing. On Windows, backslash separators are
    /// normalised to `/` first — jj filesets are forward-slash, so a Windows caller's
    /// `src\a.rs` would otherwise become a literal-backslash filename that matches
    /// nothing — then `"` is escaped for the `root-file:"…"` string literal. On
    /// non-Windows platforms `\` is a legitimate filename byte, so it is left alone
    /// and instead escaped as `\\` (before `"` is escaped, so the two escapes don't
    /// double up), producing a valid string literal without mangling the path.
    static member Path(path: string) =
        let escaped =
            if System.OperatingSystem.IsWindows() then
                path.Replace(char 92, '/').Replace("\"", "\\\"")
            else
                path.Replace("\\", "\\\\").Replace("\"", "\\\"")

        JjFileset(sprintf "root-file:\"%s\"" escaped)

/// Options for `workspaceAdd` (`jj workspace add`).
type WorkspaceAdd =
    {
        /// Name for the new workspace.
        Name: string
        /// Revision the workspace's working copy starts at (`-r <base>`).
        Base: string
        /// Filesystem path for the new workspace.
        Path: string
        /// How to seed the new workspace's sparse patterns (`--sparse-patterns`);
        /// `None` leaves jj's default (inherit from the current workspace).
        SparsePatterns: SparseMode option
    }

    /// A workspace named `name`, based at `baseRev`, materialised at `path`.
    static member Create(name: string, baseRev: string, path: string) =
        { Name = name
          Base = baseRev
          Path = path
          SparsePatterns = None }

    /// Seed the new workspace's sparse patterns with `mode` (`--sparse-patterns`).
    member this.WithSparse(mode: SparseMode) =
        { this with SparsePatterns = Some mode }

/// Options for `squashPaths` (`jj squash --from <from> --into <into>
/// [--use-destination-message] <filesets>`).
type SquashPaths =
    {
        /// Source revision the filesets are squashed out of (`--from`).
        From: string
        /// Destination revision the filesets are squashed into (`--into`).
        Into: string
        /// The exact filesets to move; empty squashes the whole `from` change.
        Filesets: JjFileset list
        /// Keep the destination's description rather than combining the two
        /// (`--use-destination-message`).
        UseDestinationMessage: bool
    }

    /// Squash from `fromRev` into `intoRev`, with no filesets selected yet.
    static member Create(fromRev: string, intoRev: string) =
        { From = fromRev
          Into = intoRev
          Filesets = []
          UseDestinationMessage = false }

    /// Set the filesets to move (replacing any already added).
    member this.WithFilesets(filesets: JjFileset seq) =
        { this with
            Filesets = List.ofSeq filesets }

    /// Keep the destination's description (`--use-destination-message`) instead of
    /// combining the two.
    member this.WithUseDestinationMessage() =
        { this with
            UseDestinationMessage = true }

/// A pre-validated revset expression, for callers that accept revsets from
/// untrusted input (UIs, bots, agents) and want to fail early. Deliberately
/// minimal — jj's revset grammar is too rich to validate here — it only guarantees
/// the expression is non-empty and cannot be parsed as a flag (no leading `-`),
/// matching the internal guard the positional-revset methods apply anyway.
[<Sealed>]
type RevsetExpr private (value: string) =
    /// The validated expression.
    member _.Value = value
    override _.ToString() = value

    /// Validate `revset` (non-empty, no leading `-`).
    static member Create(revset: string) : Result<RevsetExpr, ProcessError> =
        match rejectFlagLike BINARY "revset" revset with
        | Error e -> Error e
        | Ok() -> Ok(RevsetExpr revset)

/// The outcome of an op-log rollback (`Jj.RollbackTo`) — the shared protocol behind
/// `Jj.Transaction` and `Repo.TryMerge`. Reported to the caller rather than swallowed, so
/// a refused rollback is never mistaken for a completed one.
///
/// Treat this as potentially extensible — add a `| _ ->` arm when pattern-matching so a
/// future outcome doesn't break your code.
[<RequireQualifiedAccess>]
type RollbackOutcome =
    /// The repo was restored to the captured operation (`op restore <captured>`).
    | RolledBack
    /// The rollback was **deliberately refused**: the captured operation is no longer
    /// visible in the recent op log, so the op-head advanced past it — a concurrent
    /// operation — and restoring would have discarded that work. Carries the captured
    /// op-head and the op-head observed at probe time.
    | SkippedDiverged of captured: string * current: string

/// What the installed `jj` binary supports, probed via `capabilities`. A value
/// type — the client holds no state, so probe once and keep the result.
type JjCapabilities =
    {
        /// The binary's parsed version.
        Version: VcsToolkit.Diff.Version
    }

    /// Whether the binary meets the validated floor (jj >= 0.38).
    member this.IsSupported = compare this.Version MinSupported >= 0

    /// Error unless `IsSupported` — a clear "needs jj >= 0.38, found 0.35.0"
    /// instead of a cryptic argv/template failure later.
    member this.EnsureSupported() : Result<unit, ProcessError> =
        if this.IsSupported then
            Ok()
        else
            Error(
                ProcessError.Spawn(
                    BINARY,
                    sprintf "VcsToolkit.Jj requires jj >= %O (the validated floor), found %O" MinSupported this.Version
                )
            )
