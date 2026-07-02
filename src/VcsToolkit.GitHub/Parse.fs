namespace VcsToolkit.GitHub

open System.Text.Json
open VcsToolkit.CliSupport

/// A pull request (`gh pr list/view --json number,title,state,headRefName,baseRefName,url`).
type PullRequest =
    {
        /// PR number.
        Number: uint64
        /// PR title.
        Title: string
        /// State, e.g. `"OPEN"`, `"MERGED"`, `"CLOSED"`.
        State: string
        /// Source (head) branch name (empty when gh sends `null`, e.g. a deleted branch).
        HeadRefName: string
        /// Target (base) branch name.
        BaseRefName: string
        /// Web URL.
        Url: string
    }

/// An issue (`gh issue list --json …`; `gh issue view` additionally fills `Body`/`Url`).
type Issue =
    {
        /// Issue number.
        Number: uint64
        /// Issue title.
        Title: string
        /// State, e.g. `"OPEN"`, `"CLOSED"`.
        State: string
        /// Issue body (markdown).
        Body: string
        /// Web URL.
        Url: string
    }

/// A GitHub Actions workflow run (`gh run list/view --json …`).
type WorkflowRun =
    {
        /// The run id (`databaseId`) — the `<run-id>` other `gh run` commands take.
        DatabaseId: uint64
        /// Workflow name as shown in the runs list.
        Name: string
        /// The run's display title (usually the commit subject).
        DisplayTitle: string
        /// Lifecycle status, e.g. `"queued"`, `"in_progress"`, `"completed"`.
        Status: string
        /// Outcome, e.g. `"success"`, `"failure"` — gh reports an empty string until
        /// the run completes (not `null`).
        Conclusion: string
        /// Name of the workflow that produced the run.
        WorkflowName: string
        /// Branch the run was triggered for.
        HeadBranch: string
        /// Triggering event, e.g. `"push"`, `"workflow_dispatch"`.
        Event: string
        /// Web URL.
        Url: string
        /// Creation timestamp (ISO 8601).
        CreatedAt: string
    }

/// gh's coarse categorisation of a `CheckRun`'s state — the field to branch on when
/// deciding whether CI passed. An unrecognised value (or absent field) reads as
/// `Unknown` rather than failing the parse, so the wrapper never breaks on an
/// unmodelled value.
[<RequireQualifiedAccess>]
type CheckBucket =
    /// The check succeeded.
    | Pass
    /// The check failed.
    | Fail
    /// The check is queued or still running.
    | Pending
    /// The check was skipped (e.g. a conditional job that didn't run).
    | Skipping
    /// The check was cancelled.
    | Cancel
    /// A bucket gh reported that this version doesn't model, or an absent field.
    | Unknown

    /// Whether this bucket means the check failed or was cancelled — the states that
    /// should fail an aggregate CI verdict. (For the single-case tests use the
    /// compiler-generated `IsPending` / `IsPass` / `IsUnknown` / `IsSkipping` / `IsCancel`.)
    member this.IsFailing = this = CheckBucket.Fail || this = CheckBucket.Cancel

    /// Whether this bucket means the check completed successfully (alias of the
    /// generated `IsPass`, kept for symmetry with `IsFailing`).
    member this.IsPassing = this = CheckBucket.Pass

/// One check on a PR (`gh pr checks --json …`).
type CheckRun =
    {
        /// Check name.
        Name: string
        /// Raw state, e.g. `"SUCCESS"`, `"FAILURE"`, `"IN_PROGRESS"`.
        State: string
        /// gh's categorisation of `State` — the field to branch on. See `CheckBucket`.
        Bucket: CheckBucket
        /// Workflow the check belongs to (empty for non-Actions checks).
        Workflow: string
        /// Web link to the check's details.
        Link: string
        /// Start timestamp (ISO 8601), empty until started.
        StartedAt: string
        /// Completion timestamp (ISO 8601), empty until completed.
        CompletedAt: string
    }

/// A release (`gh release list/view --json …`).
type Release =
    {
        /// The release's tag.
        TagName: string
        /// Release title (may be empty).
        Name: string
        /// Release notes (markdown); empty from `releaseList`, which doesn't fetch it.
        Body: string
        /// Web URL; empty from `releaseList`, which doesn't fetch it.
        Url: string
        /// Publication timestamp (ISO 8601); empty for a draft.
        PublishedAt: string
        /// `true` for an unpublished draft.
        IsDraft: bool
        /// `true` for a prerelease.
        IsPrerelease: bool
        /// `true` for the latest release. Only `releaseList` reports this; from
        /// `releaseView` it defaults to `false`.
        IsLatest: bool
    }

/// A submitted PR review (from `gh pr view --json reviews`).
type Review =
    {
        /// Reviewer login (empty for a deleted account).
        Author: string
        /// Review state: `"APPROVED"`, `"CHANGES_REQUESTED"`, `"COMMENTED"`,
        /// `"DISMISSED"` or `"PENDING"`.
        State: string
        /// Review body (may be empty).
        Body: string
        /// Submission timestamp (ISO 8601).
        SubmittedAt: string
    }

/// A PR conversation comment (from `gh pr view --json comments`).
type Comment =
    {
        /// Commenter login (empty for a deleted account).
        Author: string
        /// Comment body.
        Body: string
        /// Web URL of the comment.
        Url: string
        /// Creation timestamp (ISO 8601).
        CreatedAt: string
    }

/// The review/comment feedback on a PR (`gh pr view --json reviews,comments`).
type PrFeedback =
    {
        /// Submitted reviews, oldest first (gh's order).
        Reviews: Review list
        /// Conversation comments, oldest first (gh's order).
        Comments: Comment list
    }

