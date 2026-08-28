namespace VcsToolkit.Agent.Tests

open System
open System.IO
open System.Reflection
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Reflection
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing
open VcsToolkit.Agent
open VcsToolkit.Core
open VcsToolkit.Forge
open VcsToolkit.Git
open VcsToolkit.Jj
open VcsToolkit.TestKit

module private Golden =
    let private projectDir =
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        |> Seq.tryPick (fun attribute ->
            if attribute.Key = "AgentTestsProjectDir" then
                Option.ofObj attribute.Value
            else
                None)
        |> Option.defaultWith (fun () -> failwith "AgentTestsProjectDir assembly metadata is missing")

    let read name =
        File.ReadAllText(Path.Combine(projectDir, "Golden", name)).Replace("\r\n", "\n")

module private CommitTest =
    type IsolatedJjRunner(inner: IProcessRunner, repositoryRoot: string) =
        let configRoot = Path.Combine(repositoryRoot, ".jj", ".vcs-toolkit-jj-config")

        let isolate (command: Command) =
            command
            |> Command.env "JJ_CONFIG" (Path.Combine(configRoot, "vcs-agent-nonexistent-config.toml"))
            |> Command.env "APPDATA" configRoot
            |> Command.env "LOCALAPPDATA" configRoot
            |> Command.env "XDG_CONFIG_HOME" configRoot
            |> Command.env "JJ_USER" "test"
            |> Command.env "JJ_EMAIL" "test@example.com"

        interface IProcessRunner with
            member _.CaptureStringAsync(command, cancellationToken) =
                inner.CaptureStringAsync(isolate command, cancellationToken)

            member _.CaptureBytesAsync(command, cancellationToken) =
                inner.CaptureBytesAsync(isolate command, cancellationToken)

            member _.SpawnAsync(command, cancellationToken) =
                inner.SpawnAsync(isolate command, cancellationToken)

    type FailAfterSuccessfulCommitRunner(inner: IProcessRunner) =
        let mutable committed = false
        let mutable failedPostflightRead = false

        interface IProcessRunner with
            member _.CaptureStringAsync(command, cancellationToken) =
                task {
                    if committed && not failedPostflightRead then
                        failedPostflightRead <- true
                        return Error(ProcessError.Spawn(command.Program, "injected post-commit read failure"))
                    else
                        let! result = inner.CaptureStringAsync(command, cancellationToken)

                        if command.Arguments |> Seq.contains "commit" then
                            match result with
                            | Ok _ -> committed <- true
                            | Error _ -> ()

                        return result
                }

            member _.CaptureBytesAsync(command, cancellationToken) =
                inner.CaptureBytesAsync(command, cancellationToken)

            member _.SpawnAsync(command, cancellationToken) =
                inner.SpawnAsync(command, cancellationToken)

    type WrongRevisionRunner(inner: IProcessRunner, repositoryRoot: string) =
        interface IProcessRunner with
            member _.CaptureStringAsync(command, cancellationToken) =
                task {
                    let! result = inner.CaptureStringAsync(command, cancellationToken)

                    if command.Arguments |> Seq.contains "commit" then
                        match result with
                        | Error _ -> ()
                        | Ok _ ->
                            let selected = File.ReadAllText(Path.Combine(repositoryRoot, "selected.txt"))
                            let different = File.ReadAllText(Path.Combine(repositoryRoot, "different.txt"))
                            Raw.git repositoryRoot [ "reset"; "--hard"; "HEAD~" ]
                            File.WriteAllText(Path.Combine(repositoryRoot, "selected.txt"), selected)
                            File.WriteAllText(Path.Combine(repositoryRoot, "different.txt"), different)
                            Raw.git repositoryRoot [ "commit"; "-qm"; "wrong path"; "--only"; "--"; "different.txt" ]

                    return result
                }

            member _.CaptureBytesAsync(command, cancellationToken) =
                inner.CaptureBytesAsync(command, cancellationToken)

            member _.SpawnAsync(command, cancellationToken) =
                inner.SpawnAsync(command, cancellationToken)

    type AdditionalCommitRunner(inner: IProcessRunner, repositoryRoot: string) =
        interface IProcessRunner with
            member _.CaptureStringAsync(command, cancellationToken) =
                task {
                    let! result = inner.CaptureStringAsync(command, cancellationToken)

                    if command.Arguments |> Seq.contains "commit" then
                        match result with
                        | Error _ -> ()
                        | Ok _ ->
                            File.AppendAllText(Path.Combine(repositoryRoot, "selected.txt"), "concurrent\n")

                            Raw.git
                                repositoryRoot
                                [ "commit"
                                  "-qm"
                                  "concurrent selected update"
                                  "--only"
                                  "--"
                                  "selected.txt" ]

                    return result
                }

            member _.CaptureBytesAsync(command, cancellationToken) =
                inner.CaptureBytesAsync(command, cancellationToken)

            member _.SpawnAsync(command, cancellationToken) =
                inner.SpawnAsync(command, cancellationToken)

    type FailingCommitRunner(inner: IProcessRunner, error: ProcessError) =
        interface IProcessRunner with
            member _.CaptureStringAsync(command, cancellationToken) =
                if command.Arguments |> Seq.contains "commit" then
                    Task.FromResult(Error error)
                else
                    inner.CaptureStringAsync(command, cancellationToken)

            member _.CaptureBytesAsync(command, cancellationToken) =
                inner.CaptureBytesAsync(command, cancellationToken)

            member _.SpawnAsync(command, cancellationToken) =
                inner.SpawnAsync(command, cancellationToken)

    let requireGit () =
        try
            Raw.git "." [ "--version" ]
        with _ ->
            // This live-backend test is optional on developer machines without Git.
            Assert.Ignore "git not available on PATH"

    let requireJj () =
        try
            Raw.jj "." [ "--version" ]
        with _ ->
            // Local runs may omit jj, but CI's REQUIRE_JJ gate must fail closed.
            if Environment.GetEnvironmentVariable "REQUIRE_JJ" = "1" then
                Assert.Fail "REQUIRE_JJ=1 but jj not available on PATH"
            else
                Assert.Ignore "jj not available on PATH"

    let expectCommit envelope =
        match envelope.Data with
        | Some(AgentPayload.Commit data) -> data
        | _ ->
            let detail =
                envelope.Error
                |> Option.map _.Message
                |> Option.defaultValue "no structured error"

            Assert.Fail $"commit payload expected: {detail}"
            Unchecked.defaultof<CommitData>

    let isolatedJjRunner repositoryRoot =
        IsolatedJjRunner(JobRunner() :> IProcessRunner, repositoryRoot) :> IProcessRunner

