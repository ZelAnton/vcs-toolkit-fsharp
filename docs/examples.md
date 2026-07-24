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
