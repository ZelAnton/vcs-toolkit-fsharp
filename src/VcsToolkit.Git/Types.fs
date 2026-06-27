namespace VcsToolkit.Git

open System
open ProcessKit
open VcsToolkit.CliSupport

/// Toolkit-wide constants for the git wrapper.
[<AutoOpen>]
module Constants =

    /// Name of the underlying CLI binary this crate drives.
    [<Literal>]
    let BINARY = "git"

    /// Git's well-known empty-tree object id — a stable stand-in for `HEAD` when
    /// diffing the working tree of an unborn (no-commits-yet) repository.
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

/// A pre-validated git reference name (branch/tag/remote), for callers that accept
/// names from untrusted input and want to fail early. Rules follow the load-bearing
/// core of `git check-ref-format`.
[<Sealed>]
type RefName private (value: string) =
    /// The validated name.
    member _.Value = value
    override _.ToString() = value

    /// Validate `name` as a reference name.
    static member Create(name: string) : Result<RefName, ProcessError> =
        let forbidden = set [ ' '; '~'; '^'; ':'; '?'; '*'; '['; char 92 ]

        let bad =
            name = ""
            || name.StartsWith("-", StringComparison.Ordinal)
            || name.StartsWith(".", StringComparison.Ordinal)
            || name.EndsWith("/", StringComparison.Ordinal)
            || name.EndsWith(".lock", StringComparison.Ordinal)
            || name.Contains ".."
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
