namespace VcsToolkit.GitLab

open System.Text.Json
open VcsToolkit.CliSupport

/// A merge request (`glab mr list/view --output json`). The fields are GitLab's REST
/// `MergeRequest` object, which `glab` passes through unchanged.
type MergeRequest =
    {
        /// The **project-scoped** id (`iid`) — the `<id>` other `glab mr` commands take.
        /// (GitLab's global `id` is deliberately not surfaced.)
        Iid: uint64
        /// MR title.
        Title: string
        /// State, e.g. `"opened"`, `"closed"`, `"merged"`, `"locked"` (GitLab's
        /// lower-case spelling — note it is `"opened"`, not `"open"`).
        State: string
        /// Source (head) branch name.
        SourceBranch: string
        /// Target (base) branch name.
        TargetBranch: string
        /// Web URL.
        WebUrl: string
        /// Whether the MR is a draft (GitLab's `draft`; the deprecated
        /// `work_in_progress` is not read).
        Draft: bool
    }

/// A project, returned as `RepoView` (`glab repo view --output json`) — the fields are
/// GitLab's REST `Project` object.
type RepoView =
    {
        /// Project name (the last path segment's display name).
        Name: string
        /// Full namespace path, e.g. `"group/subgroup/repo"`.
        PathWithNamespace: string
        /// Default branch name (empty/null for an empty project).
        DefaultBranch: string
        /// Web URL.
        WebUrl: string
        /// Visibility, e.g. `"public"`, `"internal"`, `"private"`. `None` when glab
        /// omits the field — a consumer must treat an absent visibility as *unknown*,
        /// not as private.
        Visibility: string option
    }

/// An issue (`glab issue list/view --output json`). The fields are GitLab's REST
/// `Issue` object. Mirrors `MergeRequest`'s shape (project-scoped id, tolerant fields).
type Issue =
    {
        /// The **project-scoped** id (`iid`) — the `<id>` other `glab issue` commands
        /// take. Surfaced through the field name `Number` for cross-forge consistency
        /// with the GitHub/Gitea wrappers' `Issue`.
        Number: uint64
        /// Issue title.
        Title: string
        /// State, e.g. `"opened"`, `"closed"` (GitLab's lower-case spelling).
        State: string
        /// Issue body (GitLab's `description`, markdown); empty when absent/null.
        Body: string
        /// Web URL (GitLab's `web_url`).
        Url: string
    }

/// A release (`glab release list/view --output json`) — GitLab's REST `Release` object.
type Release =
    {
        /// The Git tag the release is attached to (the `<tag>` `releaseView` takes).
        TagName: string
        /// Release title (may be empty/absent/null — GitLab defaults it to the tag).
        Name: string
        /// Web URL of the release page. GitLab carries it as `_links.self` (there is no
        /// top-level `web_url` on a release), so it is pulled off that nested object;
        /// empty when absent.
        Url: string
        /// Publication timestamp (GitLab's `released_at`, ISO 8601); empty when
        /// absent/null (e.g. an upcoming/unpublished release).
        PublishedAt: string
        /// Release notes (GitLab's `description`, markdown); empty when absent/null.
        Description: string
    }

/// The coarse CI/pipeline outcome for an MR (`glab mr view … --output json`'s
/// `head_pipeline.status`), bucketed into the four states a caller acts on.
[<RequireQualifiedAccess>]
type CiStatus =
    /// The pipeline succeeded (`success`).
    | Passing
    /// The pipeline failed or was canceled (`failed`/`canceled`).
    | Failing
    /// The pipeline is still going (`running`/`pending`/`created`/…) **or is blocked
    /// awaiting action** (`manual`/`scheduled`/`waiting_for_resource`). The blocked
    /// states bucket here conservatively ("not known to be done"), so a poller that
    /// loops until this is no longer `Pending` should bound its wait — a `manual`
    /// pipeline stays blocked until someone triggers it and would otherwise be polled
    /// forever.
    | Pending
    /// No pipeline ran (none attached, or `skipped`).
    | None

    /// Bucket a raw GitLab pipeline `status` string. Unknown values — and the
    /// blocked-awaiting-action states `manual`/`scheduled` — read as `Pending`
    /// (conservative, "not known to be done"; see the variant docs on bounding a
    /// poller's wait).
    static member OfGitLab(status: string) : CiStatus =
        match status with
        | "success" -> CiStatus.Passing
        | "failed"
        | "canceled"
        | "cancelled" -> CiStatus.Failing
        | "skipped"
        | "" -> CiStatus.None
        | _ -> CiStatus.Pending

