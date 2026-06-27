namespace VcsToolkit.CliSupport

/// A secret value — an API token, a password — that redacts itself whenever it is
/// formatted, so it can't leak into a log line or an error message. Read the
/// underlying value only at the point of use, via `Expose`.
///
/// Redaction is the achievable guarantee here; this type does not securely scrub
/// its memory. Deliberately has no structural equality: comparing secrets with
/// short-circuiting string `=` is timing-variable and turns the type into an
/// equality oracle. Compare the exposed value explicitly if you must.
[<Sealed; NoEquality; NoComparison>]
type Secret(value: string) =

    /// Wrap a secret value.
    static member New(value: string) = Secret value

    /// Borrow the underlying secret. Call this only where the value is actually
    /// needed (e.g. setting an environment variable on a command); don't store or
    /// log the result.
    member _.Expose() = value

    /// Redacts itself — the underlying value is reachable only through `Expose`.
    override _.ToString() = "***"
