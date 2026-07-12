namespace VcsToolkit.CliSupport

open System
open System.Threading.Tasks
open ProcessKit

/// A resolved credential: a `Secret` plus an optional username. For a forge token
/// only the secret is used; for git HTTPS the username pairs with the secret as the
/// password (a personal-access token).
[<Sealed; NoEquality; NoComparison>]
type Credential private (username: string option, secret: Secret) =

    /// A bare token/secret with no username (the forge case, and git HTTPS where any
    /// username is accepted — a default is supplied at the git command site).
    static member Token(secret: Secret) = Credential(None, secret)

    /// A bare token from a plain string.
    static member Token(secret: string) = Credential(None, Secret secret)

    /// A username paired with a secret (git HTTPS user/password). The username is
    /// used only for git HTTPS; forge token-env injection uses only the secret.
    static member Userpass(username: string, secret: Secret) = Credential(Some username, secret)

    /// A username paired with a secret from a plain string.
    static member Userpass(username: string, secret: string) =
        Credential(Some username, Secret secret)

    /// The username, if one was supplied.
    member _.Username = username

    /// The secret (token/password).
    member _.Secret = secret

    /// Renders the username (if any) and a redacted secret — never the secret value.
    override _.ToString() =
        match username with
        | Some u -> sprintf "Credential(username = %s, secret = ***)" u
        | None -> "Credential(secret = ***)"

/// Which backend/tool is asking for a credential — lets a provider return different
/// secrets per service.
[<RequireQualifiedAccess>]
type CredentialService =
    /// A `git` remote operation (fetch/push/clone over HTTPS).
    | Git
    /// A GitHub (`gh`) API operation.
    | GitHub
    /// A GitLab (`glab`) API operation.
    | GitLab
    /// A Gitea (`tea`) API operation. Reserved: `tea` has no per-operation token
    /// mechanism today, so no backend currently emits this.
    | Gitea

/// The context of a credential request: which service, and the remote host if the
/// backend knows it (forge calls often defer host resolution to the CLI).
type CredentialRequest =
    {
        /// The backend/tool making the request.
        Service: CredentialService
        /// The remote host (e.g. `github.com`), if known.
        Host: string option
    }

    /// A request for `service` with no known host.
    static member Create(service: CredentialService) = { Service = service; Host = None }

    /// Attach a known remote host.
    member this.WithHost(host: string) = { this with Host = Some host }

/// Supplies a `Credential` for a `CredentialRequest`, just-in-time. Returning
/// `Ok None` means "I have nothing for this request" — the backend then falls back
/// to its ambient CLI auth, exactly as if no provider were configured. An `Error`
/// aborts the operation. A returned credential whose secret is empty is treated as
/// `None` (ambient) by the clients.
type ICredentialProvider =
    abstract member Credential: request: CredentialRequest -> Task<Result<Credential option, ProcessError>>

/// A provider that always yields the same `Credential` for every request — the
/// common "use this one token" case.
[<Sealed>]
type StaticCredential(credential: Credential) =

    /// Always supply a bare token.
    static member Token(secret: string) =
        StaticCredential(Credential.Token secret)

    interface ICredentialProvider with
        member _.Credential(_request) = Task.FromResult(Ok(Some credential))

/// A provider that reads a bare token from a named environment variable, at request
/// time. If the variable is unset/empty it yields `None` (fall back to ambient auth)
/// rather than erroring.
[<Sealed>]
type EnvToken(var: string, username: string option) =

    /// Read the token from environment variable `var`.
    new(var: string) = EnvToken(var, None)

    /// Pair the token with a username (for git HTTPS).
    member _.WithUsername(username: string) = EnvToken(var, Some username)

    interface ICredentialProvider with
        member _.Credential(_request) =
            let result =
                match Environment.GetEnvironmentVariable var with
                | null -> None
                | value when value.Trim() = "" -> None
                | value ->
                    match username with
                    | Some user -> Some(Credential.Userpass(user, value))
                    | None -> Some(Credential.Token value)

            Task.FromResult(Ok result)

/// A `ICredentialProvider` backed by a synchronous closure (see `Credentials.providerFn`).
[<Sealed>]
type FnProvider(f: CredentialRequest -> Result<Credential option, ProcessError>) =
    interface ICredentialProvider with
        member _.Credential(request) = Task.FromResult(f request)

/// The pieces needed to authenticate a `git` HTTPS operation with a `Credential`
/// without putting the secret in argv. See `Credentials.gitCredentialHelper`.
[<Sealed; NoEquality; NoComparison>]
type GitCredentialHelper(configArgs: string list, env: (string * Secret) list) =
    /// `-c key=value` global options to place before the git subcommand. They
    /// reference the secret only by environment-variable name, never by value.
    member _.ConfigArgs = configArgs
    /// Environment variables (name -> value) to set on the command. This is where
    /// the actual secret lives — in the child's environment, not its arguments.
    member _.Env = env

