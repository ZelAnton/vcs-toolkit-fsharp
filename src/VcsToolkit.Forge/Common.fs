namespace VcsToolkit.Forge

open VcsToolkit.CliSupport

/// Helpers shared by the three CLI-backed adapters' pure mappers
/// (`GitHubForge`/`GitLabForge`/`GiteaForge`): normalising a raw CLI state string against
/// the unified `ForgePrState`/`ForgeIssueState` contract, and reading an "empty means
/// absent" CLI field into an `option`.
module internal Common =

    /// Whether `state` (a raw CLI state string — gh's uppercase, glab's lowercase, or
    /// tea's `String.Equals(..., OrdinalIgnoreCase)` style) case-insensitively matches
    /// `expected` (already lowercase). ASCII-only fold, matching the `Classify.asciiLower`
    /// convention (T-070) used elsewhere in this codebase for CLI-output classification —
    /// avoids the spurious matches a full-Unicode fold (`ToLowerInvariant`/`OrdinalIgnoreCase`)
    /// could introduce.
    let stateEquals (state: string) (expected: string) : bool = asciiLower state = expected

    /// Empty string (a CLI's "no value" for a required-but-blank JSON field) → `None`;
    /// anything else → `Some`.
    let strOpt (s: string) : string option = if s = "" then None else Some s