[<TestFixture>]
type ContractTests() =

    [<Test>]
    member _.``Probe envelope matches the committed v1 golden``() =
        let actual = Agent.probe "0.1.0-test" |> AgentWire.serialize
        let expected = Golden.read "probe.v1.json"
        Assert.That(actual, Is.EqualTo expected)

    [<Test>]
    member _.``Unsupported envelope matches the committed v1 golden``() =
        let actual = Agent.unsupported AgentOperation.Inspect |> AgentWire.serialize
        let expected = Golden.read "unsupported.v1.json"
        Assert.That(actual, Is.EqualTo expected)

    [<Test>]
    member _.``Inspect envelope matches the committed v1 golden``() =
        let capabilities =
            { PullRequestCreate = true
              PullRequestComment = true
              PullRequestEdit = true
              PullRequestChecks = true
              PullRequestMerge = true
              IssueCreate = true
              IssueReopen = true
              ReleaseDelete = true }

        let data: InspectData =
            { Root = "/repo"
              Backend = "git"
              Identity =
                { Revision = Some "abc123"
                  Branch = Some "main" }
              WorkingState =
                { Dirty = true
                  ChangeCount = 1UL
                  Conflicted = false
                  Operation = "clear"
                  Tracking =
                    Some
                        { Branch = "origin/main"
                          Ahead = Some 1UL
                          Behind = Some 0UL } }
              Remotes =
                [ { Name = "origin"
                    Url = "https://github.com/example/repo.git" } ]
              Forge =
                { Status = AgentForgeStatus.Available
                  Kind = Some "github"
                  Authenticated = true
                  Version = Some "2.40.0"
                  Capabilities = capabilities }
              Operations =
                [ { Operation = AgentOperation.Inspect
                    Supported = true
                    Mutating = false } ] }

        let envelope: AgentEnvelope =
            { ContractVersion = Agent.ContractVersion
              Operation = "inspect"
              Status = AgentStatus.Success
              Terminal = true
              Data = Some(AgentPayload.Inspect data)
              Error = None
              Warnings = []
              FallbackReason = None }

        Assert.That(AgentWire.serialize envelope, Is.EqualTo(Golden.read "inspect.v1.json"))

    [<Test>]
    member _.``Changes summary envelope matches the committed v1 golden``() =
        let data =
            ChangesData.Summary
                { Paths =
                    [ { Path = "src/App.fs"
                        OldPath = None
                        Change = "modified" } ]
                  DiffStat =
                    { FilesChanged = 1UL
                      Insertions = 2UL
                      Deletions = 1UL } }

        let envelope: AgentEnvelope =
            { ContractVersion = Agent.ContractVersion
              Operation = "changes"
              Status = AgentStatus.Success
              Terminal = true
              Data = Some(AgentPayload.Changes data)
              Error = None
              Warnings = []
              FallbackReason = None }

        Assert.That(AgentWire.serialize envelope, Is.EqualTo(Golden.read "changes-summary.v1.json"))

    [<Test>]
    member _.``Commit envelope matches the committed v1 golden``() =
        let envelope: AgentEnvelope =
            { ContractVersion = Agent.ContractVersion
              Operation = "commit"
              Status = AgentStatus.Success
              Terminal = true
              Data =
                Some(
                    AgentPayload.Commit
                        { Root = "/repo"
                          Backend = "git"
                          SourceRevision = Some "abc123"
                          SourceBranch = Some "main"
                          RequestedPaths = [ "src/App.fs"; "tests/App.Tests.fs" ]
                          BackendPaths = [ "src/App.fs"; "tests/App.Tests.fs" ]
                          ObservedRevision = Some "def456"
                          ObservedBranch = Some "main"
                          ObservedCreatedRevision = Some "def456"
                          CreatedRevision = Some "def456"
                          Paths = [ "src/App.fs"; "tests/App.Tests.fs" ]
                          SelectedPathsRemaining = Some false
                          UnrelatedPathsPreserved = Some true
                          Completion = CommitCompletion.Verified }
                )
              Error = None
              Warnings =
                [ { Code = "unrelated-changes-preserved"
                    Message = "1 unrelated changed path(s) remain in the working copy." } ]
              FallbackReason = None }

        Assert.That(AgentWire.serialize envelope, Is.EqualTo(Golden.read "commit.v1.json"))

    [<Test>]
    member _.``Changes data has exactly one payload in each public union case``() =
        let cases = FSharpType.GetUnionCases typeof<ChangesData>

        Assert.That((cases |> Array.map _.Name) = [| "Summary"; "StructuredDiff" |], Is.True)

        Assert.That((cases.[0].GetFields() |> Array.map _.PropertyType) = [| typeof<AgentChangeSummary> |], Is.True)

        Assert.That((cases.[1].GetFields() |> Array.map _.PropertyType) = [| typeof<AgentFileDiff list> |], Is.True)

    [<Test>]
    member _.``Every v1 error has a stable distinct exit code``() =
        let mappings =
            [ AgentErrorCode.Unsupported, 20
              AgentErrorCode.Denied, 21
              AgentErrorCode.InvalidInput, 22
              AgentErrorCode.Backend, 23
              AgentErrorCode.Forge, 24
              AgentErrorCode.Authentication, 25
              AgentErrorCode.Timeout, 26
              AgentErrorCode.Cancellation, 27
              AgentErrorCode.OutputLimit, 28
              AgentErrorCode.ExternalCommand, 29 ]

        let actual = mappings |> List.map (fun (code, _) -> Agent.exitCode code)
        let expected = mappings |> List.map snd
        Assert.That((actual = expected), Is.True)
        Assert.That((actual |> Set.ofList |> Set.count), Is.EqualTo actual.Length)

    [<Test>]
    member _.``Errors redact credentials before serialization``() =
        let secret = "super-secret-value"

        let output =
            Agent.invalidInput
                "command"
                $"https://user:{secret}@example.test/repo token={secret} Authorization: Bearer {secret}"
            |> AgentWire.render Agent.DefaultOutputLimitBytes

        Assert.That(output.ExitCode, Is.EqualTo 22)
        Assert.That(output.Stdout, Does.Not.Contain secret)
        Assert.That(output.Stdout, Does.Contain "[REDACTED]")

        use document = JsonDocument.Parse output.Stdout

        Assert.That(
            document.RootElement.GetProperty("error").GetProperty("code").GetString(),
            Is.EqualTo "invalid-input"
        )

    [<Test>]
    member _.``Public envelopes cannot bypass final-boundary redaction``() =
        let urlSecret = "url-secret-value"
        let bearerSecret = "bearer-secret-value"
        let namedSecret = "named-secret-value"

        let message =
            $"https://user:{urlSecret}@example.test/repo Bearer {bearerSecret} api_key={namedSecret}"

        let envelope: AgentEnvelope =
            { ContractVersion = Agent.ContractVersion
              Operation = "consumer.operation"
              Status = AgentStatus.Error
              Terminal = true
              Data =
                Some(
                    AgentPayload.Commit
                        { Root = $"https://user:{urlSecret}@example.test/root"
                          Backend = "git"
                          SourceRevision = Some $"Bearer {bearerSecret}"
                          SourceBranch = Some $"api_key={namedSecret}"
                          RequestedPaths = [ $"api_key={namedSecret}" ]
                          BackendPaths = [ $"api_key={namedSecret}" ]
                          ObservedRevision = Some $"Bearer {bearerSecret}"
                          ObservedBranch = Some $"api_key={namedSecret}"
                          ObservedCreatedRevision = Some $"https://user:{urlSecret}@example.test/revision"
                          CreatedRevision = Some $"https://user:{urlSecret}@example.test/revision"
                          Paths = [ $"api_key={namedSecret}" ]
                          SelectedPathsRemaining = Some false
                          UnrelatedPathsPreserved = Some true
                          Completion = CommitCompletion.Verified }
                )
              Error =
                Some
                    { Code = AgentErrorCode.ExternalCommand
                      Message = message
                      Retryable = false
                      Truncated = false
                      LimitBytes = None
                      RequiredBytes = None }
              Warnings =
                [ { Code = $"https://user:{urlSecret}@example.test/warning"
                    Message = "credentialed URL warning" }
                  { Code = $"Bearer {bearerSecret}"
                    Message = "bearer warning" }
                  { Code = $"api_key={namedSecret}"
                    Message = message } ]
              FallbackReason = None }

        let serialized = AgentWire.serialize envelope
        let rendered = AgentWire.render Agent.DefaultOutputLimitBytes envelope

        for output in [ serialized; rendered.Stdout ] do
            Assert.That(output, Does.Not.Contain urlSecret)
            Assert.That(output, Does.Not.Contain bearerSecret)
            Assert.That(output, Does.Not.Contain namedSecret)
            Assert.That(output, Does.Contain "[REDACTED]")

        Assert.That(rendered.ExitCode, Is.EqualTo 29)

    [<Test>]
    member _.``Oversized output becomes an explicit bounded output-limit envelope``() =
        let limit = Agent.MinimumOutputLimitBytes
        let output = Agent.probe "0.1.0-test" |> AgentWire.render limit

        Assert.That(output.ExitCode, Is.EqualTo 28)
        Assert.That(Encoding.UTF8.GetByteCount output.Stdout, Is.LessThanOrEqualTo limit)

        use document = JsonDocument.Parse output.Stdout
        let error = document.RootElement.GetProperty "error"
        Assert.That(error.GetProperty("code").GetString(), Is.EqualTo "output-limit")
        Assert.That(error.GetProperty("truncated").GetBoolean(), Is.True)
        Assert.That(error.GetProperty("limitBytes").GetInt32(), Is.EqualTo limit)
        Assert.That(error.GetProperty("requiredBytes").GetInt32(), Is.GreaterThan limit)

    [<Test>]
    member _.``Probe is byte deterministic``() =
        let first = Agent.probe "0.1.0-test" |> AgentWire.serialize
        let second = Agent.probe "0.1.0-test" |> AgentWire.serialize
        Assert.That((Encoding.UTF8.GetBytes first = Encoding.UTF8.GetBytes second), Is.True)

