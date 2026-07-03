namespace VcsToolkit.Forge

open System

/// GitLab-backed implementations of the facade operations: thin calls to the
/// `VcsToolkit.GitLab` client plus pure mappers from its types into the unified DTOs.
module internal GitLabForge =

    let private stateOf (state: string) : ForgePrState =
        // GitLab REST emits lowercase; match case-insensitively for parity.
        match state.ToLowerInvariant() with
        | "merged" -> ForgePrState.Merged
        | "closed"
        | "locked" -> ForgePrState.Closed
        | _ -> ForgePrState.Open

    let private issueStateOf (state: string) : ForgeIssueState =
        if state.Equals("closed", StringComparison.OrdinalIgnoreCase) then
            ForgeIssueState.Closed
        else
            ForgeIssueState.Open

    let private strOpt (s: string) : string option = if s = "" then None else Some s

    let private mapMr (mr: VcsToolkit.GitLab.MergeRequest) : ForgePr =
        { Number = mr.Iid
          Title = mr.Title
          State = stateOf mr.State
          SourceBranch = mr.SourceBranch
          TargetBranch = mr.TargetBranch
          Url = mr.Url
          Draft = mr.Draft }

    let private mapIssue (i: VcsToolkit.GitLab.Issue) : ForgeIssue =
        { Number = i.Number
          Title = i.Title
          State = issueStateOf i.State
          Body = i.Body
          Url = i.Url }

    let private mapRelease (r: VcsToolkit.GitLab.Release) : ForgeRelease =
        { Tag = r.TagName
          Title = r.Name
          Url = r.Url
          PublishedAt = strOpt r.PublishedAt
          Body = strOpt r.Description
          // GitLab has no draft/pre-release concept on a release.
          Draft = false
          Prerelease = false }

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
          // Conservative: only claim privacy when the visibility is *known* and not
          // "public". An absent visibility (`None`) is unknown → `false` (public).
          Private =
            match p.Visibility with
            | Some v -> v <> "public"
            | None -> false }

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
            let! r = glab.MrReady(dir, number)
            return ofForge r
        }

    // `delete_branch` has no `glab mr close` equivalent, so it is ignored here.
    let prClose (glab: VcsToolkit.GitLab.GitLab) (dir: string) (number: uint64) =
        task {
            let! r = glab.MrClose(dir, number)
            return ofForge r
        }

    let prChecks (glab: VcsToolkit.GitLab.GitLab) (dir: string) (number: uint64) =
        task {
            match! glab.MrChecks(dir, number) with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok ci -> return Ok(mapCi ci)
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
