namespace VcsToolkit.Forge

open System.Threading.Tasks
open ProcessKit

/// A handle's one-shot, per-handle cache of the `tea` version probe
/// (`Gitea.Capabilities()`) — see `GitHubVersionProbe` for the caching rationale.
type internal GiteaVersionProbe = Lazy<Task<Result<VcsToolkit.Gitea.GiteaCapabilities, ProcessError>>>

/// Gitea-backed implementations of the facade operations: thin calls to the
/// `VcsToolkit.Gitea` client plus pure mappers from its types into the unified DTOs.
/// `tea` has no current-repo view, draft toggle, PR-checks command, or single-release
/// view, so `repoView`/`prMarkReady`/`prChecks`/`releaseView` have no function here — the
/// `Forge` dispatch returns `Unsupported` for the Gitea backend instead.
module internal GiteaForge =

    let private mapPr (pr: VcsToolkit.Gitea.PullRequest) : ForgePr =
        { Number = pr.Number
          Title = pr.Title
          // tea folds the merge flag into its `state` column: a merged PR reads
          // `"merged"`. `pr.Merged` is derived from that, so key off it first.
          State =
            if pr.Merged then
                ForgePrState.Merged
            elif Common.stateEquals pr.State "closed" then
                ForgePrState.Closed
            else
                ForgePrState.Open
          SourceBranch = pr.HeadBranch
          TargetBranch = pr.BaseBranch
          Url = pr.Url
          // tea's lean PR surface has no draft column → unreported, None.
          Draft = None
          // tea's PR list/view has no labels/assignees columns → the honest answer is
          // "unknown" (None), never a false empty `Some []`.
          Labels = None
          Assignees = None }

    let private mapIssue (i: VcsToolkit.Gitea.Issue) : ForgeIssue =
        { Number = i.Number
          Title = i.Title
          State =
            if Common.stateEquals i.State "closed" then
                ForgeIssueState.Closed
            else
                ForgeIssueState.Open
          Body = i.Body
          Url = i.Url
          // tea's issue surface has no labels/assignees columns → None (unknown), not [].
          Labels = None
          Assignees = None }

    let private mapRelease (r: VcsToolkit.Gitea.Release) : ForgeRelease =
        { Tag = r.Tag
          Title = r.Title
          Url = r.Url
          PublishedAt = Common.strOpt r.PublishedAt
          // `tea` has no release body/notes column.
          Body = Option.None
          // tea's release `Status` column carries draft/prerelease → Some.
          Draft = Some r.Draft
          Prerelease = Some r.Prerelease }

    // --- operations ----------------------------------------------------------

    let authStatus (tea: VcsToolkit.Gitea.Gitea) =
        task {
            let! r = tea.AuthStatus()
            return ofForge r
        }

    /// Fail-open version probe for `Capabilities`: the parsed CLI version, or `None` when
    /// the `--version` probe failed or didn't parse (never blocks capability reporting).
    /// `probe` is the handle's cached one-shot version probe (`GiteaVersionProbe`) —
    /// awaiting `probe.Value` replays the already-fetched result instead of spawning
    /// `--version` again. `Capabilities()` reuses the same cache (see `Forge.fs`) rather
    /// than probing independently, since the installed CLI's version cannot change within
    /// the handle's lifetime.
    let detectVersion (probe: GiteaVersionProbe) =
        task {
            match! probe.Value with
            | Ok caps -> return Some caps.Version
            | Error _ -> return None
        }

    /// Version-gate a typed operation: refuse `op` up front with a structural
    /// `UnsupportedVersion` when the detected tea version is confirmed below the wrapper's
    /// floor. A version that can't be probed or parsed falls through (fail-open) — the gate
    /// only ever blocks a *confirmed* too-old CLI, never fails a call that would otherwise run.
    /// `probe` is the handle's cached one-shot version probe — see `detectVersion`.
    let ensureVersion (probe: GiteaVersionProbe) (op: string) =
        task {
            match! probe.Value with
            | Ok caps when not caps.IsSupported ->
                return
                    Error(
                        ForgeError.UnsupportedVersion(
                            ForgeKind.Gitea,
                            op,
                            caps.Version,
                            VcsToolkit.Gitea.GiteaCapabilities.MinimumSupported
                        )
                    )
            | _ -> return Ok()
        }

    let prList (tea: VcsToolkit.Gitea.Gitea) (dir: string) =
        task {
            match! tea.PrList dir with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok prs -> return Ok(prs |> List.map mapPr)
        }

    let prView (tea: VcsToolkit.Gitea.Gitea) (dir: string) (number: uint64) =
        task {
            match! tea.PrView(dir, number) with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok pr -> return Ok(mapPr pr)
        }

    let prCreate (tea: VcsToolkit.Gitea.Gitea) (dir: string) (spec: PrCreate) =
        task {
            // The unified source/target map onto tea's head/base.
            let create =
                VcsToolkit.Gitea.PrCreate.Create(spec.Title, spec.Body)
                |> fun c ->
                    match spec.Source with
                    | Some s -> c.WithHead s
                    | None -> c
                |> fun c ->
                    match spec.Target with
                    | Some t -> c.WithBase t
                    | None -> c

            let! r = tea.PrCreate(dir, create)
            return ofForge r
        }

    let prComment (tea: VcsToolkit.Gitea.Gitea) (dir: string) (number: uint64) (body: string) =
        task {
            let! r = tea.PrComment(dir, number, body)
            return ofForge r
        }

    let prEdit (tea: VcsToolkit.Gitea.Gitea) (dir: string) (number: uint64) (edit: PrEdit) =
        task {
            let tEdit =
                VcsToolkit.Gitea.PrEdit.Create()
                |> fun e ->
                    match edit.Title with
                    | Some t -> e.WithTitle t
                    | None -> e
                |> fun e ->
                    match edit.Body with
                    | Some b -> e.WithBody b
                    | None -> e

            let! r = tea.PrEdit(dir, number, tEdit)
            return ofForge r
        }

    let prMerge (tea: VcsToolkit.Gitea.Gitea) (dir: string) (number: uint64) (strategy: MergeStrategy) =
        task {
            let ms =
                match strategy with
                | MergeStrategy.Merge -> VcsToolkit.Gitea.MergeStrategy.Merge
                | MergeStrategy.Squash -> VcsToolkit.Gitea.MergeStrategy.Squash
                | MergeStrategy.Rebase -> VcsToolkit.Gitea.MergeStrategy.Rebase

            let! r = tea.PrMerge(dir, number, ms)
            return ofForge r
        }


    let prClose (tea: VcsToolkit.Gitea.Gitea) (dir: string) (number: uint64) =
        task {
            let! r = tea.PrClose(dir, number)
            return ofForge r
        }

    let prCheckout (tea: VcsToolkit.Gitea.Gitea) (dir: string) (number: uint64) =
        task {
            let! r = tea.PrCheckout(dir, number)
            return ofForge r
        }

    let prReview (tea: VcsToolkit.Gitea.Gitea) (dir: string) (number: uint64) (action: ReviewAction) =
        task {
            // `Comment` reviews are refused structurally by the facade's shared
            // `ForgeSupport.unsupportedReview` gate before dispatch (`tea` has no comment-review
            // verb — use `PrComment` for a plain comment there).
            match action.Kind with
            | ReviewKind.Approve ->
                // Approve's body is optional; thread it through as `tea pr approve`'s optional comment.
                let! r = tea.PrApprove(dir, number, action.Body)
                return ofForge r
            | ReviewKind.RequestChanges ->
                // RequestChanges carries a required body by ReviewAction's construction invariant.
                let reason = defaultArg action.Body ""
                let! r = tea.PrReject(dir, number, reason)
                return ofForge r
            | ReviewKind.Comment ->
                // Unreachable: refused by `ForgeSupport.unsupportedReview` before dispatch.
                return Error(ForgeError.Unsupported(ForgeKind.Gitea, "prReview comment"))
        }

    let issueList (tea: VcsToolkit.Gitea.Gitea) (dir: string) =
        task {
            match! tea.IssueList dir with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok issues -> return Ok(issues |> List.map mapIssue)
        }

    let issueView (tea: VcsToolkit.Gitea.Gitea) (dir: string) (number: uint64) =
        task {
            match! tea.IssueView(dir, number) with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok issue -> return Ok(mapIssue issue)
        }

    let issueCreate (tea: VcsToolkit.Gitea.Gitea) (dir: string) (title: string) (body: string) =
        task {
            let! r = tea.IssueCreate(dir, title, body)
            return ofForge r
        }

    let issueClose (tea: VcsToolkit.Gitea.Gitea) (dir: string) (number: uint64) =
        task {
            let! r = tea.IssueClose(dir, number)
            return ofForge r
        }

    let issueComment (tea: VcsToolkit.Gitea.Gitea) (dir: string) (number: uint64) (body: string) =
        task {
            let! r = tea.IssueComment(dir, number, body)
            return ofForge r
        }

    let releaseList (tea: VcsToolkit.Gitea.Gitea) (dir: string) =
        task {
            match! tea.ReleaseList dir with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok releases -> return Ok(releases |> List.map mapRelease)
        }
