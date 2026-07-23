namespace VcsToolkit.Git

open System
open ProcessKit
open VcsToolkit.CliSupport

/// Toolkit-wide constants for the git wrapper.
[<AutoOpen>]
module internal Constants =

    /// Name of the underlying CLI binary this crate drives.
    [<Literal>]
    let BINARY = "git"

    /// SHA-1-**specific** empty-tree object id. `4b825dc…` is the empty-tree id only
    /// under the SHA-1 object format; a repository with `extensions.objectFormat=sha256`
    /// has a different (64-hex) empty-tree id. Do NOT treat this as a general "empty tree
    /// of the current repository" stand-in — resolve that per-repository instead via
    /// `Git.EmptyTreeOid`, which asks the repository's own `git hash-object` for it. Kept
    /// where a SHA-1-specific value is genuinely wanted (e.g. as a test fixture).
    [<Literal>]
    let EMPTY_TREE = "4b825dc642cb6eb9a060e54bf8d69288fbee4904"

    /// The oldest git major this wrapper is written against.
    [<Literal>]
    let MIN_SUPPORTED_MAJOR = 2UL

/// What a `diff` / `diffText` call compares.
[<RequireQualifiedAccess>]
type DiffSpec =
    /// All tracked working-tree changes vs the last commit (`git diff HEAD`).
    | WorkingTree
    /// A specific revision or range, e.g. `main..HEAD` or `HEAD~1` (`git diff <rev>`).
    | Rev of string

/// Options for `worktreeAdd` (`git worktree add`).
type WorktreeAdd =
    {
        /// Filesystem path for the new worktree.
        Path: string
        /// Create and check out this new branch (`-b <name>`); `None` checks out an existing ref.
        NewBranch: string option
        /// The commit/branch to base the worktree on; `None` defaults to `HEAD`.
        Commitish: string option
        /// Register the worktree without populating its files (`--no-checkout`).
        NoCheckout: bool
    }

    /// A worktree at `path` checking out an existing `commitish`.
    static member Checkout(path: string, commitish: string) =
        { Path = path
          NewBranch = None
          Commitish = Some commitish
          NoCheckout = false }

    /// A worktree at `path` creating a new branch `name` based on `commitish`.
    static member CreateBranch(path: string, name: string, commitish: string) =
        { Path = path
          NewBranch = Some name
          Commitish = Some commitish
          NoCheckout = false }

    /// Register the worktree without checking out its files (`--no-checkout`).
    member this.WithNoCheckout() = { this with NoCheckout = true }

/// Options for `push` (`git push`).
type GitPush =
    {
        /// Remote to push to (defaults to `origin`).
        Remote: string
        /// The refspec — a bare branch name, or `local:remote_branch`.
        Refspec: string
        /// Set the pushed branch as the upstream (`-u`).
        SetUpstream: bool
    }

    /// Push branch `name` to `origin` under the same name.
    static member Branch(name: string) =
        { Remote = "origin"
          Refspec = name
          SetUpstream = false }

    /// Push `local` to a differently-named `remoteBranch`.
    static member ForRefspec(local: string, remoteBranch: string) =
        { Remote = "origin"
          Refspec = sprintf "%s:%s" local remoteBranch
          SetUpstream = false }

    /// Push to a non-default remote.
    member this.WithRemote(remote: string) = { this with Remote = remote }

    /// Record the pushed branch as the local branch's upstream (`-u`).
    member this.WithUpstream() = { this with SetUpstream = true }

/// Options for `cloneRepo` (`git clone`).
type CloneSpec =
    {
        /// Check out this branch instead of the remote's default (`--branch`).
        Branch: string option
        /// Shallow-clone to this many commits (`--depth`).
        Depth: int option
        /// Create a bare repository (`--bare`).
        Bare: bool
    }

    /// A plain full clone of the remote's default branch.
    static member Create() =
        { Branch = None
          Depth = None
          Bare = false }

    /// Check out `branch` instead of the remote's default (`--branch`).
    member this.WithBranch(branch: string) = { this with Branch = Some branch }

    /// Shallow-clone to `depth` commits (`--depth`).
    member this.WithDepth(depth: int) = { this with Depth = Some depth }

    /// Clone as a bare repository (`--bare`).
    member this.WithBare() = { this with Bare = true }

