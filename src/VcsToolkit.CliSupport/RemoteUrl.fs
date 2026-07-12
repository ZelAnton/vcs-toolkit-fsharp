namespace VcsToolkit.CliSupport

open System

/// The security-sensitive *mechanics* of pulling the host authority out of a git remote
/// URL — the steps that were duplicated across `Credentials.httpsHost` (this assembly),
/// GitHub's `hostFromRemoteUrl`, and Forge's `hostOf`. Only the mechanics live here:
/// finding the text after a `scheme://`, taking the authority up to the first `/`/`?`/`#`,
/// dropping a `user:pass@` userinfo, and stripping a `:port`. Each consumer keeps its own
/// *policy* — which schemes it accepts, whether an IPv6-bracket authority is rejected or
/// unwrapped (and how it is guarded against zone-id spoofing), whether an scp-form host must
/// be dotted, and error-vs-`None` — as a thin wrapper over these primitives, so the three
/// deliberately different security postures share a skeleton without being silently merged.
[<RequireQualifiedAccess>]
module RemoteUrl =

    /// The `scheme://` separator.
    [<Literal>]
    let private SchemeSep = "://"

    /// Drop a `user[:pass]@` userinfo prefix by the LAST `@`, so an `@` inside the password
    /// can't move the host boundary. A string with no `@` is returned unchanged.
    let dropUserinfo (s: string) : string =
        match s.LastIndexOf('@') with
        | i when i >= 0 -> s.Substring(i + 1)
        | _ -> s

    /// The text after the first `scheme://` separator, or `None` when the URL carries no
    /// scheme — the scp-like `[user@]host:path` (or bare-path) form each consumer handles
    /// on its own. The match is `Ordinal`, so scheme detection never depends on culture.
    let afterScheme (url: string) : string option =
        match url.IndexOf(SchemeSep, StringComparison.Ordinal) with
        | i when i >= 0 -> Some(url.Substring(i + SchemeSep.Length))
        | _ -> None

    /// The `[user@]host[:port]` authority of a scheme URL, given the text **after** the
    /// `://` separator: everything up to the first `/`/`?`/`#`, with userinfo dropped. The
    /// port and any `[...]` IPv6 brackets are left intact — stripping a port (`stripPort`)
    /// or handling brackets is each consumer's own policy.
    let authority (afterSchemeText: string) : string =
        afterSchemeText.Split([| '/'; '?'; '#' |]).[0] |> dropUserinfo

    /// Strip an optional trailing `:port` from a (non-bracketed) `host[:port]`, returning
    /// the first colon-delimited field. A leading-colon or empty input yields `""`, left for
    /// the caller to reject.
    let stripPort (hostPort: string) : string = hostPort.Split(':').[0]