/// The single source of truth for the *kind/variant-dependent* `Unsupported` verdicts — the
/// ones `ForgeOp`/`Forge.Supports` cannot express because the operation itself exists on every
/// CLI yet refuses a specific review kind or merge/close option. Both the facade's up-front
/// introspection (`Forge.SupportsReview`/`SupportsMergeOptions`/`SupportsCloseDeleteBranch`) and
/// the dispatch's structural pre-checks (`Forge.PrReview`/`PrMerge`/`PrClose`) read these
/// predicates, so the "can I?" answer and the "is it refused?" behaviour can never drift apart:
/// flip a predicate and both move together. Operation-level gaps (chiefly Gitea, whose `tea`
/// lacks whole commands) stay in `Forge.Supports(ForgeOp)`, which this module does not touch.
module internal ForgeSupport =

    /// Whether `kind`'s CLI can submit a `prReview` of `reviewKind`. `Approve` maps to a real
    /// verb on all three forges (`gh pr review --approve` / `glab mr approve` / `tea pr
    /// approve`). `RequestChanges` maps to one on GitHub (`--request-changes`) and Gitea (`tea
    /// pr reject`), but `glab` has none — deliberately not composed from note+revoke, whose two
    /// separate calls risk a partial apply on a foreign MR. A `Comment`-review maps to one only
    /// on GitHub (`--comment`); on GitLab and Gitea a plain `PrComment` posts the note instead.
    /// The CLI-less `Unknown` handle can submit none. A future forge reads as unsupported for
    /// the non-`Approve` kinds until its support is confirmed.
    let review (kind: ForgeKind) (reviewKind: ReviewKind) : bool =
        match kind, reviewKind with
        | ForgeKind.Unknown, _ -> false
        | _, ReviewKind.Approve -> true
        | ForgeKind.GitHub, _ -> true
        | ForgeKind.Gitea, ReviewKind.RequestChanges -> true
        // GitLab request-changes/comment, Gitea comment-review, and any future forge until its
        // support is confirmed: no native verb, so unsupported.
        | _ -> false

    /// Whether `kind`'s CLI honours `prMerge`'s auto-merge / delete-source-branch options. They
    /// map to real `gh` flags (`--auto`/`--delete-branch`) on GitHub only; `glab`/`tea` (and the
    /// CLI-less `Unknown` handle) expose no confirmed equivalent, so a spec asking for either is
    /// refused there before any spawn rather than silently dropping the option. A plain strategy
    /// merge works everywhere regardless.
    let mergeOptions (kind: ForgeKind) : bool =
        match kind with
        | ForgeKind.GitHub -> true
        | _ -> false

    /// Whether `kind`'s CLI can delete the source branch when closing a PR/MR. It maps to a real
    /// `gh` flag (`pr close --delete-branch`) on GitHub only; `glab`/`tea` (and `Unknown`) expose
    /// no confirmed equivalent, so requesting it is refused there before any spawn. Closing
    /// without deleting the branch works on all three.
    let closeDeleteBranch (kind: ForgeKind) : bool =
        match kind with
        | ForgeKind.GitHub -> true
        | _ -> false

    /// Whether `kind`'s CLI honours `releaseCreate`'s draft / pre-release options. They map to
    /// real flags (`--draft`/`--prerelease`) on GitHub (`gh`) and Gitea (`tea`); `glab` has no
    /// release draft/pre-release concept (mirroring `ForgeRelease.Draft`/`Prerelease` being `None`
    /// on GitLab), and the CLI-less `Unknown` handle honours none — so a spec asking for either is
    /// refused there before any spawn rather than silently dropping the option. A plain release
    /// works everywhere regardless.
    let releaseOptions (kind: ForgeKind) : bool =
        match kind with
        | ForgeKind.GitHub
        | ForgeKind.Gitea -> true
        | _ -> false

    /// The `prReview <kind>` operation label an `Unsupported` verdict carries for a refused
    /// review kind (the string surfaced by `ForgeError.Unsupported`/`Message`).
    let private reviewOpLabel (reviewKind: ReviewKind) : string =
        match reviewKind with
        | ReviewKind.Approve -> "prReview approve"
        | ReviewKind.RequestChanges -> "prReview requestChanges"
        | ReviewKind.Comment -> "prReview comment"

    /// The structural `Unsupported` verdict for a `prReview` whose kind `kind`'s CLI cannot
    /// submit — `None` when it can (dispatch proceeds). Derived from `review`, so it agrees with
    /// `Forge.SupportsReview` by construction.
    let unsupportedReview (kind: ForgeKind) (action: ReviewAction) : ForgeError option =
        if review kind action.Kind then
            None
        else
            Some(ForgeError.Unsupported(kind, reviewOpLabel action.Kind))

    /// The structural `Unsupported` verdict for a `prMerge` whose spec asks for an option
    /// `kind`'s CLI will not honour — `None` for a plain strategy merge, or when the options are
    /// supported. Derived from `mergeOptions`, so it agrees with `Forge.SupportsMergeOptions`.
    let unsupportedMerge (kind: ForgeKind) (merge: PrMerge) : ForgeError option =
        if (merge.Auto || merge.DeleteBranch) && not (mergeOptions kind) then
            Some(ForgeError.Unsupported(kind, "prMerge auto/delete-branch"))
        else
            None

    /// The structural `Unsupported` verdict for a `prClose` asking to delete the source branch on
    /// a `kind` whose CLI cannot — `None` when not requested, or when it is supported. Derived
    /// from `closeDeleteBranch`, so it agrees with `Forge.SupportsCloseDeleteBranch`.
    let unsupportedCloseDeleteBranch (kind: ForgeKind) (deleteBranch: bool) : ForgeError option =
        if deleteBranch && not (closeDeleteBranch kind) then
            Some(ForgeError.Unsupported(kind, "prClose delete-branch"))
        else
            None

    /// The structural `Unsupported` verdict for a `releaseCreate` whose spec asks for a draft /
    /// pre-release option `kind`'s CLI will not honour — `None` for a plain release, or when the
    /// options are supported. Derived from `releaseOptions`, so it agrees with
    /// `Forge.SupportsReleaseOptions`.
    let unsupportedReleaseCreate (kind: ForgeKind) (spec: ReleaseCreate) : ForgeError option =
        if (spec.Draft || spec.Prerelease) && not (releaseOptions kind) then
            Some(ForgeError.Unsupported(kind, "releaseCreate draft/prerelease"))
        else
            None