/// Credential helpers: closure-backed providers and git's argv-safe credential helper.
[<RequireQualifiedAccess>]
module Credentials =

    /// The default username git uses when a `Credential` supplies none. GitHub and
    /// GitLab accept any username when the password is a personal-access token.
    [<Literal>]
    let DefaultGitUsername = "x-access-token"

    /// Environment-variable name carrying the username for `gitCredentialHelper`.
    [<Literal>]
    let private GitUsernameVar = "VCS_TOOLKIT_GIT_USERNAME"

    /// Environment-variable name carrying the secret for `gitCredentialHelper`.
    [<Literal>]
    let private GitPasswordVar = "VCS_TOOLKIT_GIT_PASSWORD"

    /// Environment-variable name carrying the expected host for `gitCredentialHelper`'s
    /// host scoping (empty = unscoped).
    [<Literal>]
    let private GitHostVar = "VCS_TOOLKIT_GIT_HOST"

    /// The host (with port, original case) of an `https://` URL — for scoping a credential
    /// helper to the clone URL's host. `None` for a non-https URL, an empty host, or an IPv6
    /// literal (`[::1]:443` — git formats its `host=` request idiosyncratically for those, so
    /// stay unscoped rather than risk withholding a legitimate credential). The scheme is
    /// matched case-INsensitively (git/curl accept `HTTPS://` etc.), so a mixed-case scheme on
    /// an externally-supplied clone URL cannot silently defeat host scoping. The host's port and
    /// case, by contrast, are KEPT: git's `host=` carries them verbatim and the helper compares
    /// byte-for-byte, so only the scheme comparison is case-insensitive.
    let httpsHost (url: string) : string option =
        if not (url.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase)) then
            None
        else
            // "https://" is 8 ASCII chars and OrdinalIgnoreCase is a 1:1 char mapping, so the
            // matched prefix is exactly this length; Substring past it preserves the host bytes.
            // Shared mechanics (`RemoteUrl.authority`) take the authority to the first `/`/`?`/`#`
            // and drop userinfo; the HTTPS-only scheme gate, the `[`-IPv6-authority → `None`
            // policy, and KEEPING the port and host case verbatim are this helper's own.
            let hostPort = RemoteUrl.authority (url.Substring("https://".Length))

            if hostPort = "" || hostPort.StartsWith("[", System.StringComparison.Ordinal) then
                None
            else
                Some hostPort

    /// Adapt a synchronous closure into an `ICredentialProvider`. The closure runs
    /// at request time and returns the credential (or `None` to defer to ambient
    /// auth). For async sources (a network vault), implement `ICredentialProvider`.
    let providerFn (f: CredentialRequest -> Result<Credential option, ProcessError>) : ICredentialProvider =
        FnProvider f :> ICredentialProvider

    /// Build a git `credential.helper` invocation that supplies `cred` over HTTPS while keeping
    /// the secret out of argv. The returned `ConfigArgs` install an inline helper that prints
    /// the credential read from environment variables; the secret value appears only in `Env`.
    /// A leading empty `credential.helper=` first clears any inherited helper. The helper
    /// answers only git's `get` action (so the secret is never written to a cache), and releases
    /// it only when the password is non-empty (else it falls through to ambient auth rather than
    /// overriding it with an empty credential) AND — when `expectHost` is `Some` — the host in
    /// git's request matches, so a redirect/submodule to a **different** host during a clone
    /// never receives the secret. `expectHost = None` leaves it unscoped.
    let gitCredentialHelper (cred: Credential) (expectHost: string option) : GitCredentialHelper =
        let username = defaultArg cred.Username DefaultGitUsername
        // Reference the values by env-var NAME inside the snippet, so argv never carries the
        // secret. Read git's request (key=value lines, blank-line-terminated) to learn the host,
        // then gate on: non-empty password, and unscoped-or-matching host.
        let helper =
            "!f() { test \"$1\" = get || return; h=; "
            + "while IFS= read -r l; do case \"$l\" in \"\") break ;; host=*) h=${l#host=} ;; esac; done; "
            + "test -n \"$"
            + GitPasswordVar
            + "\" || return; "
            + "test -z \"$"
            + GitHostVar
            + "\" || test \"$h\" = \"$"
            + GitHostVar
            + "\" || return; "
            + "printf 'username=%s\\npassword=%s\\n' \"$"
            + GitUsernameVar
            + "\" \"$"
            + GitPasswordVar
            + "\"; }; f"

        GitCredentialHelper(
            [ "-c"; "credential.helper="; "-c"; "credential.helper=" + helper ],
            [ (GitUsernameVar, Secret username)
              (GitPasswordVar, cred.Secret)
              (GitHostVar, Secret(defaultArg expectHost "")) ]
        )
