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
///
/// The listing operations are the one place where the calls are *not* thin: `tea`'s `pr list`
/// has neither a merged state nor a head-branch filter, so `prList`/`prForBranch` fetch a
/// superset through the client's csv listing and narrow it here — see `teaPrState`.
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

    /// The `tea pr list --state` bucket to fetch for a unified `PrListState`.
    ///
    /// `tea`'s `--state` takes only `open`/`closed`/`all`, and Gitea has no "merged" state at
    /// all: merging a PR *closes* it and sets a separate merged flag, which `tea` folds into
    /// its `state` column (a merged PR's cell reads `"merged"` — the flag `mapPr` keys off).
    /// So neither unified `Merged` nor unified `Closed` ("closed **without** merging") is a
    /// `--state` value: Gitea's closed bucket carries the merged PRs too.
    ///
    /// Each case therefore fetches the narrowest bucket that is a *confirmed superset* of the
    /// requested state, and the rows are narrowed on our side afterwards (`prMatchesState`) —
    /// rather than trusting `--state` to mean what the unified filter means:
    /// - `Open` → `--state open`; a closed or merged PR is never in it, so the local pass is
    ///   a no-op kept only for uniformity.
    /// - `Closed` → `--state closed`, minus the merged rows.
    /// - `Merged` → `--state all`, keeping only the merged rows. Deliberately **not**
    ///   `--state closed`: that Gitea's closed bucket really carries merged PRs is unconfirmed
    ///   against the real CLI (see `VcsToolkit.Gitea.PrListState`, and `PrView`, which walks
    ///   `--state all` for the same reason), and betting on it would turn a wrong guess into a
    ///   silent, permanent "no merged PRs" — while `--state all` is a superset by construction.
    /// - `All` → `--state all`, with nothing to narrow.
    let private teaPrState (state: PrListState) : VcsToolkit.Gitea.PrListState =
        match state with
        | PrListState.Open -> VcsToolkit.Gitea.PrListState.Open
        | PrListState.Closed -> VcsToolkit.Gitea.PrListState.Closed
        | PrListState.Merged -> VcsToolkit.Gitea.PrListState.All
        | PrListState.All -> VcsToolkit.Gitea.PrListState.All

    /// Whether an already-mapped `pr` belongs in a unified `state` listing — the our-side half
    /// of `teaPrState`'s "fetch a superset, then narrow" contract. It keys off the mapped
    /// `ForgePrState` (which derives `Merged` from tea's folded `state` column), so the filter
    /// and the `State` the caller ends up reading can never disagree.
    let private prMatchesState (state: PrListState) (pr: ForgePr) : bool =
        match state with
        | PrListState.All -> true
        | PrListState.Open -> pr.State = ForgePrState.Open
        | PrListState.Closed -> pr.State = ForgePrState.Closed
        | PrListState.Merged -> pr.State = ForgePrState.Merged

    /// Pull requests through the wrapper's typed csv listing (`tea pr list --state <bucket>
    /// --limit <n> --fields … --output csv`), narrowed to `options.State` here — see
    /// `teaPrState` for why the fetched bucket and the unified state are not the same thing.
    ///
    /// **The narrowing runs over the fetched window.** `tea` caps the fetch at `--limit` first
    /// (and the Gitea server clamps one page at ~50 rows regardless of a larger `--limit` —
    /// see the wrapper's `PrView` note), and only then are the non-matching rows dropped, so a
    /// `Closed`/`Merged` query can return fewer than `options.Limit` matches while older ones
    /// exist further back; a fetch that came back full is the hint that it might have. Raising
    /// `Limit` widens the window up to the server's page cap — to find one specific PR
    /// regardless of depth, use `prView`, which pages instead.
    let prList (tea: VcsToolkit.Gitea.Gitea) (dir: string) (options: PrListOptions) =
        task {
            let teaOptions: VcsToolkit.Gitea.PrListOptions =
                { State = teaPrState options.State
                  Limit = options.Limit }

            match! tea.PrList(dir, teaOptions) with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok prs -> return Ok(prs |> List.map mapPr |> List.filter (prMatchesState options.State))
        }

    /// Pull requests whose source (head) branch is `sourceBranch`, in any state. `tea pr list`
    /// has no head-branch filter, so this lists `--state all` through the same csv listing as
    /// `prList` and matches `HeadBranch` here, ordinally (git branch names are case-sensitive).
    /// No match is an empty list, never an error — the facade's contract.
    ///
    /// Unlike gh/glab, where the branch lands in argv (`--head`/`--source-branch`) and is
    /// argv-guarded before spawning, the name never reaches tea's command line here: a
    /// flag-like `--evil` is not an injection vector, it simply matches no PR. Shares
    /// `prList`'s fetched-window caveat — a PR older than the fetched window is not reported.
    let prForBranch (tea: VcsToolkit.Gitea.Gitea) (dir: string) (sourceBranch: string) =
        task {
            let teaOptions: VcsToolkit.Gitea.PrListOptions =
                VcsToolkit.Gitea.PrListOptions.Default.WithState VcsToolkit.Gitea.PrListState.All

            match! tea.PrList(dir, teaOptions) with
            | Error e -> return Error(ForgeError.Forge e)
            | Ok prs -> return Ok(prs |> List.filter (fun pr -> pr.HeadBranch = sourceBranch) |> List.map mapPr)
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

    /// The unified `IssueListState` maps 1:1 onto tea's own `--state open|closed|all`: issues
    /// have no merged state, so unlike `PrListState` there is no bucket-vs-filter mismatch to
    /// correct on our side.
    let private teaIssueState (state: IssueListState) : VcsToolkit.Gitea.IssueListState =
        match state with
        | IssueListState.Open -> VcsToolkit.Gitea.IssueListState.Open
        | IssueListState.Closed -> VcsToolkit.Gitea.IssueListState.Closed
        | IssueListState.All -> VcsToolkit.Gitea.IssueListState.All

    /// Issues through the wrapper's typed csv listing (`tea issues list --state <state>
    /// --limit <n> --fields … --output csv`). The state maps straight onto tea's own filter
    /// (see `teaIssueState`), so — unlike `prList` — nothing is narrowed on our side and the
    /// result is exactly what the CLI returned, capped at `options.Limit`.
    let issueList (tea: VcsToolkit.Gitea.Gitea) (dir: string) (options: IssueListOptions) =
        task {
            let teaOptions: VcsToolkit.Gitea.IssueListOptions =
                { State = teaIssueState options.State
                  Limit = options.Limit }

            match! tea.IssueList(dir, teaOptions) with
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
