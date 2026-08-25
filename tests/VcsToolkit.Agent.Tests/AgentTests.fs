namespace VcsToolkit.Agent.Tests

open System
open System.IO
open System.Reflection
open System.Text
open System.Text.Json
open NUnit.Framework
open VcsToolkit.Agent

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
