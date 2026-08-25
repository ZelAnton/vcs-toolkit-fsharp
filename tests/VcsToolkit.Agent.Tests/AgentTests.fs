namespace VcsToolkit.Agent.Tests

open System
open System.IO
open System.Reflection
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit.Testing
open VcsToolkit.Agent
open VcsToolkit.Core
open VcsToolkit.Forge
open VcsToolkit.Git
open VcsToolkit.Jj

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
        let data: ChangesData =
            { Mode = ChangesMode.Summary
              Summary =
                Some
                    { FilesChanged = 1UL
                      Insertions = 2UL
                      Deletions = 1UL
                      Paths =
                        [ { Path = "src/App.fs"
                            OldPath = None
                            Change = "modified" } ] }
              Files = [] }

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
              Data = None
              Error =
                Some
                    { Code = AgentErrorCode.ExternalCommand
                      Message = message
                      Retryable = false
                      Truncated = false
                      LimitBytes = None
                      RequiredBytes = None }
              Warnings =
                [ { Code = "consumer-warning"
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
                            Reply.Ok " 2 files changed, 4 insertions(+), 1 deletion(-)\n"
                        )

                let repo = Repo.FromGit(root, root, Git.WithRunner runner)

                let! envelope =
                    Agent.changesWith repo ChangesMode.Summary CancellationToken.None Agent.DefaultOutputLimitBytes

                match envelope.Data with
                | Some(AgentPayload.Changes data) ->
                    Assert.That(data.Mode, Is.EqualTo ChangesMode.Summary)
                    Assert.That(data.Files, Is.Empty)
                    Assert.That(data.Summary.Value.FilesChanged, Is.EqualTo 2UL)
                    Assert.That(data.Summary.Value.Insertions, Is.EqualTo 4UL)
                    Assert.That(data.Summary.Value.Paths.Length, Is.EqualTo 2)
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
                | Some(AgentPayload.Changes data) ->
                    Assert.That(data.Files.Length, Is.EqualTo 1)
                    Assert.That(data.Files.Head.Hunks.Head.Lines.[1].Text, Does.Not.Contain secret)
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
    member _.``Structured change output refuses content beyond the configured wire budget``() : Task =
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
            finally
                Directory.Delete(root, true)
        }
