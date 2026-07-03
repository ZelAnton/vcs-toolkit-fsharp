namespace VcsToolkit.Diff

/// Aggregate line/file counts from a diff stat (`git diff --shortstat`,
/// `jj diff --stat`).
[<Struct>]
type DiffStat =
    {
        /// Number of files changed.
        FilesChanged: uint64
        /// Lines added (`insertions(+)`).
        Insertions: uint64
        /// Lines removed (`deletions(-)`).
        Deletions: uint64
    }

    /// Build a `DiffStat`.
    static member Create(filesChanged: uint64, insertions: uint64, deletions: uint64) =
        { FilesChanged = filesChanged
          Insertions = insertions
          Deletions = deletions }

/// How a file changed in a unified diff.
///
/// Treat this as potentially extensible (the Rust model is `#[non_exhaustive]`) — add a `| _ ->`
/// arm when pattern-matching so a future change kind (e.g. copied / type-changed) doesn't break
/// your code.
[<RequireQualifiedAccess>]
type ChangeKind =
    /// A new file (`new file mode …`).
    | Added
    /// An existing file's contents changed.
    | Modified
    /// The file was removed (`deleted file mode …`).
    | Deleted
    /// The file was renamed (`rename from …` / `rename to …`).
    | Renamed

/// One line inside a `Hunk`, tagged by its role. The stored text excludes the
/// leading ` `/`+`/`-` marker and the line terminator — reconstruct exact bytes
/// from `FileDiff.Raw`, not from these lines.
[<RequireQualifiedAccess>]
type DiffLine =
    /// Unchanged context line (leading ` `).
    | Context of string
    /// Added line (leading `+`).
    | Added of string
    /// Removed line (leading `-`).
    | Removed of string

/// A single `@@ … @@` hunk within a `FileDiff`.
type Hunk =
    {
        /// Start line in the old file (the `-<start>` of the `@@` header).
        OldStart: uint64
        /// Line count in the old file (defaults to 1 when the `,<count>` is omitted).
        OldLines: uint64
        /// Start line in the new file (the `+<start>` of the `@@` header).
        NewStart: uint64
        /// Line count in the new file (defaults to 1 when the `,<count>` is omitted).
        NewLines: uint64
        /// Text after the closing `@@` (the function/section heading); empty when none.
        Section: string
        /// The hunk body, one entry per `+`/`-`/` ` line.
        Lines: DiffLine list
    }

/// One file's entry in a parsed git-format unified diff (`git diff` or `jj diff --git`).
type FileDiff =
    {
        /// How the file changed.
        Change: ChangeKind
        /// The file's path — the *new* path for a rename — forward-slash normalised.
        Path: string
        /// For a rename, the original path (forward-slash normalised); `None` otherwise.
        OldPath: string option
        /// The `@@` hunks; empty for a binary file or a pure rename with no edits.
        Hunks: Hunk list
        /// The verbatim diff section for this file, for callers that display raw text.
        Raw: string
    }

/// A parsed CLI version (`major.minor.patch`). Structural comparison is numeric in
/// field order, so a caller can gate a feature on a minimum version.
[<CustomEquality; CustomComparison>]
type Version =
    {
        /// Major component (`2` in `2.54.0`).
        Major: uint64
        /// Minor component.
        Minor: uint64
        /// Patch component (`0` when the binary reports only `major.minor`).
        Patch: uint64
    }

    override this.ToString() =
        sprintf "%d.%d.%d" this.Major this.Minor this.Patch

    /// The tuple used for ordering and equality.
    member private this.Key = (this.Major, this.Minor, this.Patch)

    override this.Equals(other: obj) =
        match other with
        | :? Version as v -> this.Key = v.Key
        | _ -> false

    override this.GetHashCode() = hash this.Key

    interface System.IComparable with
        member this.CompareTo(other: obj) =
            match other with
            | :? Version as v -> compare this.Key v.Key
            | _ -> invalidArg "other" "cannot compare a Version with a different type"