/// A repository (`gh repo view --json …`).
type Repo =
    {
        /// Repository name.
        Name: string
        /// Owner login.
        Owner: string
        /// Description, `None` when GitHub returns `null`.
        Description: string option
        /// Web URL.
        Url: string
        /// `true` for a private repository.
        IsPrivate: bool
        /// Default branch name (empty for an empty repository).
        DefaultBranch: string
    }

/// Tolerant JSON parsers over `gh … --json` output, built on `System.Text.Json`.
/// Each parser is total: a malformed document yields `Error`, never an exception;
/// an absent or `null` field reads as empty (`""`/`None`/`false`/`0`), mirroring
/// gh's optional-field shapes rather than demanding every key.
[<RequireQualifiedAccess>]
module GitHubParse =

    // --- element -> record ---------------------------------------------------

    let private toPr (el: JsonElement) : PullRequest =
        { Number = Json.u64Or el "number"
          Title = Json.strOr el "title"
          State = Json.strOr el "state"
          HeadRefName = Json.strOr el "headRefName"
          BaseRefName = Json.strOr el "baseRefName"
          Url = Json.strOr el "url" }

    let private toIssue (el: JsonElement) : Issue =
        { Number = Json.u64Or el "number"
          Title = Json.strOr el "title"
          State = Json.strOr el "state"
          Body = Json.strOr el "body"
          Url = Json.strOr el "url" }

    let private toRun (el: JsonElement) : WorkflowRun =
        { DatabaseId = Json.u64Or el "databaseId"
          Name = Json.strOr el "name"
          DisplayTitle = Json.strOr el "displayTitle"
          Status = Json.strOr el "status"
          Conclusion = Json.strOr el "conclusion"
          WorkflowName = Json.strOr el "workflowName"
          HeadBranch = Json.strOr el "headBranch"
          Event = Json.strOr el "event"
          Url = Json.strOr el "url"
          CreatedAt = Json.strOr el "createdAt" }

    let private toBucket (el: JsonElement) : CheckBucket =
        // gh emits lowercase bucket strings; match them exactly (an unknown value or
        // absent field is the forward-compatible catch-all).
        match el.TryGetProperty "bucket" with
        | true, p when p.ValueKind = JsonValueKind.String ->
            match p.GetString() |> Option.ofObj |> Option.defaultValue "" with
            | "pass" -> CheckBucket.Pass
            | "fail" -> CheckBucket.Fail
            | "pending" -> CheckBucket.Pending
            | "skipping" -> CheckBucket.Skipping
            | "cancel" -> CheckBucket.Cancel
            | _ -> CheckBucket.Unknown
        | _ -> CheckBucket.Unknown

    let private toCheck (el: JsonElement) : CheckRun =
        { Name = Json.strOr el "name"
          State = Json.strOr el "state"
          Bucket = toBucket el
          Workflow = Json.strOr el "workflow"
          Link = Json.strOr el "link"
          StartedAt = Json.strOr el "startedAt"
          CompletedAt = Json.strOr el "completedAt" }

    let private toRelease (el: JsonElement) : Release =
        { TagName = Json.strOr el "tagName"
          Name = Json.strOr el "name"
          Body = Json.strOr el "body"
          Url = Json.strOr el "url"
          PublishedAt = Json.strOr el "publishedAt"
          IsDraft = Json.boolOr el "isDraft"
          IsPrerelease = Json.boolOr el "isPrerelease"
          IsLatest = Json.boolOr el "isLatest" }

    let private toReview (el: JsonElement) : Review =
        { Author = Json.nestedStr el "author" "login"
          State = Json.strOr el "state"
          Body = Json.strOr el "body"
          SubmittedAt = Json.strOr el "submittedAt" }

    let private toComment (el: JsonElement) : Comment =
        { Author = Json.nestedStr el "author" "login"
          Body = Json.strOr el "body"
          Url = Json.strOr el "url"
          CreatedAt = Json.strOr el "createdAt" }

    let private toRepo (el: JsonElement) : Repo =
        { Name = Json.strOr el "name"
          Owner = Json.nestedStr el "owner" "login"
          Description = Json.strOpt el "description"
          Url = Json.strOr el "url"
          IsPrivate = Json.boolOr el "isPrivate"
          DefaultBranch = Json.nestedStr el "defaultBranchRef" "name" }

    let private toFeedback (el: JsonElement) : PrFeedback =
        { Reviews = Json.arrayOf el "reviews" |> List.map toReview
          Comments = Json.arrayOf el "comments" |> List.map toComment }

    // --- public parsers (over the shared total helpers in `Json`) ------------

    /// Parse a `gh pr list` array.
    let parsePrList = Json.parseArray toPr
    /// Parse a single `gh pr view` object.
    let parsePr = Json.parseObject toPr
    /// Parse a `gh issue list` array.
    let parseIssueList = Json.parseArray toIssue
    /// Parse a single `gh issue view` object.
    let parseIssue = Json.parseObject toIssue
    /// Parse a `gh run list` array.
    let parseRunList = Json.parseArray toRun
    /// Parse a single `gh run view` object.
    let parseRun = Json.parseObject toRun
    /// Parse a `gh pr checks` array.
    let parseChecks = Json.parseArray toCheck
    /// Parse a `gh release list` array.
    let parseReleaseList = Json.parseArray toRelease
    /// Parse a single `gh release view` object.
    let parseRelease = Json.parseObject toRelease
    /// Parse a `gh repo view` object, flattening the nested `owner`/`defaultBranchRef`.
    let parseRepo = Json.parseObject toRepo
    /// Parse a `gh pr view --json reviews,comments` object, flattening nested authors.
    let parseFeedback = Json.parseObject toFeedback
