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
