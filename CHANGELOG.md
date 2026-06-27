# Changelog

All notable changes to **VcsToolkit** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `VcsToolkit.CliSupport`: shared plumbing ported from the Rust `vcs-cli-support` crate — the `rejectFlagLike` argv injection guard, error classifiers (`isMergeConflict`, `isNothingToCommit`, `isTransientFetchError`, `isLockContention`), the `RetryPolicy` / `Retry.retryAsync` lock-contention retry, credential provisioning (`Secret`, `Credential`, `ICredentialProvider`, `StaticCredential`, `EnvToken`, `Credentials.gitCredentialHelper`), and the `ManagedClient` wrapper over the ProcessKit runner.
- `VcsToolkit.Diff`: the git-format unified-diff model (`FileDiff`, `Hunk`, `DiffLine`, `ChangeKind`, `DiffStat`) and `parseDiff` parser, plus `Version` and `parseDottedVersion`, ported from the Rust `vcs-diff` crate.
- `VcsToolkit.Git`: the `git` CLI client (`Git`) ported from the Rust `vcs-git` crate — status (porcelain v1/v2), branches, commit/`commitPaths`, checkout, diff/log, merge/rebase/reset, fetch/push/clone, worktrees, tags, blame, config, remotes, cherry-pick/revert, `switchWithStash`, and a `harden` profile — with the builder specs (`CommitPaths`, `GitPush`, `MergeCommit`, `MergeNoCommit`, `WorktreeAdd`, `CloneSpec`, `AnnotatedTag`), validated `RefName`/`RevSpec` newtypes, `GitCapabilities`, and the git output parsers.

[Unreleased]: https://github.com/ZelAnton/vcs-toolkit-fsharp/commits/main