/// Options for `commitPaths` (`git commit --only`).
type CommitPaths =
    {
        /// The exact paths whose working-tree content to commit (`--only -- <paths>`).
        Paths: string list
        /// The commit message (`-m`).
        Message: string
        /// Amend the previous commit instead of creating a new one (`--amend`).
        Amend: bool
    }

    /// Commit exactly `paths`' working-tree content with `message`.
    static member Create(paths: string seq, message: string) =
        { Paths = List.ofSeq paths
          Message = message
          Amend = false }

    /// Amend the previous commit instead of creating a new one (`--amend`).
    member this.WithAmend() = { this with Amend = true }

/// Options for `mergeCommit` (`git merge` that commits the result).
type MergeCommit =
    {
        /// The branch to merge in.
        Branch: string
        /// Always create a merge commit, even when a fast-forward was possible (`--no-ff`).
        NoFf: bool
        /// The merge commit message (`-m`); `None` takes the default message (`--no-edit`).
        Message: string option
    }

    /// Merge `name` taking the default merge message non-interactively.
    static member ForBranch(name: string) =
        { Branch = name
          NoFf = false
          Message = None }

    /// Always create a merge commit, even when a fast-forward was possible (`--no-ff`).
    member this.WithNoFf() = { this with NoFf = true }

    /// Use `m` as the merge commit message (`-m`).
    member this.WithMessage(m: string) = { this with Message = Some m }

/// Options for `mergeNoCommit` (`git merge --no-commit`).
type MergeNoCommit =
    {
        /// The branch to merge in.
        Branch: string
        /// Stage the squashed result without recording `MERGE_HEAD` (`--squash`).
        Squash: bool
        /// Always record a real (abortable) merge, even when a fast-forward was possible (`--no-ff`).
        NoFf: bool
    }

    /// Merge `name` but stop before committing.
    static member ForBranch(name: string) =
        { Branch = name
          Squash = false
          NoFf = false }

    /// Stage the squashed result without recording `MERGE_HEAD` (`--squash`).
    member this.WithSquash() = { this with Squash = true }

    /// Always record a real (abortable) merge (`--no-ff`).
    member this.WithNoFf() = { this with NoFf = true }

/// Options for `tagCreateAnnotated` (`git tag -a`).
type AnnotatedTag =
    {
        /// The tag name.
        Name: string
        /// The tag message (`-m`).
        Message: string
        /// The revision to tag (`<rev>`); `None` tags `HEAD`.
        Rev: string option
    }

    /// An annotated tag `name` with `message` at `HEAD`.
    static member Create(name: string, message: string) =
        { Name = name
          Message = message
          Rev = None }

    /// Tag `r` instead of `HEAD`.
    member this.WithRev(r: string) = { this with Rev = Some r }

/// Options for `clean` (`git clean`). Deliberately defensive, mirroring the already-explicit
/// `force` parameter on `WorktreeRemove`: bare `Create()` sets neither `DryRun` nor `Force`, and
/// `Git.Clean` refuses to spawn `git` at all until the caller opts into one via `WithDryRun`/
/// `WithForce` — independent of the repository's own `clean.requireForce` config, which this
/// wrapper never relies on to make the operation safe.
type Clean =
    {
        /// Also remove untracked directories, not just files (`-d`).
        Directories: bool
        /// Also remove ignored files (`-x`).
        IncludeIgnored: bool
        /// Remove *only* ignored files, leaving other untracked files alone (`-X`, mutually
        /// exclusive with `IncludeIgnored` in git itself — setting both is the caller's error).
        OnlyIgnored: bool
        /// Show what would be removed, without removing anything (`-n` / `--dry-run`).
        DryRun: bool
        /// Actually remove the files — the caller's explicit, first-class acknowledgement that
        /// this operation deletes untracked files irrecoverably (`-f` / `--force`).
        Force: bool
    }

    /// Bare defaults: no directories/ignored files targeted, and neither `DryRun` nor `Force`
    /// set. `Git.Clean` refuses a spec still in this shape.
    static member Create() =
        { Directories = false
          IncludeIgnored = false
          OnlyIgnored = false
          DryRun = false
          Force = false }

    /// Show what would be removed without removing anything (`-n`).
    member this.WithDryRun() = { this with DryRun = true }

    /// Actually remove the files (`-f`).
    member this.WithForce() = { this with Force = true }

    /// Also remove untracked directories (`-d`).
    member this.WithDirectories() = { this with Directories = true }

    /// Also remove ignored files (`-x`).
    member this.WithIncludeIgnored() = { this with IncludeIgnored = true }

    /// Remove *only* ignored files (`-X`).
    member this.WithOnlyIgnored() = { this with OnlyIgnored = true }

