namespace VcsToolkit.Forge

open System.Threading.Tasks
open ProcessKit

/// A handle's one-shot, per-handle cache of the `gh --version` probe (`GitHub.Capabilities()`).
/// `Lazy<Task<...>>`'s default thread-safety mode evaluates the factory at most once — the
/// first `.Value` access spawns `--version` and every later access (including a concurrent
/// one racing an in-flight probe) replays the same `Task`/result instead of spawning again.
/// Constructed once per `Backend` case (see `Forge.fs`), so two separately-built `Forge`
/// handles never share a cache, while a `.At` sibling — which reuses the same `Backend`
/// value — does (same underlying CLI, whose version cannot vary by bound directory).
type internal GitHubVersionProbe = Lazy<Task<Result<VcsToolkit.GitHub.GitHubCapabilities, ProcessError>>>

/// GitHub-backed implementations of the facade operations: thin calls to the
/// `VcsToolkit.GitHub` client plus pure mappers from its types into the unified DTOs.
/// The GitHub client types are fully qualified so the unified same-name Forge DTOs
/// (`PrCreate`/`PrEdit`/`MergeStrategy`/`CiStatus`) stay unqualified in this namespace.
module internal GitHubForge =

    let private stateOf (state: string) : ForgePrState =
        if Common.stateEquals state "merged" then
            ForgePrState.Merged
        elif Common.stateEquals state "closed" then
            ForgePrState.Closed
        else
            ForgePrState.Open

    let private issueStateOf (state: string) : ForgeIssueState =
        if Common.stateEquals state "closed" then
            ForgeIssueState.Closed
        else
            ForgeIssueState.Open

    let private mapPr (pr: VcsToolkit.GitHub.PullRequest) : ForgePr =
        { Number = pr.Number
          Title = pr.Title
          State = stateOf pr.State
          SourceBranch = pr.HeadRefName
          TargetBranch = pr.BaseRefName
          Url = pr.Url
          // gh's lean `--json` fields don't include `isDraft`, so the draft state is
          // unreported here → None (not a confirmed `Some false`).
          Draft = None
          // gh returns labels/assignees when requested (both are in `PR_FIELDS`), so these
          // are confirmed values — an empty list is a confirmed "none", never unknown.
          Labels = Some pr.Labels
          Assignees = Some pr.Assignees
          // author/createdAt/updatedAt are confirmed (all in `PR_FIELDS`) → Some; a deleted
          // author reads as `Some ""` (a confirmed fact), never None. A null milestone
          // flattens to "" in the wrapper → None here (no milestone), a set one → Some title.
          Author = Some pr.Author
          CreatedAt = Some pr.CreatedAt
          UpdatedAt = Some pr.UpdatedAt
          Milestone = Common.strOpt pr.Milestone }

    let private mapIssue (i: VcsToolkit.GitHub.Issue) : ForgeIssue =
        { Number = i.Number
          Title = i.Title
          State = issueStateOf i.State
          Body = i.Body
          Url = i.Url
          // gh returns labels/assignees on issues too (both are in `ISSUE_*_FIELDS`) →
          // confirmed values, never unknown.
          Labels = Some i.Labels
          Assignees = Some i.Assignees
          // author/createdAt/updatedAt confirmed (all in `ISSUE_*_FIELDS`) → Some (deleted
          // author is `Some ""`); a null milestone → None, a set one → Some title.
          Author = Some i.Author
          CreatedAt = Some i.CreatedAt
          UpdatedAt = Some i.UpdatedAt
          Milestone = Common.strOpt i.Milestone }

    let private mapRelease (r: VcsToolkit.GitHub.Release) : ForgeRelease =
        { Tag = r.TagName
          Title = r.Name
          Url = r.Url
          // gh reports an empty `publishedAt`/`body` for a draft/lean list — surface None.
          PublishedAt = Common.strOpt r.PublishedAt
          Body = Common.strOpt r.Body
          // gh's release surface carries both flags on list and view → Some.
          Draft = Some r.IsDraft
          Prerelease = Some r.IsPrerelease
          // `releaseList` doesn't fetch the author (lean list, like Body/Url) → "" → None here;
          // `releaseView` supplies it → Some login (a deleted author also reads "" → None).
          Author = Common.strOpt r.Author }

    let private mapRepo (r: VcsToolkit.GitHub.Repo) : ForgeRepo =
        { Name = r.Name
          Owner = r.Owner
          DefaultBranch = r.DefaultBranch
          Url = r.Url
          // gh's repo surface carries `isPrivate` → a confirmed verdict, Some.
          Private = Some r.IsPrivate }

    /// Fold gh's per-check buckets into one coarse status: any fail/cancel ⇒ Failing;
    /// else any pending ⇒ Pending; else any pass ⇒ Passing; else — if there are only
    /// unmodelled (`Unknown`) checks — Pending (conservatively "not known to be done");
    /// else None. `Skipping` is a deliberate terminal no-op.
    let private aggregate (checks: VcsToolkit.GitHub.CheckRun list) : CiStatus =
        let mutable anyFailing = false
        let mutable anyPending = false
        let mutable anyPass = false
        let mutable anyUnknown = false

        for c in checks do
            if c.Bucket.IsFailing then
                anyFailing <- true
            elif c.Bucket.IsPending then
                anyPending <- true
            elif c.Bucket.IsPassing then
                anyPass <- true
            elif c.Bucket.IsUnknown then
                anyUnknown <- true

        if anyFailing then CiStatus.Failing
        elif anyPending then CiStatus.Pending
        elif anyPass then CiStatus.Passing
        elif anyUnknown then CiStatus.Pending
        else CiStatus.None

    // --- operations ----------------------------------------------------------

    let authStatus (gh: VcsToolkit.GitHub.GitHub) =
        task {
            let! r = gh.AuthStatus()
            return ofForge r
        }

    /// Fail-open version probe for `Capabilities`: the parsed CLI version, or `None` when
    /// the `--version` probe failed or didn't parse (never blocks capability reporting).
    /// `probe` is the handle's cached one-shot version probe (`GitHubVersionProbe`) —
    /// awaiting `probe.Value` replays the already-fetched result instead of spawning
    /// `--version` again. `Capabilities()` reuses the same cache (see `Forge.fs`) rather
    /// than probing independently, since the installed CLI's version cannot change within
    /// the handle's lifetime.
    let detectVersion (probe: GitHubVersionProbe) =
        task {
            match! probe.Value with
            | Ok caps -> return Some caps.Version
            | Error _ -> return None
        }

    /// Version-gate a typed operation: refuse `op` up front with a structural
    /// `UnsupportedVersion` when the detected gh version is confirmed below the wrapper's
    /// floor. A version that can't be probed or parsed falls through (fail-open) — the gate
    /// only ever blocks a *confirmed* too-old CLI, never fails a call that would otherwise run.
    /// `probe` is the handle's cached one-shot version probe — see `detectVersion`.
    let ensureVersion (probe: GitHubVersionProbe) (op: string) =
        task {
            match! probe.Value with
            | Ok caps when not caps.IsSupported ->
                return
                    Error(
                        ForgeError.UnsupportedVersion(
                            ForgeKind.GitHub,
                            op,
                            caps.Version,
                            VcsToolkit.GitHub.GitHubCapabilities.MinimumSupported
                        )
                    )
            | _ -> return Ok()
        }

    let repoView (gh: VcsToolkit.GitHub.GitHub) (dir: string) =
        task {
            match! gh.RepoView dir with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok repo -> return Ok(mapRepo repo)
        }

    /// The unified `PrListState` maps 1:1 onto gh's own `--state open|closed|merged|all`.
    let private ghPrState (state: PrListState) : VcsToolkit.GitHub.PrListState =
        match state with
        | PrListState.Open -> VcsToolkit.GitHub.PrListState.Open
        | PrListState.Closed -> VcsToolkit.GitHub.PrListState.Closed
        | PrListState.Merged -> VcsToolkit.GitHub.PrListState.Merged
        | PrListState.All -> VcsToolkit.GitHub.PrListState.All

    /// The unified `IssueListState` maps 1:1 onto gh's own `--state open|closed|all`.
    let private ghIssueState (state: IssueListState) : VcsToolkit.GitHub.IssueListState =
        match state with
        | IssueListState.Open -> VcsToolkit.GitHub.IssueListState.Open
        | IssueListState.Closed -> VcsToolkit.GitHub.IssueListState.Closed
        | IssueListState.All -> VcsToolkit.GitHub.IssueListState.All

    let prList (gh: VcsToolkit.GitHub.GitHub) (dir: string) (options: PrListOptions) =
        task {
            let ghOptions: VcsToolkit.GitHub.PrListOptions =
                { State = ghPrState options.State
                  Limit = options.Limit }

            match! gh.PrList(dir, ghOptions) with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok prs -> return Ok(prs |> List.map mapPr)
        }

    /// Pull requests whose head is `sourceBranch`, in any state, against any base branch
    /// (`gh pr list --head <branch> --state all …`, the two-argument `PrListForBranch`).
    let prForBranch (gh: VcsToolkit.GitHub.GitHub) (dir: string) (sourceBranch: string) =
        task {
            match! gh.PrListForBranch(dir, sourceBranch) with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok prs -> return Ok(prs |> List.map mapPr)
        }

    let prView (gh: VcsToolkit.GitHub.GitHub) (dir: string) (number: uint64) =
        task {
            match! gh.PrView(dir, number) with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok pr -> return Ok(mapPr pr)
        }

    let prCreate (gh: VcsToolkit.GitHub.GitHub) (dir: string) (spec: PrCreate) =
        task {
            // The unified source/target map onto gh's head/base.
            let create =
                VcsToolkit.GitHub.PrCreate.Create(spec.Title, spec.Body)
                |> fun c ->
                    match spec.Source with
                    | Some s -> c.WithHead s
                    | None -> c
                |> fun c ->
                    match spec.Target with
                    | Some t -> c.WithBase t
                    | None -> c

            let! r = gh.PrCreate(dir, create)
            return ofForge r
        }

    let prComment (gh: VcsToolkit.GitHub.GitHub) (dir: string) (number: uint64) (body: string) =
        task {
            let! r = gh.PrComment(dir, number, body)
            return ofForge r
        }

    let prEdit (gh: VcsToolkit.GitHub.GitHub) (dir: string) (number: uint64) (edit: PrEdit) =
        task {
            let ghEdit =
                VcsToolkit.GitHub.PrEdit.Create()
                |> fun e ->
                    match edit.Title with
                    | Some t -> e.WithTitle t
                    | None -> e
                |> fun e ->
                    match edit.Body with
                    | Some b -> e.WithBody b
                    | None -> e

            let! r = gh.PrEdit(dir, number, ghEdit)
            return ofForge r
        }

    let prMerge (gh: VcsToolkit.GitHub.GitHub) (dir: string) (number: uint64) (merge: PrMerge) =
        task {
            // Map the unified strategy onto gh's own PrMerge, then carry `Auto`/`DeleteBranch`
            // through to gh's real `--auto`/`--delete-branch` flags.
            let strategy =
                match merge.Strategy with
                | MergeStrategy.Merge -> VcsToolkit.GitHub.PrMerge.Merge
                | MergeStrategy.Squash -> VcsToolkit.GitHub.PrMerge.Squash
                | MergeStrategy.Rebase -> VcsToolkit.GitHub.PrMerge.Rebase

            let ghMerge =
                strategy
                |> fun m -> if merge.Auto then m.WithAuto() else m
                |> fun m -> if merge.DeleteBranch then m.WithDeleteBranch() else m

            let! r = gh.PrMerge(dir, number, ghMerge)
            return ofForge r
        }

    let prMarkReady (gh: VcsToolkit.GitHub.GitHub) (dir: string) (number: uint64) =
        task {
            let! r = gh.PrMarkReady(dir, number)
            return ofForge r
        }

    let prClose (gh: VcsToolkit.GitHub.GitHub) (dir: string) (number: uint64) (deleteBranch: bool) =
        task {
            let! r = gh.PrClose(dir, number, deleteBranch)
            return ofForge r
        }

    let prCheckout (gh: VcsToolkit.GitHub.GitHub) (dir: string) (number: uint64) =
        task {
            let! r = gh.PrCheckout(dir, number)
            return ofForge r
        }

    let prReview (gh: VcsToolkit.GitHub.GitHub) (dir: string) (number: uint64) (action: ReviewAction) =
        task {
            // Map the unified review action onto gh's own ReviewAction. RequestChanges/Comment
            // carry a body by the unified ReviewAction's construction invariant, so the None arms
            // are unreachable — fall back to a comment with an empty body rather than throw.
            let ghAction =
                match action.Kind, action.Body with
                | ReviewKind.Approve, None -> VcsToolkit.GitHub.ReviewAction.Approve
                | ReviewKind.Approve, Some b -> VcsToolkit.GitHub.ReviewAction.Approve.WithBody b
                | ReviewKind.RequestChanges, Some b -> VcsToolkit.GitHub.ReviewAction.RequestChanges b
                | ReviewKind.Comment, Some b -> VcsToolkit.GitHub.ReviewAction.Comment b
                | ReviewKind.RequestChanges, None
                | ReviewKind.Comment, None -> VcsToolkit.GitHub.ReviewAction.Comment ""

            let! r = gh.PrReview(dir, number, ghAction)
            return ofForge r
        }

    let prChecks (gh: VcsToolkit.GitHub.GitHub) (dir: string) (number: uint64) =
        task {
            match! gh.PrChecks(dir, number) with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok checks -> return Ok(aggregate checks)
        }

    let prDiff (gh: VcsToolkit.GitHub.GitHub) (dir: string) (number: uint64) =
        task {
            let! r = gh.PrDiff(dir, number)
            return ofForge r
        }

    let issueList (gh: VcsToolkit.GitHub.GitHub) (dir: string) (options: IssueListOptions) =
        task {
            let ghOptions: VcsToolkit.GitHub.IssueListOptions =
                { State = ghIssueState options.State
                  Limit = options.Limit }

            match! gh.IssueList(dir, ghOptions) with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok issues -> return Ok(issues |> List.map mapIssue)
        }

    let issueView (gh: VcsToolkit.GitHub.GitHub) (dir: string) (number: uint64) =
        task {
            match! gh.IssueView(dir, number) with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok issue -> return Ok(mapIssue issue)
        }

    let issueCreate (gh: VcsToolkit.GitHub.GitHub) (dir: string) (title: string) (body: string) =
        task {
            let! r = gh.IssueCreate(dir, title, body)
            return ofForge r
        }

    let issueClose (gh: VcsToolkit.GitHub.GitHub) (dir: string) (number: uint64) =
        task {
            let! r = gh.IssueClose(dir, number)
            return ofForge r
        }

    let issueComment (gh: VcsToolkit.GitHub.GitHub) (dir: string) (number: uint64) (body: string) =
        task {
            let! r = gh.IssueComment(dir, number, body)
            return ofForge r
        }

    let releaseList (gh: VcsToolkit.GitHub.GitHub) (dir: string) =
        task {
            match! gh.ReleaseList dir with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok releases -> return Ok(releases |> List.map mapRelease)
        }

    let releaseView (gh: VcsToolkit.GitHub.GitHub) (dir: string) (tag: string) =
        task {
            match! gh.ReleaseView(dir, tag) with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok release -> return Ok(mapRelease release)
        }

    let releaseCreate (gh: VcsToolkit.GitHub.GitHub) (dir: string) (spec: ReleaseCreate) =
        task {
            let create =
                VcsToolkit.GitHub.ReleaseCreate.Create spec.Tag
                |> fun c ->
                    match spec.Title with
                    | Some t -> c.WithTitle t
                    | None -> c
                |> fun c ->
                    match spec.Notes with
                    | Some n -> c.WithNotes n
                    | None -> c
                |> fun c -> if spec.Draft then c.WithDraft() else c
                |> fun c -> if spec.Prerelease then c.WithPrerelease() else c

            let! r = gh.ReleaseCreate(dir, create)
            return ofForge r
        }
