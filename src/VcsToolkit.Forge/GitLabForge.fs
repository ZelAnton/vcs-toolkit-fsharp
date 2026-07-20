namespace VcsToolkit.Forge

open System.Threading.Tasks
open ProcessKit

/// A handle's one-shot, per-handle cache of the `glab` version probe
/// (`GitLab.Capabilities()`) — see `GitHubVersionProbe` for the caching rationale.
type internal GitLabVersionProbe = Lazy<Task<Result<VcsToolkit.GitLab.GitLabCapabilities, ProcessError>>>

/// GitLab-backed implementations of the facade operations: thin calls to the
/// `VcsToolkit.GitLab` client plus pure mappers from its types into the unified DTOs.
module internal GitLabForge =

    let private stateOf (state: string) : ForgePrState =
        // GitLab REST emits lowercase; match case-insensitively for parity.
        if Common.stateEquals state "merged" then
            ForgePrState.Merged
        elif Common.stateEquals state "closed" || Common.stateEquals state "locked" then
            ForgePrState.Closed
        else
            ForgePrState.Open

    let private issueStateOf (state: string) : ForgeIssueState =
        if Common.stateEquals state "closed" then
            ForgeIssueState.Closed
        else
            ForgeIssueState.Open

    let private mapMr (mr: VcsToolkit.GitLab.MergeRequest) : ForgePr =
        { Number = mr.Iid
          Title = mr.Title
          State = stateOf mr.State
          SourceBranch = mr.SourceBranch
          TargetBranch = mr.TargetBranch
          Url = mr.Url
          // GitLab's MR surface carries `draft` → a confirmed verdict, Some.
          Draft = Some mr.Draft
          // GitLab's REST MR always carries labels/assignees → confirmed values (an empty
          // list is a confirmed "none", never unknown).
          Labels = Some mr.Labels
          Assignees = Some mr.Assignees }

    let private mapIssue (i: VcsToolkit.GitLab.Issue) : ForgeIssue =
        { Number = i.Number
          Title = i.Title
          State = issueStateOf i.State
          Body = i.Body
          Url = i.Url
          // GitLab's REST issue always carries labels/assignees → confirmed values.
          Labels = Some i.Labels
          Assignees = Some i.Assignees }

    let private mapRelease (r: VcsToolkit.GitLab.Release) : ForgeRelease =
        { Tag = r.TagName
          Title = r.Name
          Url = r.Url
          PublishedAt = Common.strOpt r.PublishedAt
          Body = Common.strOpt r.Description
          // GitLab has no draft/pre-release concept on a release → unknown, None (not a
          // fabricated `Some false`).
          Draft = None
          Prerelease = None }

    let private mapProject (p: VcsToolkit.GitLab.Repo) : ForgeRepo =
        // GitLab has no separate "owner" — everything before the last `/` in the
        // namespace path is the owner, the last segment the project slug.
        let owner =
            match p.PathWithNamespace.LastIndexOf('/') with
            | i when i >= 0 -> p.PathWithNamespace.Substring(0, i)
            | _ -> ""

        { Name = p.Name
          Owner = owner
          DefaultBranch = p.DefaultBranch
          Url = p.Url
          // Only report privacy when glab actually returned a visibility: a known value
          // maps to `Some (v <> "public")`, an absent one to `None` (unknown) — never a
          // fabricated `Some false` that reads as a confirmed public repo.
          Private =
            match p.Visibility with
            | Some v -> Some(v <> "public")
            | None -> None }

    let private mapCi (c: VcsToolkit.GitLab.CiStatus) : CiStatus =
        match c with
        | VcsToolkit.GitLab.CiStatus.Passing -> CiStatus.Passing
        | VcsToolkit.GitLab.CiStatus.Failing -> CiStatus.Failing
        | VcsToolkit.GitLab.CiStatus.Pending -> CiStatus.Pending
        | VcsToolkit.GitLab.CiStatus.None -> CiStatus.None

    // --- operations ----------------------------------------------------------

    let authStatus (glab: VcsToolkit.GitLab.GitLab) =
        task {
            let! r = glab.AuthStatus()
            return ofForge r
        }

    /// Fail-open version probe for `Capabilities`: the parsed CLI version, or `None` when
    /// the `--version` probe failed or didn't parse (never blocks capability reporting).
    /// `probe` is the handle's cached one-shot version probe (`GitLabVersionProbe`) —
    /// awaiting `probe.Value` replays the already-fetched result instead of spawning
    /// `--version` again. `Capabilities()` reuses the same cache (see `Forge.fs`) rather
    /// than probing independently, since the installed CLI's version cannot change within
    /// the handle's lifetime.
    let detectVersion (probe: GitLabVersionProbe) =
        task {
            match! probe.Value with
            | Ok caps -> return Some caps.Version
            | Error _ -> return None
        }

    /// Version-gate a typed operation: refuse `op` up front with a structural
    /// `UnsupportedVersion` when the detected glab version is confirmed below the wrapper's
    /// floor. A version that can't be probed or parsed falls through (fail-open) — the gate
    /// only ever blocks a *confirmed* too-old CLI, never fails a call that would otherwise run.
    /// `probe` is the handle's cached one-shot version probe — see `detectVersion`.
    let ensureVersion (probe: GitLabVersionProbe) (op: string) =
        task {
            match! probe.Value with
            | Ok caps when not caps.IsSupported ->
                return
                    Error(
                        ForgeError.UnsupportedVersion(
                            ForgeKind.GitLab,
                            op,
                            caps.Version,
                            VcsToolkit.GitLab.GitLabCapabilities.MinimumSupported
                        )
                    )
            | _ -> return Ok()
        }

    let repoView (glab: VcsToolkit.GitLab.GitLab) (dir: string) =
        task {
            match! glab.RepoView dir with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok project -> return Ok(mapProject project)
        }

    let prList (glab: VcsToolkit.GitLab.GitLab) (dir: string) =
        task {
            match! glab.MrList dir with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok mrs -> return Ok(mrs |> List.map mapMr)
        }

    let prView (glab: VcsToolkit.GitLab.GitLab) (dir: string) (number: uint64) =
        task {
            match! glab.MrView(dir, number) with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok mr -> return Ok(mapMr mr)
        }

    let prCreate (glab: VcsToolkit.GitLab.GitLab) (dir: string) (spec: PrCreate) =
        task {
            // The unified source/target ARE glab's naming — a 1:1 field map.
            let create =
                VcsToolkit.GitLab.MrCreate.Create(spec.Title, spec.Body)
                |> fun c ->
                    match spec.Source with
                    | Some s -> c.WithSource s
                    | None -> c
                |> fun c ->
                    match spec.Target with
                    | Some t -> c.WithTarget t
                    | None -> c

            let! r = glab.MrCreate(dir, create)
            return ofForge r
        }

    let prComment (glab: VcsToolkit.GitLab.GitLab) (dir: string) (number: uint64) (body: string) =
        task {
            let! r = glab.MrComment(dir, number, body)
            return ofForge r
        }

    let prEdit (glab: VcsToolkit.GitLab.GitLab) (dir: string) (number: uint64) (edit: PrEdit) =
        task {
            let glEdit =
                VcsToolkit.GitLab.MrEdit.Create()
                |> fun e ->
                    match edit.Title with
                    | Some t -> e.WithTitle t
                    | None -> e
                |> fun e ->
                    match edit.Body with
                    | Some b -> e.WithBody b
                    | None -> e

            let! r = glab.MrEdit(dir, number, glEdit)
            return ofForge r
        }

    /// `glab mr merge` exposes no confirmed auto-merge / delete-source-branch flag, so the
    /// unified spec's `Auto`/`DeleteBranch` can't be honoured on GitLab. Report a structural
    /// `Unsupported` when either is asked for (so the facade refuses it before any spawn rather
    /// than silently dropping the option); `None` for a plain, supportable strategy merge.
    let unsupportedMerge (merge: PrMerge) : ForgeError option =
        if merge.Auto || merge.DeleteBranch then
            Some(ForgeError.Unsupported(ForgeKind.GitLab, "prMerge auto/delete-branch"))
        else
            None

    /// `glab mr close` exposes no confirmed delete-source-branch flag. Report a structural
    /// `Unsupported` when it is requested so the facade refuses it before any spawn rather than
    /// silently dropping the option; `None` when closing without deleting the branch.
    let unsupportedClose (deleteBranch: bool) : ForgeError option =
        if deleteBranch then
            Some(ForgeError.Unsupported(ForgeKind.GitLab, "prClose delete-branch"))
        else
            None

    let prMerge (glab: VcsToolkit.GitLab.GitLab) (dir: string) (number: uint64) (strategy: MergeStrategy) =
        task {
            let ms =
                match strategy with
                | MergeStrategy.Merge -> VcsToolkit.GitLab.MergeStrategy.Merge
                | MergeStrategy.Squash -> VcsToolkit.GitLab.MergeStrategy.Squash
                | MergeStrategy.Rebase -> VcsToolkit.GitLab.MergeStrategy.Rebase

            let! r = glab.MrMerge(dir, number, ms)
            return ofForge r
        }

    let prMarkReady (glab: VcsToolkit.GitLab.GitLab) (dir: string) (number: uint64) =
        task {
            let! r = glab.MrMarkReady(dir, number)
            return ofForge r
        }


    let prClose (glab: VcsToolkit.GitLab.GitLab) (dir: string) (number: uint64) =
        task {
            let! r = glab.MrClose(dir, number)
            return ofForge r
        }

    let prCheckout (glab: VcsToolkit.GitLab.GitLab) (dir: string) (number: uint64) =
        task {
            let! r = glab.MrCheckout(dir, number)
            return ofForge r
        }

    let prChecks (glab: VcsToolkit.GitLab.GitLab) (dir: string) (number: uint64) =
        task {
            match! glab.MrChecks(dir, number) with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok ci -> return Ok(mapCi ci)
        }

    let prDiff (glab: VcsToolkit.GitLab.GitLab) (dir: string) (number: uint64) =
        task {
            let! r = glab.MrDiff(dir, number)
            return ofForge r
        }

    let issueList (glab: VcsToolkit.GitLab.GitLab) (dir: string) =
        task {
            match! glab.IssueList dir with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok issues -> return Ok(issues |> List.map mapIssue)
        }

    let issueView (glab: VcsToolkit.GitLab.GitLab) (dir: string) (number: uint64) =
        task {
            match! glab.IssueView(dir, number) with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok issue -> return Ok(mapIssue issue)
        }

    let issueCreate (glab: VcsToolkit.GitLab.GitLab) (dir: string) (title: string) (body: string) =
        task {
            let! r = glab.IssueCreate(dir, title, body)
            return ofForge r
        }

    let issueClose (glab: VcsToolkit.GitLab.GitLab) (dir: string) (number: uint64) =
        task {
            let! r = glab.IssueClose(dir, number)
            return ofForge r
        }

    let issueComment (glab: VcsToolkit.GitLab.GitLab) (dir: string) (number: uint64) (body: string) =
        task {
            let! r = glab.IssueComment(dir, number, body)
            return ofForge r
        }

    let releaseList (glab: VcsToolkit.GitLab.GitLab) (dir: string) =
        task {
            match! glab.ReleaseList dir with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok releases -> return Ok(releases |> List.map mapRelease)
        }

    let releaseView (glab: VcsToolkit.GitLab.GitLab) (dir: string) (tag: string) =
        task {
            match! glab.ReleaseView(dir, tag) with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok release -> return Ok(mapRelease release)
        }