/// Tolerant parsers over `glab … --output json` output (GitLab's REST JSON, which
/// `glab` passes through). Each parser is total — a malformed document yields `Error`,
/// never an exception — building on the shared `Json` helpers.
[<RequireQualifiedAccess>]
module GitLabParse =

    let private toMr (el: JsonElement) : MergeRequest =
        { Iid = Json.u64Or el "iid"
          Title = Json.strOr el "title"
          State = Json.strOr el "state"
          SourceBranch = Json.strOr el "source_branch"
          TargetBranch = Json.strOr el "target_branch"
          WebUrl = Json.strOr el "web_url"
          Draft = Json.boolOr el "draft" }

    let private toRepoView (el: JsonElement) : RepoView =
        { Name = Json.strOr el "name"
          PathWithNamespace = Json.strOr el "path_with_namespace"
          DefaultBranch = Json.strOr el "default_branch"
          WebUrl = Json.strOr el "web_url"
          Visibility = Json.strOpt el "visibility" }

    let private toIssue (el: JsonElement) : Issue =
        { Number = Json.u64Or el "iid"
          Title = Json.strOr el "title"
          State = Json.strOr el "state"
          Body = Json.strOr el "description"
          Url = Json.strOr el "web_url" }

    let private toRelease (el: JsonElement) : Release =
        { TagName = Json.strOr el "tag_name"
          Name = Json.strOr el "name"
          // GitLab nests the release-page URL under `_links.self` (no top-level web_url).
          Url = Json.nestedStr el "_links" "self"
          PublishedAt = Json.strOr el "released_at"
          Description = Json.strOr el "description" }

    /// The pipeline status for an MR: `head_pipeline.status`, falling back to the
    /// deprecated `pipeline.status` only when `head_pipeline` is absent (matching the
    /// Rust `.or(...)` on the objects — a present `head_pipeline` wins even if its
    /// status is empty). No pipeline object at all yields `""` → `CiStatus.None`.
    let private pipelineStatus (el: JsonElement) : string =
        match el.TryGetProperty "head_pipeline" with
        | true, p when p.ValueKind = JsonValueKind.Object -> Json.strOr p "status"
        | _ ->
            match el.TryGetProperty "pipeline" with
            | true, p when p.ValueKind = JsonValueKind.Object -> Json.strOr p "status"
            | _ -> ""

    /// Parse a `glab mr list` array.
    let parseMrList = Json.parseArray toMr
    /// Parse a single `glab mr view` object.
    let parseMr = Json.parseObject toMr
    /// Parse a `glab repo view` object.
    let parseRepoView = Json.parseObject toRepoView
    /// Parse a `glab issue list` array.
    let parseIssueList = Json.parseArray toIssue
    /// Parse a single `glab issue view` object.
    let parseIssue = Json.parseObject toIssue
    /// Parse a `glab release list` array.
    let parseReleaseList = Json.parseArray toRelease
    /// Parse a single `glab release view` object.
    let parseRelease = Json.parseObject toRelease

    /// Parse the CI/pipeline status out of a `glab mr view <id> --output json` object.
    let parseCiStatus =
        Json.parseObject (fun el -> CiStatus.OfGitLab(pipelineStatus el))