/// A pre-validated git reference name (branch/tag/remote), for callers that accept
/// names from untrusted input and want to fail early. Validation follows the
/// load-bearing core of `git check-ref-format`, evaluated per slash-separated path
/// component. A name is rejected when it:
///
///  * is empty, or is the single character `@`;
///  * begins with `-` (an argv-injection guard, beyond `check-ref-format` itself);
///  * has an empty path component — i.e. begins or ends with `/`, or contains `//`;
///  * has a component beginning with `.` (e.g. `feature/.hidden`) or ending with
///    `.lock` (e.g. `foo.lock/bar`);
///  * contains `..`, ends with `.`, or contains the reflog/upstream sequence `@{`
///    (which git resolves below the ref layer and could redirect the operation to a
///    different ref than the one validated here);
///  * contains an ASCII control character (including DEL) or any of the pattern/path
///    metacharacters space `~` `^` `:` `?` `*` `[` `\`.
///
/// Deliberately *not* enforced: the requirement that a ref contain at least one `/`.
/// One-level names (`main`, `v1.0`) are accepted, matching git's `--allow-onelevel`,
/// because branch/tag/remote names are routinely single-level. Filesystem-specific
/// checks git applies only under `core.protectHFS`/`core.protectNTFS` are likewise
/// out of scope — they are not part of the `check-ref-format` core.
[<Sealed>]
type RefName private (value: string) =
    /// The validated name.
    member _.Value = value
    override _.ToString() = value

    /// Validate `name` as a reference name; see the type doc-comment for the exact
    /// rule set (the load-bearing core of `git check-ref-format`, per-component).
    static member Create(name: string) : Result<RefName, ProcessError> =
        // Chars git check-ref-format forbids anywhere (the ASCII control set is tested
        // separately): space, the pattern/reflog metacharacters ~ ^ : ? * [ and `\`.
        let forbidden = set [ ' '; '~'; '^'; ':'; '?'; '*'; '['; char 92 ]

        // A slash-separated path component must be non-empty (so the name cannot begin
        // or end with `/`, nor contain `//`), must not begin with a dot (a hidden-ref
        // component such as `feature/.hidden`), and must not end with `.lock` (git's
        // lock-file suffix, e.g. `foo.lock/bar`).
        let badComponent (c: string) =
            c.Length = 0
            || c.StartsWith(".", StringComparison.Ordinal)
            || c.EndsWith(".lock", StringComparison.Ordinal)

        let bad =
            name.Length = 0
            || name = "@"
            || name.StartsWith("-", StringComparison.Ordinal)
            || name.EndsWith(".", StringComparison.Ordinal)
            || name.Contains ".."
            || name.Contains "@{"
            || name.Split('/') |> Array.exists badComponent
            || name |> Seq.exists (fun c -> Char.IsControl c || Set.contains c forbidden)

        if bad then
            Error(ProcessError.Spawn(BINARY, sprintf "invalid git reference name: \"%s\"" name))
        else
            Ok(RefName name)

/// A pre-validated revision/range expression (`HEAD~2`, `main..feature`). Minimal:
/// guarantees only non-empty and not flag-shaped, matching the internal guard.
[<Sealed>]
type RevSpec private (value: string) =
    /// The validated expression.
    member _.Value = value
    override _.ToString() = value

    /// Validate `rev` as a revision/range expression (non-empty, no leading `-`).
    static member Create(rev: string) : Result<RevSpec, ProcessError> =
        match rejectFlagLike BINARY "revision" rev with
        | Error e -> Error e
        | Ok() -> Ok(RevSpec rev)

/// One entry from `git stash list -z --format=%gd%x1f%H%x1f%gs`.
type StashEntry =
    {
        /// Position in the stash list (`stash@{Index}`) — 0 is the most recently pushed entry,
        /// the one a bare `stash pop`/`stash apply` would target.
        Index: uint32
        /// Full commit hash of the stash commit (`%H`).
        Hash: string
        /// Branch the stash was created on, parsed from the reflog subject (`%gs`) when it
        /// matches git's own `"WIP on <branch>: ..."` / `"On <branch>: ..."` shape — a ref name
        /// structurally can't contain `:` (`RefName.Create`'s forbidden set), so the first colon
        /// in the subject unambiguously ends the branch component. `None` when the subject
        /// doesn't match either shape (a foreign/hand-written reflog entry).
        Branch: string option
        /// The stash's reflog subject (`%gs`), verbatim for valid UTF-8 output — e.g.
        /// `"WIP on main: 1234567 subject"` or `"On main: custom message"`. If git emits
        /// malformed UTF-8, .NET decoding replaces the invalid byte sequences with U+FFFD
        /// replacement characters. Left un-stripped (rather than trying to peel off the
        /// `"WIP on <branch>: "`/`"On <branch>: "` prefix) so the field can never lose or
        /// misplace part of a message that itself contains colons or embedded newlines.
        Message: string
    }

