namespace VcsToolkit.GitHub

open System.Text.Json

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

    /// A string property, or `""` when absent / `null` / not a string (gh sends a
    /// present `null` for some optional strings, e.g. a deleted branch's `headRefName`).
    let private strOr (el: JsonElement) (name: string) : string =
        match el.TryGetProperty name with
        | true, p when p.ValueKind = JsonValueKind.String -> p.GetString() |> Option.ofObj |> Option.defaultValue ""
        | _ -> ""

    /// A string property as an option: `Some` only for a present non-null string.
    let private strOpt (el: JsonElement) (name: string) : string option =
        match el.TryGetProperty name with
        | true, p when p.ValueKind = JsonValueKind.String -> p.GetString() |> Option.ofObj
        | _ -> None

    /// A numeric property as `uint64`, or `0` when absent / not a number.
    let private u64Or (el: JsonElement) (name: string) : uint64 =
        match el.TryGetProperty name with
        | true, p when p.ValueKind = JsonValueKind.Number ->
            match p.TryGetUInt64() with
            | true, n -> n
            | _ -> 0UL
        | _ -> 0UL

    /// A boolean property, or `false` when absent / not a boolean.
    let private boolOr (el: JsonElement) (name: string) : bool =
        match el.TryGetProperty name with
        | true, p when p.ValueKind = JsonValueKind.True || p.ValueKind = JsonValueKind.False -> p.GetBoolean()
        | _ -> false

    /// A string `field` read from a nested object property `objName`, or `""` when
    /// the object is absent / `null` (gh nests `owner`/`author`/`defaultBranchRef`).
    let private nestedStr (el: JsonElement) (objName: string) (field: string) : string =
        match el.TryGetProperty objName with
        | true, o when o.ValueKind = JsonValueKind.Object -> strOr o field
        | _ -> ""

    /// The elements of an array property (empty when absent / not an array).
    let private arrayOf (el: JsonElement) (name: string) : JsonElement list =
        match el.TryGetProperty name with
        | true, a when a.ValueKind = JsonValueKind.Array -> [ for x in a.EnumerateArray() -> x ]
        | _ -> []

    // --- element -> record ---------------------------------------------------

    let private toPr (el: JsonElement) : PullRequest =
        { Number = u64Or el "number"
          Title = strOr el "title"
          State = strOr el "state"
          HeadRefName = strOr el "headRefName"
          BaseRefName = strOr el "baseRefName"
          Url = strOr el "url" }

    let private toIssue (el: JsonElement) : Issue =
        { Number = u64Or el "number"
          Title = strOr el "title"
          State = strOr el "state"
          Body = strOr el "body"
          Url = strOr el "url" }

    let private toRun (el: JsonElement) : WorkflowRun =
        { DatabaseId = u64Or el "databaseId"
          Name = strOr el "name"
          DisplayTitle = strOr el "displayTitle"
          Status = strOr el "status"
          Conclusion = strOr el "conclusion"
          WorkflowName = strOr el "workflowName"
          HeadBranch = strOr el "headBranch"
          Event = strOr el "event"
          Url = strOr el "url"
          CreatedAt = strOr el "createdAt" }

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
        { Name = strOr el "name"
          State = strOr el "state"
          Bucket = toBucket el
          Workflow = strOr el "workflow"
          Link = strOr el "link"
          StartedAt = strOr el "startedAt"
          CompletedAt = strOr el "completedAt" }

    let private toRelease (el: JsonElement) : Release =
        { TagName = strOr el "tagName"
          Name = strOr el "name"
          Body = strOr el "body"
          Url = strOr el "url"
          PublishedAt = strOr el "publishedAt"
          IsDraft = boolOr el "isDraft"
          IsPrerelease = boolOr el "isPrerelease"
          IsLatest = boolOr el "isLatest" }

    let private toReview (el: JsonElement) : Review =
        { Author = nestedStr el "author" "login"
          State = strOr el "state"
          Body = strOr el "body"
          SubmittedAt = strOr el "submittedAt" }

    let private toComment (el: JsonElement) : Comment =
        { Author = nestedStr el "author" "login"
          Body = strOr el "body"
          Url = strOr el "url"
          CreatedAt = strOr el "createdAt" }

    let private toRepo (el: JsonElement) : Repo =
        { Name = strOr el "name"
          Owner = nestedStr el "owner" "login"
          Description = strOpt el "description"
          Url = strOr el "url"
          IsPrivate = boolOr el "isPrivate"
          DefaultBranch = nestedStr el "defaultBranchRef" "name" }

    let private toFeedback (el: JsonElement) : PrFeedback =
        { Reviews = arrayOf el "reviews" |> List.map toReview
          Comments = arrayOf el "comments" |> List.map toComment }

    // --- public parsers ------------------------------------------------------

    /// Run `parse` over the parsed document's root, mapping a malformed document to
    /// an `Error` — never an exception. Two exception families are expected here and
    /// both are the parser saying "this isn't the shape I model", so both become
    /// `Error`: `JsonException` (syntactically malformed JSON, or a `null` input
    /// string via `ArgumentNullException`, a subtype) and `InvalidOperationException`
    /// (a field reader hit a `JsonElement` of the wrong kind — e.g. `TryGetProperty`
    /// on a non-object). This keeps the parsers total, the contract the callers rely on.
    let private withDoc (json: string) (parse: JsonElement -> Result<'T, string>) : Result<'T, string> =
        try
            use doc = JsonDocument.Parse json
            parse doc.RootElement
        with
        | :? JsonException as ex ->
            // Malformed JSON is the expected failure here; surface its message as a
            // parse error rather than letting the exception escape the pure boundary.
            Error ex.Message
        | :? System.ArgumentNullException ->
            // A null input string — `gh` never emits one, but a caller could pass it;
            // report it as a parse failure rather than throwing.
            Error "no JSON to parse (null input)"
        | :? System.InvalidOperationException as ex ->
            // A wrong-kind `JsonElement` reached a field reader (e.g. a non-object
            // array element in `reviews`/`comments`). Rust's serde rejects the same
            // shapes; we mirror that with an `Error` instead of a crash.
            Error ex.Message

    let private parseArray (toItem: JsonElement -> 'T) (json: string) : Result<'T list, string> =
        withDoc json (fun root ->
            if root.ValueKind <> JsonValueKind.Array then
                Error "expected a JSON array"
            elif
                root.EnumerateArray()
                |> Seq.forall (fun e -> e.ValueKind = JsonValueKind.Object)
            then
                Ok [ for item in root.EnumerateArray() -> toItem item ]
            else
                // A non-object element (`[1,2,3]`, `[null]`) can't populate a record —
                // serde fails the whole parse on this shape, so we do too.
                Error "expected a JSON array of objects")

    let private parseObject (toItem: JsonElement -> 'T) (json: string) : Result<'T, string> =
        withDoc json (fun root ->
            if root.ValueKind = JsonValueKind.Object then
                Ok(toItem root)
            else
                Error "expected a JSON object")

    /// Parse a `gh pr list` array.
    let parsePrList = parseArray toPr
    /// Parse a single `gh pr view` object.
    let parsePr = parseObject toPr
    /// Parse a `gh issue list` array.
    let parseIssueList = parseArray toIssue
    /// Parse a single `gh issue view` object.
    let parseIssue = parseObject toIssue
    /// Parse a `gh run list` array.
    let parseRunList = parseArray toRun
    /// Parse a single `gh run view` object.
    let parseRun = parseObject toRun
    /// Parse a `gh pr checks` array.
    let parseChecks = parseArray toCheck
    /// Parse a `gh release list` array.
    let parseReleaseList = parseArray toRelease
    /// Parse a single `gh release view` object.
    let parseRelease = parseObject toRelease
    /// Parse a `gh repo view` object, flattening the nested `owner`/`defaultBranchRef`.
    let parseRepo = parseObject toRepo
    /// Parse a `gh pr view --json reviews,comments` object, flattening nested authors.
    let parseFeedback = parseObject toFeedback
