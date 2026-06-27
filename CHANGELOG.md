# Changelog

All notable changes to **VcsToolkit** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `VcsToolkit.CliSupport`: shared plumbing ported from the Rust `vcs-cli-support` crate — the `rejectFlagLike` argv injection guard, error classifiers (`isMergeConflict`, `isNothingToCommit`, `isTransientFetchError`, `isLockContention`), the `RetryPolicy` / `Retry.retryAsync` lock-contention retry, credential provisioning (`Secret`, `Credential`, `ICredentialProvider`, `StaticCredential`, `EnvToken`, `Credentials.gitCredentialHelper`), and the `ManagedClient` wrapper over the ProcessKit runner.
- `VcsToolkit.Diff`: the git-format unified-diff model (`FileDiff`, `Hunk`, `DiffLine`, `ChangeKind`, `DiffStat`) and `parseDiff` parser, plus `Version` and `parseDottedVersion`, ported from the Rust `vcs-diff` crate.
- `VcsToolkit.Git`: the `git` CLI client (`Git`) ported from the Rust `vcs-git` crate — status (porcelain v1/v2), branches, commit/`commitPaths`, checkout, diff/log, merge/rebase/reset, fetch/push/clone, worktrees, tags, blame, config, remotes, cherry-pick/revert, `switchWithStash`, and a `harden` profile — with the builder specs (`CommitPaths`, `GitPush`, `MergeCommit`, `MergeNoCommit`, `WorktreeAdd`, `CloneSpec`, `AnnotatedTag`), validated `RefName`/`RevSpec` newtypes, `GitCapabilities`, and the git output parsers.
- `VcsToolkit.Jj`: the Jujutsu (`jj`) CLI client (`Jj`) ported from the Rust `vcs-jj` crate — changes/`log`, `describe`/`new`, bookmarks (local and remote-tracking), the operation log (`opLog`/`opRestore`/`opUndo`) with op-log-rollback `transaction`s, workspaces (incl. a bounded `workspaceRoots` fan-out), `squash`/`split`/`absorb`/`duplicate`/`abandon`, diff and template queries, and git sync (`gitFetch`/`gitPush`/`gitClone`/`gitImport`) — with the builder specs (`WorkspaceAdd`, `SquashPaths`), `JjFileset`/`RevsetExpr` newtypes, `JjCapabilities` gating on the validated jj >= 0.38 floor, and the jj output parsers. The cwd-bound view and the native conflict model are not yet ported.

[Unreleased]: https://github.com/ZelAnton/vcs-toolkit-fsharp/commits/main