/// The sync state of a submodule, decoded from the leading marker character of a
/// `git submodule status` line.
[<RequireQualifiedAccess>]
type SubmoduleState =
    /// Not initialized (`-`): the submodule's working tree has not been checked out.
    | Uninitialized
    /// Out of sync (`+`): the submodule's currently checked-out commit differs from the one
    /// recorded in the superproject's index.
    | OutOfSync
    /// Conflicted (`U`): the submodule has unresolved merge conflicts.
    | Conflict
    /// In sync (a leading space): the checked-out commit matches the recorded one.
    | Current

/// One submodule recorded in the superproject's `.gitmodules`, parsed from
/// `git config --file .gitmodules --list -z`.
type Submodule =
    {
        /// The submodule's logical name — the `submodule.<name>.*` config subsection. This is
        /// NOT necessarily the same as `Path`: git lets a submodule's name and working-tree path
        /// differ, and the name may itself contain dots or spaces.
        Name: string
        /// The submodule's path within the superproject working tree (`submodule.<name>.path`);
        /// empty when `.gitmodules` omits it.
        Path: string
        /// The submodule's upstream URL (`submodule.<name>.url`); empty when omitted.
        Url: string
        /// The branch this submodule tracks (`submodule.<name>.branch`); `None` when unset — the
        /// common case, since a submodule is normally pinned to a recorded commit, not a branch.
        Branch: string option
    }

/// One entry from `git submodule status` — a submodule's path, checked-out commit, and typed
/// sync state.
type SubmoduleStatus =
    {
        /// The submodule's path within the superproject working tree.
        Path: string
        /// The object id of the submodule's currently checked-out commit — or, when the
        /// submodule is not initialized, the commit recorded in the superproject's index.
        Commit: string
        /// The sync state, decoded from the line's leading marker character.
        State: SubmoduleState
        /// The `git describe` of the checked-out commit, when git printed one in parentheses;
        /// `None` otherwise (e.g. an uninitialized submodule, whose describe git omits).
        Describe: string option
    }

/// Options for `submoduleUpdate` (`git submodule update`). Bare `Create()` runs a plain update
/// of every recorded submodule; the `With…` builders opt into `--init`, `--recursive`,
/// `--depth`, and a path restriction. Any restricting paths are emitted after an end-of-options
/// `--` terminator (see `Git.SubmoduleUpdate`), so a path can never be reinterpreted as a flag.
type SubmoduleUpdate =
    {
        /// Initialize any not-yet-initialized submodules before updating (`--init`).
        Init: bool
        /// Recurse into nested submodules (`--recursive`).
        Recursive: bool
        /// Shallow-fetch each updated submodule to this many commits (`--depth <n>`); `None`
        /// leaves the depth unrestricted.
        Depth: int option
        /// Restrict the update to these submodule paths; empty updates every recorded submodule.
        Paths: string list
    }

    /// A plain update of every recorded submodule, with no extra options.
    static member Create() =
        { Init = false
          Recursive = false
          Depth = None
          Paths = [] }

    /// Initialize not-yet-initialized submodules first (`--init`).
    member this.WithInit() = { this with Init = true }

    /// Recurse into nested submodules (`--recursive`).
    member this.WithRecursive() = { this with Recursive = true }

    /// Shallow-fetch each updated submodule to `depth` commits (`--depth`).
    member this.WithDepth(depth: int) = { this with Depth = Some depth }

    /// Restrict the update to `paths` (emitted after the end-of-options `--`).
    member this.WithPaths(paths: string seq) = { this with Paths = List.ofSeq paths }

/// What the installed `git` binary supports, probed via `capabilities`.
type GitCapabilities =
    {
        /// The binary's parsed version.
        Version: VcsToolkit.Diff.Version
    }

    /// Whether the binary meets the supported floor (major >= 2).
    member this.IsSupported = this.Version.Major >= MIN_SUPPORTED_MAJOR

    /// Error unless `IsSupported`.
    member this.EnsureSupported() : Result<unit, ProcessError> =
        if this.IsSupported then
            Ok()
        else
            Error(
                ProcessError.Spawn(
                    BINARY,
                    sprintf
                        "VcsToolkit.Git requires git >= %d (validated on 2.54), found %O"
                        MIN_SUPPORTED_MAJOR
                        this.Version
                )
            )