[<TestFixture>]
type ReadOnlyOutcomeTests() =

    let createTempRoot tag =
        let root = Path.Combine(Path.GetTempPath(), $"vcs-agent-{tag}-{Guid.NewGuid():N}")

        Directory.CreateDirectory root |> ignore
        root

    let gitSnapshot root remote =
        let nul = string '\000'
        let gitDir = Path.Combine(root, ".git")
        Directory.CreateDirectory gitDir |> ignore

        let status =
            [ "# branch.oid abc123"
              "# branch.head main"
              "# branch.upstream origin/main"
              "# branch.ab +1 -2"
              "1 .M N... 100644 100644 100644 1 2 src/App.fs" ]
            |> List.map (fun line -> line + nul)
            |> String.concat ""

        let runner =
            ScriptedRunner()
                .On([ "status"; "--porcelain=v2"; "--branch"; "-z" ], Reply.Ok status)
                .On([ "rev-parse"; "--git-dir" ], Reply.Ok(gitDir + "\n"))
                .On([ "remote"; "-v" ], Reply.Ok($"origin\t{remote} (fetch)\norigin\t{remote} (push)\n"))

        Repo.FromGit(root, root, Git.WithRunner runner)

    let jjSnapshot root =
        let runner =
            ScriptedRunner()
                .On([ "-T"; "commit_id" ], Reply.Ok "jjcommit123\n")
                .On([ "log"; "-r"; "@"; "--limit"; "1" ], Reply.Ok "jjcommit123\t0\t0\n")
                .On([ "log"; "heads(::@ & bookmarks())" ], Reply.Ok "main\tparent123\n")
                .On([ "log"; "-r"; "::@" ], Reply.Ok "jjcommit123\tparent123\nparent123\t\n")
                .On([ "root" ], Reply.Ok(root + "\n"))
                .On([ "diff"; "-r"; "@"; "--summary" ], Reply.Ok "M src/App.fs\n")
                .On([ "git"; "remote"; "list"; "--ignore-working-copy" ], Reply.Ok "")

        Repo.FromJj(root, root, Jj.WithRunner runner)

    [<Test>]
    member _.``Git inspect composes snapshot remotes and authenticated forge with redaction``() : Task =
        task {
            let root = createTempRoot "git-inspect"
            let secret = "remote-secret"

            try
                let repo = gitSnapshot root $"https://user:{secret}@github.com/example/repo.git"

                let forgeRunner =
                    ScriptedRunner()
                        .On([ "--version" ], Reply.Ok "gh version 2.40.0\n")
                        .On([ "auth"; "status" ], Reply.Exit 0)

                let forge = Forge.FromGitHub(root, VcsToolkit.GitHub.GitHub.WithRunner forgeRunner)

                let! envelope = Agent.inspectWith repo (Some forge) CancellationToken.None Agent.DefaultOutputLimitBytes

                Assert.That(envelope.Status, Is.EqualTo AgentStatus.Success)

                match envelope.Data with
                | Some(AgentPayload.Inspect data) ->
                    Assert.That(data.Backend, Is.EqualTo "git")
                    Assert.That(data.Identity.Revision, Is.EqualTo(Some "abc123"))
                    Assert.That(data.WorkingState.Dirty, Is.True)
                    Assert.That(data.Forge.Status, Is.EqualTo AgentForgeStatus.Available)
                    Assert.That(data.Forge.Kind, Is.EqualTo(Some "github"))
                    Assert.That(data.Forge.Authenticated, Is.True)
                    Assert.That(data.Forge.Capabilities.PullRequestChecks, Is.True)
                    Assert.That(data.Remotes.Head.Url, Does.Not.Contain secret)
                | _ -> Assert.Fail "inspect payload expected"

                let serialized = AgentWire.serialize envelope
                Assert.That(serialized, Does.Not.Contain secret)
                Assert.That(serialized, Does.Contain "[REDACTED]")
            finally
                Directory.Delete(root, true)
        }

    [<Test>]
    member _.``Invalid exact paths and cancellation never invoke a backend``() : Task =
        task {
            let nullPaths: string list = Unchecked.defaultof<_>

            let invalidPathSets =
                [ []
                  nullPaths
                  [ "../outside.fs" ]
                  [ "/rooted.fs" ]
                  [ "C:/rooted.fs" ]
                  [ "a\\b.fs" ]
                  [ "a//b.fs" ]
                  [ "a.fs"; "a.fs" ] ]

            for paths in invalidPathSets do
                let runner = ScriptedRunner()
                let repo = Repo.FromGit(".", ".", Git.WithRunner runner)

                let! envelope =
                    Agent.commitWith repo paths "message" CancellationToken.None Agent.DefaultOutputLimitBytes

                Assert.That(envelope.Error.Value.Code, Is.EqualTo AgentErrorCode.InvalidInput)
                Assert.That(runner.Received, Is.Empty)

            for message in [ " "; string '\000' ] do
                let runner = ScriptedRunner()
                let repo = Repo.FromGit(".", ".", Git.WithRunner runner)

                let! envelope =
                    Agent.commitWith repo [ "src/App.fs" ] message CancellationToken.None Agent.DefaultOutputLimitBytes

                Assert.That(envelope.Error.Value.Code, Is.EqualTo AgentErrorCode.InvalidInput)
                Assert.That(runner.Received, Is.Empty)

            let emptyRepositoryRequest = CommitRequest.Create(" ", [ "src/App.fs" ], "message")

            let! emptyRepository = Agent.commit emptyRepositoryRequest CancellationToken.None
            Assert.That(emptyRepository.Error.Value.Code, Is.EqualTo AgentErrorCode.InvalidInput)

            use cts = new CancellationTokenSource()
            cts.Cancel()
            let cancelledRunner = ScriptedRunner()
            let cancelledRepo = Repo.FromGit(".", ".", Git.WithRunner cancelledRunner)

            let! cancelled =
                Agent.commitWith cancelledRepo [ "src/App.fs" ] "message" cts.Token Agent.DefaultOutputLimitBytes

            Assert.That(cancelled.Error.Value.Code, Is.EqualTo AgentErrorCode.Cancellation)
            Assert.That(cancelledRunner.Received, Is.Empty)
        }

    [<Test>]
    member _.``Backend timeout is a typed terminal commit result``() : Task =
        task {
            let root =
                Path.Combine(Path.GetTempPath(), $"vcs-agent-commit-timeout-{Guid.NewGuid():N}")

            let gitDir = Path.Combine(root, ".git")
            Directory.CreateDirectory gitDir |> ignore
            let nul = string '\000'

            let snapshot =
                [ "# branch.oid abc123"
                  "# branch.head main"
                  "1 .M N... 100644 100644 100644 1 2 src/App.fs" ]
                |> List.map (fun line -> line + nul)
                |> String.concat ""

            let runner =
                ScriptedRunner()
                    .On([ "status"; "--porcelain=v2"; "--branch"; "-z" ], Reply.Ok snapshot)
                    .On([ "rev-parse"; "--git-dir" ], Reply.Ok(gitDir + "\n"))
                    .On([ "status"; "--porcelain=v1"; "-z" ], Reply.Ok($" M src/App.fs{nul}"))
                    .On([ "commit" ], Reply.Error(ProcessError.Timeout("git", TimeSpan.FromSeconds 1.0, "", "")))

            try
                let repo = Repo.FromGit(root, root, Git.WithRunner runner)

                let! envelope =
                    Agent.commitWith
                        repo
                        [ "src/App.fs" ]
                        "message"
                        CancellationToken.None
                        Agent.DefaultOutputLimitBytes

                Assert.That(envelope.Status, Is.EqualTo AgentStatus.Error)
                Assert.That(envelope.Error.Value.Code, Is.EqualTo AgentErrorCode.Timeout)
                Assert.That(envelope.Terminal, Is.True)
                let data = CommitTest.expectCommit envelope
                Assert.That(data.SourceRevision, Is.EqualTo(Some "abc123"))
                Assert.That(data.SourceBranch, Is.EqualTo(Some "main"))
                Assert.That(data.ObservedCreatedRevision.IsNone, Is.True)
                Assert.That(data.CreatedRevision.IsNone, Is.True)
                Assert.That(data.Paths, Is.Empty)
                Assert.That(data.Completion, Is.EqualTo CommitCompletion.Ambiguous)

                Assert.That(
                    runner.CountReceived(fun invocation -> invocation.Args |> Seq.contains "commit"),
                    Is.EqualTo 1
                )
            finally
                Directory.Delete(root, true)
        }

    [<Test>]
    member _.``Backend cancellation is a typed terminal commit result``() : Task =
        task {
            let root =
                Path.Combine(Path.GetTempPath(), $"vcs-agent-commit-cancel-{Guid.NewGuid():N}")

            let gitDir = Path.Combine(root, ".git")
            Directory.CreateDirectory gitDir |> ignore
            let nul = string '\000'

            let snapshot =
                [ "# branch.oid abc123"
                  "# branch.head main"
                  "1 .M N... 100644 100644 100644 1 2 src/App.fs" ]
                |> List.map (fun line -> line + nul)
                |> String.concat ""

            let runner =
                ScriptedRunner()
                    .On([ "status"; "--porcelain=v2"; "--branch"; "-z" ], Reply.Ok snapshot)
                    .On([ "rev-parse"; "--git-dir" ], Reply.Ok(gitDir + "\n"))
                    .On([ "status"; "--porcelain=v1"; "-z" ], Reply.Ok($" M src/App.fs{nul}"))
                    .On([ "commit" ], Reply.Error(ProcessError.Cancelled "git"))

            try
                let repo = Repo.FromGit(root, root, Git.WithRunner runner)

                let! envelope =
                    Agent.commitWith
                        repo
                        [ "src/App.fs" ]
                        "message"
                        CancellationToken.None
                        Agent.DefaultOutputLimitBytes

                Assert.That(envelope.Status, Is.EqualTo AgentStatus.Error)
                Assert.That(envelope.Error.Value.Code, Is.EqualTo AgentErrorCode.Cancellation)
                Assert.That(envelope.Terminal, Is.True)
                let data = CommitTest.expectCommit envelope
                Assert.That(data.SourceRevision, Is.EqualTo(Some "abc123"))
                Assert.That(data.ObservedCreatedRevision.IsNone, Is.True)
                Assert.That(data.CreatedRevision.IsNone, Is.True)
                Assert.That(data.Paths, Is.Empty)
                Assert.That(data.Completion, Is.EqualTo CommitCompletion.Ambiguous)
            finally
                Directory.Delete(root, true)
        }

    [<Test>]
    member _.``Commit refuses an oversized prospective success before mutation``() : Task =
        task {
            let root =
                Path.Combine(Path.GetTempPath(), $"vcs-agent-commit-budget-{Guid.NewGuid():N}")

            let gitDir = Path.Combine(root, ".git")
            Directory.CreateDirectory gitDir |> ignore
            let nul = string '\000'
            let path = "src/" + String.replicate 350 "x" + ".fs"

            let snapshot =
                [ "# branch.oid abc123"
                  "# branch.head main"
                  $"1 .M N... 100644 100644 100644 1 2 {path}" ]
                |> List.map (fun line -> line + nul)
                |> String.concat ""

            let runner =
                ScriptedRunner()
                    .On([ "status"; "--porcelain=v2"; "--branch"; "-z" ], Reply.Ok snapshot)
                    .On([ "rev-parse"; "--git-dir" ], Reply.Ok(gitDir + "\n"))
                    .On([ "status"; "--porcelain=v1"; "-z" ], Reply.Ok($" M {path}{nul}"))
                    .On([ "commit" ], Reply.Exit 0)

            try
                let repo = Repo.FromGit(root, root, Git.WithRunner runner)

                let! envelope =
                    Agent.commitWith repo [ path ] "message" CancellationToken.None Agent.MinimumOutputLimitBytes

                Assert.That(envelope.Error.Value.Code, Is.EqualTo AgentErrorCode.OutputLimit)

                Assert.That(
                    runner.CountReceived(fun invocation -> invocation.Args |> Seq.contains "commit"),
                    Is.EqualTo 0
                )
            finally
                Directory.Delete(root, true)
        }

    [<Test>]
    member _.``Git commit preserves unrelated dirt and a retry cannot capture it``() : Task =
        task {
            CommitTest.requireGit ()
            use sandbox = GitSandbox.Init "agent-commit-git"
            sandbox.CommitFile("selected-a.txt", "before a\n", "base a")
            sandbox.CommitFile("selected-b.txt", "before b\n", "base b")
            sandbox.CommitFile("tracked-unrelated.txt", "before unrelated\n", "base unrelated")
            let source = sandbox.RevParse "HEAD"
            sandbox.Write("selected-a.txt", "selected a\n")
            sandbox.Write("selected-b.txt", "selected b\n")
            sandbox.Write("tracked-unrelated.txt", "dirty tracked\n")
            sandbox.Write("untracked-unrelated.txt", "dirty untracked\n")

            let request =
                CommitRequest.Create(sandbox.Path, [ "selected-a.txt"; "selected-b.txt" ], "selected only")

            let! envelope = Agent.commit request CancellationToken.None
            let data = CommitTest.expectCommit envelope

            Assert.That(data.Backend, Is.EqualTo "git")
            Assert.That(data.Root, Is.EqualTo(Path.GetFullPath sandbox.Path))
            Assert.That(data.SourceRevision, Is.EqualTo(Some source))
            Assert.That(data.SourceBranch, Is.EqualTo(Some "main"))
            Assert.That(data.CreatedRevision, Is.EqualTo(Some(sandbox.RevParse "HEAD")))
            Assert.That((data.Paths = [ "selected-a.txt"; "selected-b.txt" ]), Is.True)
            Assert.That(data.Completion, Is.EqualTo CommitCompletion.Verified)
            Assert.That(envelope.Warnings |> List.map _.Code, Does.Contain "unrelated-changes-preserved")

            Assert.That(
                File.ReadAllText(Path.Combine(sandbox.Path, "tracked-unrelated.txt")),
                Is.EqualTo "dirty tracked\n"
            )

            Assert.That(
                File.ReadAllText(Path.Combine(sandbox.Path, "untracked-unrelated.txt")),
                Is.EqualTo "dirty untracked\n"
            )

            let! opened =
                match Repo.Open sandbox.Path with
                | Ok repo -> task { return repo }
                | Error error -> failwith error.Message

            let! remaining = opened.ChangedFiles()

            match remaining with
            | Error error -> Assert.Fail error.Message
            | Ok changes ->
                let remainingPaths = changes |> List.map _.Path |> List.sort

                Assert.That((remainingPaths = [ "tracked-unrelated.txt"; "untracked-unrelated.txt" ]), Is.True)

            let createdRevision = data.CreatedRevision.Value

            for path in data.Paths do
                match! opened.LogPaths(createdRevision, 1, [ path ]) with
                | Error error -> Assert.Fail error.Message
                | Ok(commit :: _) -> Assert.That(commit.Id, Is.EqualTo createdRevision)
                | Ok [] -> Assert.Fail $"created Git revision did not include {path}"

            match! opened.LogPaths(createdRevision, 1, [ "tracked-unrelated.txt" ]) with
            | Error error -> Assert.Fail error.Message
            | Ok(commit :: _) -> Assert.That(commit.Id, Is.Not.EqualTo createdRevision)
            | Ok [] -> ()

            let beforeRetry = sandbox.RevParse "HEAD"
            let! retry = Agent.commit request CancellationToken.None
            Assert.That(retry.Error.Value.Code, Is.EqualTo AgentErrorCode.InvalidInput)
            Assert.That(sandbox.RevParse "HEAD", Is.EqualTo beforeRetry)

            Assert.That(
                File.ReadAllText(Path.Combine(sandbox.Path, "tracked-unrelated.txt")),
                Is.EqualTo "dirty tracked\n"
            )
        }

    [<Test>]
    member _.``Git replay after a post-commit failure cannot capture unrelated dirt``() : Task =
        task {
            CommitTest.requireGit ()
            use sandbox = GitSandbox.Init "agent-commit-git-recovery"
            sandbox.CommitFile("selected.txt", "before\n", "base")
            sandbox.Write("selected.txt", "selected\n")
            sandbox.Write("unrelated.txt", "unrelated\n")
            let before = sandbox.RevParse "HEAD"

            let failingRunner =
                CommitTest.FailAfterSuccessfulCommitRunner(JobRunner() :> IProcessRunner)

            let repo = Repo.FromGit(sandbox.Path, sandbox.Path, Git.WithRunner failingRunner)

            let! first =
                Agent.commitWith
                    repo
                    [ "selected.txt" ]
                    "selected only"
                    CancellationToken.None
                    Agent.DefaultOutputLimitBytes

            Assert.That(first.Error.Value.Code, Is.EqualTo AgentErrorCode.Backend)
            let evidence = CommitTest.expectCommit first
            Assert.That(evidence.Completion, Is.EqualTo CommitCompletion.Ambiguous)
            Assert.That(evidence.SourceRevision, Is.EqualTo(Some before))
            Assert.That(evidence.SourceBranch, Is.EqualTo(Some "main"))
            let afterMutation = sandbox.RevParse "HEAD"
            Assert.That(afterMutation, Is.Not.EqualTo before)
            Assert.That(evidence.ObservedCreatedRevision, Is.EqualTo(Some afterMutation))
            Assert.That(evidence.CreatedRevision, Is.EqualTo(Some afterMutation))
            Assert.That((evidence.Paths = [ "selected.txt" ]), Is.True)

            let request =
                CommitRequest.Create(sandbox.Path, [ "selected.txt" ], "selected only")

            let! retry = Agent.commit request CancellationToken.None
            Assert.That(retry.Error.Value.Code, Is.EqualTo AgentErrorCode.InvalidInput)
            Assert.That(sandbox.RevParse "HEAD", Is.EqualTo afterMutation)
            Assert.That(File.ReadAllText(Path.Combine(sandbox.Path, "unrelated.txt")), Is.EqualTo "unrelated\n")
        }

    [<Test>]
    member _.``A backend success with a different revision path set is a structured ambiguous failure``() : Task =
        task {
            CommitTest.requireGit ()
            use sandbox = GitSandbox.Init "agent-commit-wrong-revision"
            sandbox.CommitFile("selected.txt", "before selected\n", "base selected")
            sandbox.CommitFile("different.txt", "before different\n", "base different")
            sandbox.Write("selected.txt", "selected\n")
            sandbox.Write("different.txt", "different\n")

            let runner =
                CommitTest.WrongRevisionRunner(JobRunner() :> IProcessRunner, sandbox.Path)

            let repo = Repo.FromGit(sandbox.Path, sandbox.Path, Git.WithRunner runner)

            let! envelope =
                Agent.commitWith
                    repo
                    [ "selected.txt" ]
                    "selected only"
                    CancellationToken.None
                    Agent.DefaultOutputLimitBytes

            Assert.That(envelope.Status, Is.EqualTo AgentStatus.Error)
            Assert.That(envelope.Error.Value.Code, Is.EqualTo AgentErrorCode.Backend)
            let evidence = CommitTest.expectCommit envelope
            Assert.That(evidence.Completion, Is.EqualTo CommitCompletion.Ambiguous)
            Assert.That(evidence.ObservedCreatedRevision, Is.EqualTo(Some(sandbox.RevParse "HEAD")))
            Assert.That(evidence.CreatedRevision.IsNone, Is.True)
            Assert.That(evidence.Paths, Is.Empty)
            Assert.That(evidence.SelectedPathsRemaining, Is.EqualTo(Some true))
        }

    [<Test>]
    member _.``A second Git commit with an aggregate matching path set is not claimed``() : Task =
        task {
            CommitTest.requireGit ()
            use sandbox = GitSandbox.Init "agent-commit-multi-revision"
            sandbox.CommitFile("selected.txt", "before\n", "base")
            sandbox.Write("selected.txt", "selected\n")
            let sourceRevision = sandbox.RevParse "HEAD"

            let runner =
                CommitTest.AdditionalCommitRunner(JobRunner() :> IProcessRunner, sandbox.Path)

            let repo = Repo.FromGit(sandbox.Path, sandbox.Path, Git.WithRunner runner)

            let! envelope =
                Agent.commitWith
                    repo
                    [ "selected.txt" ]
                    "selected only"
                    CancellationToken.None
                    Agent.DefaultOutputLimitBytes

            Assert.That(envelope.Status, Is.EqualTo AgentStatus.Error)
            Assert.That(envelope.Error.Value.Code, Is.EqualTo AgentErrorCode.Backend)
            let evidence = CommitTest.expectCommit envelope
            let observedRevision = sandbox.RevParse "HEAD"
            Assert.That(evidence.SourceRevision, Is.EqualTo(Some sourceRevision))
            Assert.That(evidence.ObservedCreatedRevision, Is.EqualTo(Some observedRevision))
            Assert.That(evidence.CreatedRevision.IsNone, Is.True)
            Assert.That(evidence.Paths, Is.Empty)
            Assert.That(evidence.Completion, Is.EqualTo CommitCompletion.Ambiguous)

            match! repo.Git.Value.DiffBetween(sandbox.Path, sourceRevision, observedRevision) with
            | Error error -> Assert.Fail error.Message
            | Ok diffs -> Assert.That((diffs |> List.map _.Path |> Set.ofList) = set [ "selected.txt" ], Is.True)
        }

    [<Test>]
    member _.``An unborn Git repository verifies exactly one root revision``() : Task =
        task {
            CommitTest.requireGit ()
            use sandbox = GitSandbox.Init "agent-commit-unborn"
            sandbox.Write("selected.txt", "selected\n")
            sandbox.Git [ "add"; "--"; "selected.txt" ]

            let! envelope =
                Agent.commit
                    (CommitRequest.Create(sandbox.Path, [ "selected.txt" ], "initial selected"))
                    CancellationToken.None

            let evidence = CommitTest.expectCommit envelope
            Assert.That(envelope.Status, Is.EqualTo AgentStatus.Success)
            Assert.That(evidence.SourceRevision.IsNone, Is.True)
            Assert.That(evidence.ObservedCreatedRevision.IsSome, Is.True)
            Assert.That(evidence.CreatedRevision, Is.EqualTo evidence.ObservedCreatedRevision)
            Assert.That((evidence.Paths = [ "selected.txt" ]), Is.True)
            Assert.That(evidence.Completion, Is.EqualTo CommitCompletion.Verified)

            match Repo.Open sandbox.Path with
            | Error error -> Assert.Fail error.Message
            | Ok repo ->
                match! repo.Log(evidence.CreatedRevision.Value, 2) with
                | Error error -> Assert.Fail error.Message
                | Ok [ root ] -> Assert.That(root.Id, Is.EqualTo evidence.CreatedRevision.Value)
                | Ok commits -> Assert.Fail $"expected one sole root revision, observed {commits.Length}"
        }

    [<Test>]
    member _.``Git rename expands to one verified old and new backend path pair``() : Task =
        task {
            CommitTest.requireGit ()
            use sandbox = GitSandbox.Init "agent-commit-git-rename"
            sandbox.CommitFile("old-name.txt", "content\n", "base rename")
            sandbox.CommitFile("unrelated.txt", "before\n", "base unrelated")
            sandbox.Git [ "mv"; "old-name.txt"; "new-name.txt" ]
            sandbox.Write("unrelated.txt", "dirty\n")

            let! envelope =
                Agent.commit
                    (CommitRequest.Create(sandbox.Path, [ "new-name.txt" ], "rename exactly"))
                    CancellationToken.None

            let data = CommitTest.expectCommit envelope
            Assert.That(data.Completion, Is.EqualTo CommitCompletion.Verified)
            Assert.That((data.RequestedPaths = [ "new-name.txt" ]), Is.True)
            Assert.That((Set.ofList data.BackendPaths = set [ "old-name.txt"; "new-name.txt" ]), Is.True)
            Assert.That((Set.ofList data.Paths = set [ "old-name.txt"; "new-name.txt" ]), Is.True)
            Assert.That(File.Exists(Path.Combine(sandbox.Path, "old-name.txt")), Is.False)
            Assert.That(File.ReadAllText(Path.Combine(sandbox.Path, "new-name.txt")), Is.EqualTo "content\n")
            Assert.That(File.ReadAllText(Path.Combine(sandbox.Path, "unrelated.txt")), Is.EqualTo "dirty\n")

            let createdRevision = sandbox.RevParse "HEAD"

            let! replay =
                Agent.commit
                    (CommitRequest.Create(sandbox.Path, [ "new-name.txt" ], "rename exactly"))
                    CancellationToken.None

            Assert.That(replay.Error.Value.Code, Is.EqualTo AgentErrorCode.InvalidInput)
            Assert.That(sandbox.RevParse "HEAD", Is.EqualTo createdRevision)
        }

    [<Test>]
    member _.``Jujutsu commit preserves unrelated dirt and reports the created revision``() : Task =
        task {
            CommitTest.requireJj ()
            use sandbox = JjSandbox.InitNonColocated "agent-commit-jj"
            sandbox.Write("base.txt", "base\n")
            sandbox.Write("tracked-unrelated.txt", "before unrelated\n")
            sandbox.Describe "base"
            sandbox.NewChange "work"
            sandbox.Write("selected.txt", "selected\n")
            sandbox.Write("tracked-unrelated.txt", "dirty tracked\n")
            sandbox.Write("untracked-unrelated.txt", "dirty untracked\n")

            let request =
                CommitRequest.Create(sandbox.Path, [ "selected.txt" ], "selected only")

            let repo =
                Repo.FromJj(sandbox.Path, sandbox.Path, Jj.WithRunner(CommitTest.isolatedJjRunner sandbox.Path))

            let! envelope =
                Agent.commitWith repo request.Paths request.Message CancellationToken.None request.OutputLimitBytes

            let data = CommitTest.expectCommit envelope

            Assert.That(data.Backend, Is.EqualTo "jj")
            Assert.That(data.SourceRevision.IsSome, Is.True)
            Assert.That(data.CreatedRevision.IsSome, Is.True)
            Assert.That((data.Paths = [ "selected.txt" ]), Is.True)
            Assert.That(envelope.Warnings |> List.map _.Code, Does.Contain "unrelated-changes-preserved")

            Assert.That(
                File.ReadAllText(Path.Combine(sandbox.Path, "tracked-unrelated.txt")),
                Is.EqualTo "dirty tracked\n"
            )

            Assert.That(
                File.ReadAllText(Path.Combine(sandbox.Path, "untracked-unrelated.txt")),
                Is.EqualTo "dirty untracked\n"
            )

            match! repo.ChangedFiles() with
            | Error error -> Assert.Fail error.Message
            | Ok changes ->
                let remainingPaths = changes |> List.map _.Path |> List.sort

                Assert.That((remainingPaths = [ "tracked-unrelated.txt"; "untracked-unrelated.txt" ]), Is.True)

            let createdRevision = data.CreatedRevision.Value

            match! repo.Log("@-", 1) with
            | Error error -> Assert.Fail error.Message
            | Ok(commit :: _) -> Assert.That(createdRevision, Is.EqualTo commit.Id)
            | Ok [] -> Assert.Fail "created jj revision was not found"

            match! repo.LogPaths(createdRevision, 1, [ "selected.txt" ]) with
            | Error error -> Assert.Fail error.Message
            | Ok(commit :: _) -> Assert.That(commit.Id, Is.EqualTo createdRevision)
            | Ok [] -> Assert.Fail "created Jujutsu revision did not include selected.txt"

            match! repo.LogPaths(createdRevision, 1, [ "tracked-unrelated.txt" ]) with
            | Error error -> Assert.Fail error.Message
            | Ok(commit :: _) -> Assert.That(commit.Id, Is.Not.EqualTo createdRevision)
            | Ok [] -> ()

            let! retry =
                Agent.commitWith repo request.Paths request.Message CancellationToken.None request.OutputLimitBytes

            Assert.That(retry.Error.Value.Code, Is.EqualTo AgentErrorCode.InvalidInput)

            Assert.That(
                File.ReadAllText(Path.Combine(sandbox.Path, "tracked-unrelated.txt")),
                Is.EqualTo "dirty tracked\n"
            )
        }

    [<Test>]
    member _.``Jujutsu replay after a post-commit failure cannot capture unrelated dirt``() : Task =
        task {
            CommitTest.requireJj ()
            use sandbox = JjSandbox.InitNonColocated "agent-commit-jj-recovery"
            sandbox.Write("base.txt", "base\n")
            sandbox.Describe "base"
            sandbox.NewChange "work"
            sandbox.Write("selected.txt", "selected\n")
            sandbox.Write("unrelated.txt", "unrelated\n")

            let openedBefore =
                Repo.FromJj(sandbox.Path, sandbox.Path, Jj.WithRunner(CommitTest.isolatedJjRunner sandbox.Path))

            let! beforeSnapshot = openedBefore.Snapshot()

            let beforeRevision =
                match beforeSnapshot with
                | Ok snapshot -> snapshot.Head
                | Error error -> failwith error.Message

            let failingRunner =
                CommitTest.FailAfterSuccessfulCommitRunner(CommitTest.isolatedJjRunner sandbox.Path)

            let repo = Repo.FromJj(sandbox.Path, sandbox.Path, Jj.WithRunner failingRunner)

            let! first =
                Agent.commitWith
                    repo
                    [ "selected.txt" ]
                    "selected only"
                    CancellationToken.None
                    Agent.DefaultOutputLimitBytes

            Assert.That(first.Error.Value.Code, Is.EqualTo AgentErrorCode.Backend)
            let evidence = CommitTest.expectCommit first
            Assert.That(evidence.Completion, Is.EqualTo CommitCompletion.Ambiguous)
            Assert.That(evidence.SourceRevision, Is.EqualTo beforeRevision)
            Assert.That(evidence.CreatedRevision.IsSome, Is.True)
            Assert.That((evidence.Paths = [ "selected.txt" ]), Is.True)

            let openedAfter =
                Repo.FromJj(sandbox.Path, sandbox.Path, Jj.WithRunner(CommitTest.isolatedJjRunner sandbox.Path))

            match! openedAfter.Snapshot() with
            | Error error -> Assert.Fail error.Message
            | Ok snapshot -> Assert.That(snapshot.Head, Is.Not.EqualTo beforeRevision)

            let request =
                CommitRequest.Create(sandbox.Path, [ "selected.txt" ], "selected only")

            let! retry =
                Agent.commitWith
                    openedAfter
                    request.Paths
                    request.Message
                    CancellationToken.None
                    request.OutputLimitBytes

            Assert.That(retry.Error.Value.Code, Is.EqualTo AgentErrorCode.InvalidInput)

            match! openedAfter.ChangedFiles() with
            | Error error -> Assert.Fail error.Message
            | Ok changes -> Assert.That((changes |> List.map _.Path) = [ "unrelated.txt" ], Is.True)
        }

    [<Test>]
    member _.``Jujutsu failures without a revision transition emit no created candidate``() : Task =
        task {
            CommitTest.requireJj ()

            let failures =
                [ AgentErrorCode.Backend, ProcessError.Spawn("jj", "injected commit failure")
                  AgentErrorCode.Timeout, ProcessError.Timeout("jj", TimeSpan.FromSeconds 1.0, "", "") ]

            for expectedCode, failure in failures do
                use sandbox =
                    JjSandbox.InitNonColocated $"agent-commit-jj-no-transition-{expectedCode}"

                sandbox.Write("base.txt", "base\n")
                sandbox.Describe "base"
                sandbox.NewChange "work"
                sandbox.Write("selected.txt", "selected\n")

                let inner = CommitTest.isolatedJjRunner sandbox.Path
                let runner = CommitTest.FailingCommitRunner(inner, failure) :> IProcessRunner
                let repo = Repo.FromJj(sandbox.Path, sandbox.Path, Jj.WithRunner runner)
                let! before = repo.Snapshot()

                let sourceRevision =
                    match before with
                    | Ok snapshot -> snapshot.Head
                    | Error error -> failwith error.Message

                let! envelope =
                    Agent.commitWith
                        repo
                        [ "selected.txt" ]
                        "selected only"
                        CancellationToken.None
                        Agent.DefaultOutputLimitBytes

                Assert.That(envelope.Status, Is.EqualTo AgentStatus.Error)
                Assert.That(envelope.Error.Value.Code, Is.EqualTo expectedCode)
                let evidence = CommitTest.expectCommit envelope
                Assert.That(evidence.SourceRevision, Is.EqualTo sourceRevision)
                Assert.That(evidence.ObservedRevision, Is.EqualTo sourceRevision)
                Assert.That(evidence.ObservedCreatedRevision.IsNone, Is.True)
                Assert.That(evidence.CreatedRevision.IsNone, Is.True)
                Assert.That(evidence.Paths, Is.Empty)
                Assert.That(evidence.Completion, Is.EqualTo CommitCompletion.Ambiguous)
        }

    [<Test>]
    member _.``Jujutsu rename expands to one verified old and new backend path pair``() : Task =
        task {
            CommitTest.requireJj ()
            use sandbox = JjSandbox.InitNonColocated "agent-commit-jj-rename"
            sandbox.Write("old-name.txt", "content\n")
            sandbox.Write("unrelated.txt", "before\n")
            sandbox.Describe "base"
            sandbox.NewChange "rename"
            File.Move(Path.Combine(sandbox.Path, "old-name.txt"), Path.Combine(sandbox.Path, "new-name.txt"))
            sandbox.Write("unrelated.txt", "dirty\n")

            let repo =
                Repo.FromJj(sandbox.Path, sandbox.Path, Jj.WithRunner(CommitTest.isolatedJjRunner sandbox.Path))

            let! envelope =
                Agent.commitWith
                    repo
                    [ "new-name.txt" ]
                    "rename exactly"
                    CancellationToken.None
                    Agent.DefaultOutputLimitBytes

            let data = CommitTest.expectCommit envelope
            Assert.That(data.Completion, Is.EqualTo CommitCompletion.Verified)
            Assert.That((data.RequestedPaths = [ "new-name.txt" ]), Is.True)
            Assert.That((Set.ofList data.BackendPaths = set [ "old-name.txt"; "new-name.txt" ]), Is.True)
            Assert.That((Set.ofList data.Paths = set [ "old-name.txt"; "new-name.txt" ]), Is.True)
            Assert.That(File.Exists(Path.Combine(sandbox.Path, "old-name.txt")), Is.False)
            Assert.That(File.ReadAllText(Path.Combine(sandbox.Path, "new-name.txt")), Is.EqualTo "content\n")
            Assert.That(File.ReadAllText(Path.Combine(sandbox.Path, "unrelated.txt")), Is.EqualTo "dirty\n")

            let! afterCommit = repo.Snapshot()

            let afterRevision =
                match afterCommit with
                | Ok snapshot -> snapshot.Head
                | Error error -> failwith error.Message

            let! replay =
                Agent.commitWith
                    repo
                    [ "new-name.txt" ]
                    "rename exactly"
                    CancellationToken.None
                    Agent.DefaultOutputLimitBytes

            Assert.That(replay.Error.Value.Code, Is.EqualTo AgentErrorCode.InvalidInput)

            match! repo.Snapshot() with
            | Ok snapshot -> Assert.That(snapshot.Head, Is.EqualTo afterRevision)
            | Error error -> Assert.Fail error.Message
        }

    [<Test>]
    member _.``Inspect API applies the final envelope budget``() : Task =
        task {
            let root = createTempRoot "bounded-inspect"
            let remote = "https://example.test/" + String.replicate 2_000 "x"

            try
                let! envelope =
                    Agent.inspectWith
                        (gitSnapshot root remote)
                        None
                        CancellationToken.None
                        Agent.MinimumOutputLimitBytes

                Assert.That(envelope.Status, Is.EqualTo AgentStatus.Error)
                Assert.That(envelope.Data.IsNone, Is.True)
                Assert.That(envelope.Error.Value.Code, Is.EqualTo AgentErrorCode.OutputLimit)
                Assert.That(envelope.Error.Value.RequiredBytes.Value, Is.GreaterThan Agent.MinimumOutputLimitBytes)
            finally
                Directory.Delete(root, true)
        }

    [<Test>]
    member _.``Jujutsu inspect reports absent forge without fabricating authentication``() : Task =
        task {
            let root = createTempRoot "jj-inspect"

            try
                let! envelope =
                    Agent.inspectWith (jjSnapshot root) None CancellationToken.None Agent.DefaultOutputLimitBytes

                match envelope.Data with
                | Some(AgentPayload.Inspect data) ->
                    Assert.That(data.Backend, Is.EqualTo "jj")
                    Assert.That(data.Identity.Revision, Is.EqualTo(Some "jjcommit123"))
                    Assert.That(data.Forge.Status, Is.EqualTo AgentForgeStatus.Absent)
                    Assert.That(data.Forge.Authenticated, Is.False)
                | _ -> Assert.Fail "inspect payload expected"
            finally
                Directory.Delete(root, true)
        }

    [<Test>]
    member _.``Unknown forge is a typed successful unsupported fact``() : Task =
        task {
            let root = createTempRoot "unknown-forge"

            try
                let! envelope =
                    Agent.inspectWith
                        (jjSnapshot root)
                        (Some(Forge.FromUnknown root))
                        CancellationToken.None
                        Agent.DefaultOutputLimitBytes

                match envelope.Data with
                | Some(AgentPayload.Inspect data) ->
                    Assert.That(data.Forge.Status, Is.EqualTo AgentForgeStatus.Unsupported)
                    Assert.That(data.Forge.Kind.IsNone, Is.True)
                    Assert.That(data.Forge.Capabilities.PullRequestCreate, Is.False)
                | _ -> Assert.Fail "inspect payload expected"
            finally
                Directory.Delete(root, true)
        }

    [<Test>]
    member _.``Git changes summary reuses typed status and diff stat parsers``() : Task =
        task {
            let root = createTempRoot "git-summary"
            let nul = string '\000'

            try
                let runner =
                    ScriptedRunner()
                        .On([ "status"; "--porcelain=v1"; "-z" ], Reply.Ok($" M src/App.fs{nul}?? new.fs{nul}"))
                        .On([ "rev-parse"; "--verify"; "-q"; "HEAD" ], Reply.Ok "abc123\n")
                        .On(
                            [ "diff"; "--no-relative"; "--shortstat"; "HEAD" ],
                            Reply.Ok " 1 file changed, 4 insertions(+), 1 deletion(-)\n"
                        )

                let repo = Repo.FromGit(root, root, Git.WithRunner runner)

                let! envelope =
                    Agent.changesWith repo ChangesMode.Summary CancellationToken.None Agent.DefaultOutputLimitBytes

                match envelope.Data with
                | Some(AgentPayload.Changes(ChangesData.Summary summary)) ->
                    Assert.That(summary.Paths.Length, Is.EqualTo 2)
                    Assert.That(summary.DiffStat.FilesChanged, Is.EqualTo 1UL)
                    Assert.That(summary.DiffStat.Insertions, Is.EqualTo 4UL)
                    Assert.That(summary.DiffStat.Deletions, Is.EqualTo 1UL)
                | _ -> Assert.Fail "changes payload expected"
            finally
                Directory.Delete(root, true)
        }

    [<Test>]
    member _.``Jujutsu structured diff is typed and redacted at API and wire boundaries``() : Task =
        task {
            let root = createTempRoot "jj-diff"
            let secret = "diff-secret"

            let raw =
                $"diff --git a/config.txt b/config.txt\n--- a/config.txt\n+++ b/config.txt\n@@ -1,1 +1,1 @@\n-old\n+api_key={secret}\n"

            try
                let runner = ScriptedRunner().On([ "diff"; "-r"; "@"; "--git" ], Reply.Ok raw)

                let repo = Repo.FromJj(root, root, Jj.WithRunner runner)

                let! envelope =
                    Agent.changesWith
                        repo
                        ChangesMode.StructuredDiff
                        CancellationToken.None
                        Agent.DefaultOutputLimitBytes

                match envelope.Data with
                | Some(AgentPayload.Changes(ChangesData.StructuredDiff files)) ->
                    Assert.That(files.Length, Is.EqualTo 1)
                    Assert.That(files.Head.Hunks.Head.Lines.[1].Text, Does.Not.Contain secret)
                | _ -> Assert.Fail "changes payload expected"

                Assert.That(AgentWire.serialize envelope, Does.Not.Contain secret)
            finally
                Directory.Delete(root, true)
        }

    [<Test>]
    member _.``Changes cancellation is a typed terminal outcome``() : Task =
        task {
            use cts = new CancellationTokenSource()
            cts.Cancel()

            let repo = Repo.FromGit(".", ".", Git.WithRunner(ScriptedRunner()))

            let! envelope = Agent.changesWith repo ChangesMode.Summary cts.Token Agent.DefaultOutputLimitBytes

            Assert.That(envelope.Status, Is.EqualTo AgentStatus.Error)
            Assert.That(envelope.Error.Value.Code, Is.EqualTo AgentErrorCode.Cancellation)
            Assert.That(envelope.Terminal, Is.True)
        }

    [<Test>]
    member _.``Structured change API and wire output share one final budget outcome``() : Task =
        task {
            let root = createTempRoot "bounded-diff"
            let content = String.replicate 2_000 "x"

            let raw =
                $"diff --git a/large.txt b/large.txt\n--- a/large.txt\n+++ b/large.txt\n@@ -0,0 +1,1 @@\n+{content}\n"

            try
                let repo =
                    Repo.FromJj(
                        root,
                        root,
                        Jj.WithRunner(ScriptedRunner().On([ "diff"; "-r"; "@"; "--git" ], Reply.Ok raw))
                    )

                let! envelope =
                    Agent.changesWith
                        repo
                        ChangesMode.StructuredDiff
                        CancellationToken.None
                        Agent.MinimumOutputLimitBytes

                Assert.That(envelope.Status, Is.EqualTo AgentStatus.Error)
                Assert.That(envelope.Data.IsNone, Is.True)
                Assert.That(envelope.Error.Value.Code, Is.EqualTo AgentErrorCode.OutputLimit)
                Assert.That(envelope.Error.Value.LimitBytes, Is.EqualTo(Some Agent.MinimumOutputLimitBytes))
                Assert.That(envelope.Error.Value.RequiredBytes.Value, Is.GreaterThan Agent.MinimumOutputLimitBytes)

                let rendered = AgentWire.render Agent.MinimumOutputLimitBytes envelope
                Assert.That(rendered.ExitCode, Is.EqualTo 28)

                Assert.That(
                    Encoding.UTF8.GetByteCount rendered.Stdout,
                    Is.LessThanOrEqualTo Agent.MinimumOutputLimitBytes
                )

                use document = JsonDocument.Parse rendered.Stdout
                let error = document.RootElement.GetProperty "error"
                Assert.That(error.GetProperty("code").GetString(), Is.EqualTo "output-limit")
                Assert.That(error.GetProperty("truncated").GetBoolean(), Is.True)

                Assert.That(
                    error.GetProperty("requiredBytes").GetInt32(),
                    Is.EqualTo envelope.Error.Value.RequiredBytes.Value
                )
            finally
                Directory.Delete(root, true)
        }
