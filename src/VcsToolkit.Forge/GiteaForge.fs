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
          Assignees = None
          // tea's csv PR surface (`--output csv` + `--fields`, K-049/T-115) carries no
          // author/timestamp/milestone columns → the honest "unknown", None (same contract as
          // Labels/Assignees/Draft above), never a fabricated value.
          Author = None
          CreatedAt = None
          UpdatedAt = None
          Milestone = None }

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
          Assignees = None
          // tea's csv issue surface has no author/timestamp/milestone columns → None (unknown).
          Author = None
          CreatedAt = None
          UpdatedAt = None
          Milestone = None }

    let private mapRelease (r: VcsToolkit.Gitea.Release) : ForgeRelease =
        { Tag = r.Tag
          Title = r.Title
          Url = r.Url
          PublishedAt = Common.strOpt r.PublishedAt
          // `tea` has no release body/notes column.
          Body = Option.None
          // tea's release `Status` column carries draft/prerelease → Some.
          Draft = Some r.Draft
          Prerelease = Some r.Prerelease
          // tea's release csv table has no author column → None (unknown), never fabricated.
          Author = Option.None }

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

    /// `tea pr list --output json` does not work against the real CLI for ANY state value —
    /// see K-049: the `--output json` flag itself is rejected regardless of `--state` (`tea`
    /// prints `unknown output type 'json', available types are: ...` with exit code 0, which
    /// is exactly what produced the confusing downstream JSON-parse failure), so there is no
    /// working listing path to reach even for `Open`/`All`. `Closed`/`Merged` previously had
    /// their own *additional* documented reason on top of that (isolating either from a
    /// `--state all` fetch risks silently dropping matches past `--limit`, since a closed
    /// row's `state` column can itself read `"merged"` — see `GiteaParse`/`mapPr`'s
    /// `Merged`-flag derivation) — that reasoning still holds, but is now subsumed by this
    /// more fundamental, blanket one. Refuse structurally, before any spawn, for every state:
    /// this turns what would otherwise be a confusing runtime JSON-parse failure into a
    /// single honest, consistent "unsupported" signal, rather than only for two of the four
    /// states.
    let prList (_tea: VcsToolkit.Gitea.Gitea) (_dir: string) (options: PrListOptions) =
        task {
            return
                Error(
                    ForgeError.Unsupported(
                        ForgeKind.Gitea,
                        sprintf
                            "prList(%A): `tea pr list --output json` does not work against the real CLI (K-049) — no state is listable yet"
                            options.State
                    )
                )
        }

    /// `tea pr list --output json` does not work against the real CLI for ANY state (K-049,
    /// see `prList` above) — there is no working listing path to filter by source branch on
    /// our side either, so refuse structurally, before any spawn, the same way `prList` does.
    let prForBranch (_tea: VcsToolkit.Gitea.Gitea) (_dir: string) (sourceBranch: string) =
        task {
            return
                Error(
                    ForgeError.Unsupported(
                        ForgeKind.Gitea,
                        sprintf
                            "prForBranch(%s): `tea pr list --output json` does not work against the real CLI (K-049) — no listing path to filter by source branch"
                            sourceBranch
                    )
                )
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

    /// `tea` 0.9.2 has no `pr edit` command at all — an unrecognised `pr edit` silently falls
    /// through to a plain `pr list` instead of editing (K-063; confirmed against the real tea
    /// 0.9.2 binary and its Go source). There is no working edit path to reach, so refuse
    /// structurally, before any spawn, exactly like `prList`/`prForBranch` — turning what would
    /// otherwise be a silent no-op into an honest `Unsupported` signal.
    let prEdit (_tea: VcsToolkit.Gitea.Gitea) (_dir: string) (number: uint64) (_edit: PrEdit) =
        task {
            return
                Error(
                    ForgeError.Unsupported(
                        ForgeKind.Gitea,
                        sprintf
                            "prEdit(#%d): `tea` 0.9.2 has no `pr edit` command (an unrecognised `pr edit` silently falls through to `pr list`; K-063) — edit a PR's title/body via the Gitea REST API instead"
                            number
                    )
                )
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

    /// `tea issues list --output json` is unsupported by the real CLI for every state — the
    /// identical K-049 root cause as `prList` above (the `--output json` flag itself is
    /// rejected, not something state-specific). Refuse structurally, before any spawn, for
    /// every state.
    let issueList (_tea: VcsToolkit.Gitea.Gitea) (_dir: string) (options: IssueListOptions) =
        task {
            return
                Error(
                    ForgeError.Unsupported(
                        ForgeKind.Gitea,
                        sprintf
                            "issueList(%A): `tea issues list --output json` does not work against the real CLI (K-049) — no state is listable yet"
                            options.State
                    )
                )
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

    let releaseCreate (tea: VcsToolkit.Gitea.Gitea) (dir: string) (spec: ReleaseCreate) =
        task {
            let create =
                VcsToolkit.Gitea.ReleaseCreate.Create spec.Tag
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

            let! r = tea.ReleaseCreate(dir, create)
            return ofForge r
        }
