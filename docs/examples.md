# Examples

These examples use the public APIs implemented by the corresponding projects (and, after the
first release, shipped in their NuGet packages). The toolkit runs the installed VCS and forge
CLIs, so use it only with repositories and credentials you are allowed to modify.

## Repository state, merge probes, and worktrees

`Repo.Open` detects Git or Jujutsu. A merge probe rolls itself back before returning, and the
worktree call creates a new branch from the requested base revision.

```fsharp
open VcsToolkit.Core

let inspectAndCreateWorktree repoDir =
    task {
        match Repo.Open repoDir with
        | Error error -> return Error error
        | Ok repo ->
            match! repo.Snapshot() with
            | Error error -> return Error error
            | Ok snapshot ->
                printfn "Head: %A; branch: %A" snapshot.Head snapshot.Branch

                let! merge = repo.TryMerge "origin/main"
                printfn "Merge probe: %A" merge

                let! existing = repo.ListWorktrees()
                printfn "Attached worktrees: %A" existing

                return! repo.CreateWorktree("../docs-worktree", "docs-example", "main")
    }
```

## Pull request lifecycle

Construct the facade for the forge that hosts the repository. This GitHub example uses the
ambient `gh` login; use `Forge.GitLab` or `Forge.Gitea` for those CLIs instead. `PrCreate`
returns the CLI's success output — a URL on GitHub/GitLab — not the PR number, so query by the
exact source branch to get the `ForgePr.Number`. Do not take the first item from an unfiltered
PR list: it may belong to somebody else's branch.

```fsharp
open VcsToolkit.Forge

let createAndMergePullRequest repoDir sourceBranch =
    task {
        let forge = Forge.GitHub repoDir
        let spec =
            PrCreate.Create("Document examples", "Adds a public API cookbook.").WithSource(sourceBranch)

        let! created = forge.PrCreate spec

        match created with
        | Error error -> return Error error
        | Ok url ->
            printfn "Opened pull request: %s" url

            match! forge.PrForBranch sourceBranch with
            | Error error -> return Error error
            | Ok prs ->
                match prs |> List.tryFind (fun pr -> pr.State = ForgePrState.Open) with
                | None -> return Error(ForgeError.InvalidInput "No open pull request found for the source branch")
                | Some pr ->
                    let! detail = forge.PrView pr.Number
                    printfn "Pull request: %A" detail
                    let! merged = forge.PrMerge(pr.Number, PrMerge.Squash)
                    return merged |> Result.map ignore
    }
```

## Watching repository changes

`VcsToolkit.Watch` ships two monitors over the same event model. Pick by **how the repository
is reached**, not by preference:

| | `RepoWatcher` | `RepoPoller` |
|---|---|---|
| Driven by | `FileSystemWatcher` on the `.git`/`.jj` state dir (optionally the working tree) | a plain interval timer |
| Latency | sub-second (`Debounce`, default 250 ms) | bounded below by `Interval` (default 2 s) |
| Cost while idle | none — no query until the filesystem moves | one `git`/`jj` re-query per tick, change or not |
| Where it works | local disks with a working OS watch | anywhere a subprocess runs |
| Known-bad ground | network shares (SMB/NFS), docker/podman volume mounts, across the WSL/host boundary — the OS watch is unreliable or silently absent; a removed-and-recreated state dir invalidates the watch and is only *reported* (`WatcherStats.WatchErrors`), never recovered from | — no OS-watch failure mode at all |

Prefer `RepoWatcher` for a prompt, status bar, or TUI on a local checkout; reach for
`RepoPoller` when the repository lives on a network share or inside a container/VM mount, or
when you would otherwise have to hand-roll a timer around `RepoDiff.diff`. Both emit the same
`RepoEvent`s for the same series of repository mutations, and both are consumed identically —
`Recv()` / `ReadAll()` / `Dispose`.

Build a watcher before entering the receive loop. `Recv` returns `None` after normal disposal;
a terminal re-query failure is surfaced as a `ChannelClosedException` whose inner exception is
`WatcherTerminated`.

```fsharp
open System.Threading.Channels
open VcsToolkit.Core
open VcsToolkit.Watch

let watch repoDir =
    task {
        match Repo.Open repoDir with
        | Error error -> eprintfn "Cannot open repository: %A" error
        | Ok repo ->
            match! RepoWatcher.Builder(repo).WorkingTree(true).Build() with
            | Error error -> eprintfn "Cannot start watcher: %A" error
            | Ok watcher ->
                use watcher = watcher
                try
                    let mutable running = true
                    while running do
                        match! watcher.Recv() with
                        | Some change -> printfn "Events: %A" change.Events
                        | None -> running <- false
                with
                | :? ChannelClosedException as closed ->
                    match closed.InnerException with
                    | :? WatcherTerminated as terminated ->
                        let (WatcherTerminated error) = terminated
                        eprintfn "Watcher stopped: %A" error
                    | _ -> return raise closed
    }
```

Swapping in the poller is a one-line change at the build site — the receive loop is unchanged,
including the terminal-failure handling (`RepoPoller` closes its channel with the same
`WatcherTerminated`), and `ReadAll` is available on both. Only the knobs differ: an `Interval`
instead of `Debounce`/`MaxWait`, and no working-tree switch, since a poller re-queries the whole
state either way.

```fsharp
open VcsToolkit.Watch

let poll repoDir =
    task {
        match Repo.Open repoDir with
        | Error error -> eprintfn "Cannot open repository: %A" error
        | Ok repo ->
            // On a network share or a container volume mount, where an OS filesystem watch is
            // unreliable: re-query every second instead of waiting for a notification.
            match! RepoPoller.Builder(repo).Interval(TimeSpan.FromSeconds 1.0).Build() with
            | Error error -> eprintfn "Cannot start poller: %A" error
            | Ok poller ->
                use poller = poller
                let mutable running = true

                while running do
                    match! poller.Recv() with
                    | Some change -> printfn "Events: %A" change.Events
                    | None -> running <- false
    }
```

`ReadAll(?cancellationToken)` is the `IAsyncEnumerable` form of the same stream on both
monitors — natural from C# (`await foreach`, honouring both the argument token and
`WithCancellation`). From F#, note that FSharp.Core's `task { }` cannot `for … in` an
`IAsyncEnumerable` (that needs `TaskSeq`), so a `Recv` loop like the one above is usually the
simpler F# consumer.

## Resolving Git conflict markers

Parse a conflicted text file, choose a side for every region, and render the original segments
when you need a byte-exact round trip instead.

```fsharp
open VcsToolkit.Git

let resolveOurs content =
    match Conflict.parseConflicts content with
    | Error error -> Error error
    | Ok segments ->
        let original = Conflict.render segments
        printfn "Original conflict text: %s" original
        Conflict.resolve segments ResolutionSide.Ours
```

## Supplying credentials through `ICredentialProvider`

`EnvToken` reads the secret only when an operation runs. The GitHub client injects it as
`GH_TOKEN`, never as a command-line argument; an unset variable falls back to ambient `gh` auth.

```fsharp
open VcsToolkit.CliSupport
open VcsToolkit.GitHub

let listPullRequests repoDir =
    task {
        let provider: ICredentialProvider = EnvToken("GITHUB_TOKEN") :> ICredentialProvider
        let github = GitHub.Create().WithCredentials provider
        return! github.PrList repoDir
    }
```
