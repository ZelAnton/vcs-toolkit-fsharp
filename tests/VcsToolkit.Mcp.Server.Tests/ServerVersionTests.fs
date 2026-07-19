module VcsToolkit.Mcp.Server.Tests.ServerVersionTests

open System.Reflection
open NUnit.Framework

// ---------------------------------------------------------------------------
// T-079: `ServerInfo.Version` must track the built assembly's version rather than a
// hardcoded "1.0.0" literal.
// ---------------------------------------------------------------------------

[<TestFixture>]
type ServerVersionTests() =

    /// `Main.serverVersion` reads the entry assembly's
    /// `AssemblyInformationalVersionAttribute`. In this test host the "entry assembly" is the
    /// test runner, not `vcs-mcp`, so `Main.serverVersion` itself can't be called directly to
    /// exercise the assembly-reading behaviour end to end here — instead this asserts the
    /// production code no longer returns the old hardcoded literal, and separately proves
    /// `vcs-mcp`'s own assembly (read the same way `Main.serverVersion` reads the entry
    /// assembly) carries a real, non-"1.0.0" informational version.
    [<Test>]
    member _.ServerVersionIsNotTheOldHardcodedLiteral() =
        Assert.That(Main.serverVersion (), Is.Not.EqualTo "1.0.0")

    [<Test>]
    member _.McpServerAssemblyInformationalVersionIsNotTheOldHardcodedLiteral() =
        // `Main.serverVersion` is a function value; its runtime type is a compiler-generated
        // closure class compiled into the `vcs-mcp` assembly, so this reaches the right
        // assembly without a type to name directly (the `Main` module declares no types).
        let asm = Main.serverVersion.GetType().Assembly

        match asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null -> Assert.Fail "vcs-mcp assembly must carry AssemblyInformationalVersionAttribute"
        | attr -> Assert.That(attr.InformationalVersion, Is.Not.EqualTo "1.0.0")

    [<Test>]
    member _.ServerVersionReadsFromAssemblyMetadataDynamically() =
        // `Main.serverVersion` is a function value; its runtime type is compiled into the
        // `vcs-mcp` assembly, so this reaches the assembly whose metadata the server ships.
        let asm = Main.serverVersion.GetType().Assembly

        let expectedAssemblyVersion =
            match asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
            | null ->
                Assert.Fail "vcs-mcp assembly must carry AssemblyInformationalVersionAttribute"
                ""
            | attr -> attr.InformationalVersion

        let versionReadFromAssembly = Main.readVersionFromAssembly asm
        Assert.That(versionReadFromAssembly, Is.EqualTo expectedAssemblyVersion)
        Assert.That(versionReadFromAssembly, Is.Not.EqualTo "0.0.0-unknown")
        Assert.That(versionReadFromAssembly, Is.Not.EqualTo "1.0.0")

        // The NUnit host is the entry assembly in this process. Comparing `serverVersion()` to
        // the same assembly read verifies its delegation rather than accepting a hardcoded value.
        let expectedEntryAssemblyVersion =
            Main.readVersionFromAssembly (Assembly.GetEntryAssembly())

        Assert.That(Main.serverVersion (), Is.EqualTo expectedEntryAssemblyVersion)

    /// Proves the end-to-end wiring, not just that `serverVersion()` in isolation is non-"1.0.0":
    /// `runServer` builds the handshake's `ServerInfo` via
    /// `options.ServerInfo <- Main.buildServerInfo()`, and `buildServerInfo` is
    /// `Implementation(Name = "vcs-mcp", Version = serverVersion ())`. This test calls
    /// `buildServerInfo` — the exact function `runServer` uses — and asserts its `Version`
    /// equals `serverVersion()`'s own result, demonstrating the value actually assigned to
    /// `ServerInfo.Version` is `serverVersion()`'s output and not a separately hardcoded
    /// literal that merely happens not to be "1.0.0". (The comparison is against
    /// `serverVersion()` rather than the `vcs-mcp` assembly's own
    /// `AssemblyInformationalVersionAttribute` because in this test host the entry assembly
    /// `serverVersion()` reads is the test runner, not `vcs-mcp` — see the first test's
    /// remark above; that per-assembly reading behaviour is exercised in isolation by
    /// `McpServerAssemblyInformationalVersionIsNotTheOldHardcodedLiteral` above.)
    [<Test>]
    member _.ServerInfoVersionIsWiredToServerVersion() =
        let info = Main.buildServerInfo ()
        Assert.That(info.Name, Is.EqualTo "vcs-mcp")
        Assert.That(info.Version, Is.EqualTo(Main.serverVersion ()))
