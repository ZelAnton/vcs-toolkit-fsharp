module VcsToolkit.Gitea.LiveTests

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading.Tasks
open NUnit.Framework
open VcsToolkit.Forge

// Live integration tests for the Gitea forge, exercised against a real, containerised Gitea
// server rather than a scripted-CLI fake. The whole suite is OPT-IN behind the
// `REQUIRE_GITEA_LIVE` environment gate (modelled on the `REQUIRE_JJ` gate, K-034): with the
// gate unset the fixture's one-time setup calls `Assert.Ignore`, so an ordinary `dotnet test`
// run — and any machine without Docker — never spins up a container and is never slowed or
// failed. Under the gate the real `docker` daemon and the real `tea` CLI must be present; a
// missing one is `Assert.Fail`, so a green CI run genuinely proves the live binaries ran
// (K-007/K-034) instead of silently skipping. The forge operations spawn the real `tea`
// binary, which is exactly the point: tea's print-table parser is the project's most fragile
// (no JSON REST shape), and only a live run catches format drift when tea/Gitea are upgraded.
//
// Isolation of `tea`'s login store is via `XDG_CONFIG_HOME` (honoured by tea on Linux — the
// CI target — and pointed at a throwaway directory); on Windows/macOS local runs tea falls
// back to its ambient config location, so the login is also removed in teardown.

/// Abbreviation for the raw Gitea client type — `VcsToolkit.Gitea` is deliberately not
/// `open`ed here because several of its DTO names (`PrCreate`, `MergeStrategy`, …) collide
/// with the `VcsToolkit.Forge` ones this file uses.
type private GiteaClient = VcsToolkit.Gitea.Gitea

/// Captured result of a child process (never throws on a non-zero exit — the caller decides).
type private ProcOutput =
    { ExitCode: int
      StdOut: string
      StdErr: string }

/// Run `fileName args` to completion, capturing stdout/stderr. `env` overrides are layered on
/// the inherited environment; `workDir` sets the working directory when `Some`. Raises only on
/// a genuine spawn failure (missing binary) — a non-zero exit is reported in `ExitCode`.
let private startProc
    (fileName: string)
    (args: string list)
    (env: (string * string) list)
    (workDir: string option)
    : ProcOutput =
    let psi =
        ProcessStartInfo(
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        )

    for a in args do
        psi.ArgumentList.Add a

    for (key, value) in env do
        psi.Environment[key] <- value

    match workDir with
    | Some dir -> psi.WorkingDirectory <- dir
    | None -> ()

    use proc =
        match Process.Start psi with
        | null -> failwithf "failed to start process '%s'" fileName
        | started -> started

    // Read both pipes concurrently before waiting, so a chatty child cannot deadlock on a
    // full stdout/stderr buffer.
    let stdoutTask = proc.StandardOutput.ReadToEndAsync()
    let stderrTask = proc.StandardError.ReadToEndAsync()
    proc.WaitForExit()

    { ExitCode = proc.ExitCode
      StdOut = stdoutTask.Result
      StdErr = stderrTask.Result }

/// Whether `fileName args` runs and exits zero — the availability probe for `docker`/`tea`.
/// Probing `docker version --format {{.Server.Version}}` also confirms a *running daemon*,
/// not just the client binary on PATH (K-007: a present binary is not a working service).
let private commandAvailable (fileName: string) (args: string list) : bool =
    try
        (startProc fileName args [] None).ExitCode = 0
    with _ ->
        // Any spawn/IO failure (binary absent, daemon socket unreachable) means we can't use
        // it — the gate logic turns this into Ignore/Fail; nothing to recover here.
        false

/// Remove a directory tree, swallowing the transient failures that must not fail a test run.
let private deleteDirBestEffort (dir: string) =
    if dir <> "" && Directory.Exists dir then
        try
            Directory.Delete(dir, true)
        with
        | :? IOException ->
            // A file handle may still be held (Windows); the OS reclaims the temp dir later.
            ()
        | :? UnauthorizedAccessException ->
            // Transient access denial while a handle is released; cleanup must not fail the run.
            ()

/// A fresh, unique throwaway directory under the OS temp root.
let private newTempDir (tag: string) : string =
    let dir =
        Path.Combine(
            Path.GetTempPath(),
            sprintf "vcs-gitea-live-%s-%d-%s" tag (Environment.ProcessId) (Guid.NewGuid().ToString("N"))
        )

    Directory.CreateDirectory dir |> ignore
    dir

/// The host port Docker mapped to the container's port 3000 (`docker port <name> 3000` prints
/// `host:port`, one line per address family — take the first).
let private mappedPort (containerName: string) : int =
    let result = startProc "docker" [ "port"; containerName; "3000" ] [] None

    if result.ExitCode <> 0 then
        failwithf "`docker port` failed (%d): stdout=%s stderr=%s" result.ExitCode result.StdOut result.StdErr

    let firstLine =
        result.StdOut.Split('\n')
        |> Array.map (fun line -> line.Trim())
        |> Array.filter (fun line -> line <> "")
        |> Array.tryHead

    match firstLine with
    | None -> failwithf "`docker port` produced no mapping: %s" result.StdOut
    | Some line ->
        let portText = line.Substring(line.LastIndexOf(':') + 1)

        match Int32.TryParse portText with
        | true, value -> value
        | _ -> failwithf "could not parse a mapped port from `%s`" line

/// The `Authorization: Basic <base64(user:pass)>` header for the token-minting call.
let private basicAuth (user: string) (password: string) : AuthenticationHeaderValue =
    let raw =
        Convert.ToBase64String(Encoding.ASCII.GetBytes(sprintf "%s:%s" user password))

    AuthenticationHeaderValue("Basic", raw)

/// The `Authorization: token <token>` header Gitea accepts for a personal access token.
let private tokenAuth (token: string) : AuthenticationHeaderValue =
    AuthenticationHeaderValue("token", token)

/// POST a JSON body to a Gitea API `url`, returning the response body; raises on a non-2xx.
let private apiPost (http: HttpClient) (url: string) (auth: AuthenticationHeaderValue) (json: string) : Task<string> =
    task {
        use request = new HttpRequestMessage(HttpMethod.Post, url)
        request.Headers.Authorization <- auth
        request.Content <- new StringContent(json, Encoding.UTF8, "application/json")
        use! response = http.SendAsync request
        let! body = response.Content.ReadAsStringAsync()

        if not response.IsSuccessStatusCode then
            failwithf "POST %s -> HTTP %d: %s" url (int response.StatusCode) body

        return body
    }

/// Poll `GET /api/v1/version` until the container answers, or fail after `timeout`.
let private waitForReady (http: HttpClient) (baseUrl: string) (timeout: TimeSpan) : Task =
    task {
        let deadline = DateTime.UtcNow.Add timeout
        let mutable ready = false

        while not ready && DateTime.UtcNow < deadline do
            let! reachable =
                task {
                    try
                        use! response = http.GetAsync(baseUrl + "/api/v1/version")
                        return response.IsSuccessStatusCode
                    with _ ->
                        // Connection refused/reset while Gitea is still booting — keep polling
                        // until the deadline; only a persistent failure ends the wait below.
                        return false
                }

            if reachable then
                ready <- true
            else
                do! Task.Delay(TimeSpan.FromSeconds 1.0)

        if not ready then
            failwithf "Gitea did not become ready at %s within %O" baseUrl timeout
    }

/// Mint a personal access token for `user` (basic-auth), returning its `sha1` secret. The
/// scopes cover repository/issue/user writes — enough for repo, PR, issue and release ops.
let private createToken
    (http: HttpClient)
    (baseUrl: string)
    (user: string)
    (password: string)
    (tokenName: string)
    : Task<string> =
    task {
        let url = sprintf "%s/api/v1/users/%s/tokens" baseUrl user

        let json =
            sprintf
                """{"name":"%s","scopes":["write:repository","write:issue","write:user","read:user","write:organization"]}"""
                tokenName

        let! body = apiPost http url (basicAuth user password) json
        use doc = JsonDocument.Parse body

        match doc.RootElement.GetProperty("sha1").GetString() with
        | null -> return failwith "token response did not carry a `sha1` field"
        | sha1 -> return sha1
    }

/// Create the auto-initialised, public working repository under the authenticated user.
let private createRepo (http: HttpClient) (baseUrl: string) (token: string) (repo: string) : Task =
    task {
        let url = sprintf "%s/api/v1/user/repos" baseUrl

        let json =
            sprintf """{"name":"%s","auto_init":true,"private":false,"default_branch":"main"}""" repo

        let! _ = apiPost http url (tokenAuth token) json
        return ()
    }

/// Create `newBranch` off `baseBranch` with a single new file — a server-side commit that
/// gives a subsequent PR a real diff, no local git push required.
let private createFileOnBranch
    (http: HttpClient)
    (baseUrl: string)
    (token: string)
    (owner: string)
    (repo: string)
    (path: string)
    (content: string)
    (baseBranch: string)
    (newBranch: string)
    (message: string)
    : Task =
    task {
        let url = sprintf "%s/api/v1/repos/%s/%s/contents/%s" baseUrl owner repo path
        let encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes content)

        let json =
            sprintf
                """{"content":"%s","branch":"%s","new_branch":"%s","message":"%s"}"""
                encoded
                baseBranch
                newBranch
                message

        let! _ = apiPost http url (tokenAuth token) json
        return ()
    }

/// Publish a release (Gitea creates the tag from `main` if it does not exist yet).
let private createRelease
    (http: HttpClient)
    (baseUrl: string)
    (token: string)
    (owner: string)
    (repo: string)
    (tag: string)
    (name: string)
    : Task =
    task {
        let url = sprintf "%s/api/v1/repos/%s/%s/releases" baseUrl owner repo

        let json =
            sprintf """{"tag_name":"%s","target_commitish":"main","name":"%s","body":"live release"}""" tag name

        let! _ = apiPost http url (tokenAuth token) json
        return ()
    }

/// A minimal local git repo whose `origin` points at the Gitea repo — `tea` reads the owner/
/// repo from this remote (it never fetches, so no credentials and no network are needed).
let private initRepoWithRemote (dir: string) (remoteUrl: string) : unit =
    Directory.CreateDirectory dir |> ignore

    let git (args: string list) =
        let result = startProc "git" args [ "GIT_TERMINAL_PROMPT", "0" ] (Some dir)

        if result.ExitCode <> 0 then
            failwithf
                "`git %s` failed (%d): stdout=%s stderr=%s"
                (String.Join(" ", args))
                result.ExitCode
                result.StdOut
                result.StdErr

    git [ "init"; "-q"; "-b"; "main" ]
    git [ "remote"; "add"; "origin"; remoteUrl ]

/// Fail a test with `message` (typed to fit any branch — `Assert.Fail` always throws).
let private failTest (message: string) : 'a =
    Assert.Fail message
    failwith "unreachable: Assert.Fail always throws"

/// Unwrap an `Ok`, failing the test (with `label`) on an `Error` — works over both the raw
/// client's `ProcessError` and the facade's `ForgeError`.
let private expectOk (label: string) (result: Result<'T, 'E>) : 'T =
    match result with
    | Ok value -> value
    | Error err -> failTest (sprintf "%s failed: %A" label err)

[<TestFixture>]
[<Category("GiteaLive")>]
type GiteaLiveTests() =

    let http = new HttpClient()
    let mutable containerName = ""
    let mutable teaConfigDir = ""
    let mutable repoParent = ""
    let mutable clonePath = ""
    let mutable baseUrl = ""
    let mutable owner = ""
    let mutable token = ""
    let mutable repoName = ""
    let mutable loginName = ""

    /// A raw Gitea client wired to the isolated `tea` login store.
    let makeClient () =
        GiteaClient.Create().DefaultEnv("XDG_CONFIG_HOME", teaConfigDir)

    [<OneTimeSetUp>]
    member _.OneTimeSetUp() : Task =
        task {
            if Environment.GetEnvironmentVariable "REQUIRE_GITEA_LIVE" <> "1" then
                // Opt-in suite: an unset gate skips it entirely (no container is started), so the
                // ordinary test run and Docker-less machines are never slowed or failed.
                Assert.Ignore
                    "REQUIRE_GITEA_LIVE is not set — skipping the live Gitea container suite (opt-in; run it via CI's gitea-live job, or set REQUIRE_GITEA_LIVE=1 locally with Docker and the tea CLI available)."

            // Under the gate the real Docker daemon and the real `tea` CLI must be present: a
            // green run has to prove the live binaries actually ran, not silently skip
            // (K-007/K-034).
            if not (commandAvailable "docker" [ "version"; "--format"; "{{.Server.Version}}" ]) then
                Assert.Fail "REQUIRE_GITEA_LIVE=1 but Docker is not available (a running Docker daemon is required)."

            if not (commandAvailable "tea" [ "--version" ]) then
                Assert.Fail "REQUIRE_GITEA_LIVE=1 but the `tea` CLI is not on PATH."

            http.Timeout <- TimeSpan.FromSeconds 30.0

            let unique = Guid.NewGuid().ToString("N").Substring(0, 8)
            containerName <- sprintf "vcs-gitea-live-%d-%s" (Environment.ProcessId) unique
            loginName <- sprintf "vcs-live-%s" unique
            owner <- "vcsadmin"
            repoName <- "live-repo"
            let password = "Vcs-Live-Passw0rd!"

            let image =
                match Environment.GetEnvironmentVariable "GITEA_IMAGE" with
                | null
                | "" -> "gitea/gitea:1.22"
                | value -> value

            let runResult =
                startProc
                    "docker"
                    [ "run"
                      "-d"
                      "--name"
                      containerName
                      "-p"
                      "127.0.0.1:0:3000"
                      "-e"
                      "GITEA__security__INSTALL_LOCK=true"
                      "-e"
                      "GITEA__database__DB_TYPE=sqlite3"
                      "-e"
                      "GITEA__server__OFFLINE_MODE=true"
                      "-e"
                      "GITEA__log__LEVEL=error"
                      image ]
                    []
                    None

            if runResult.ExitCode <> 0 then
                failwithf
                    "`docker run` failed (%d): stdout=%s stderr=%s"
                    runResult.ExitCode
                    runResult.StdOut
                    runResult.StdErr

            let port = mappedPort containerName
            baseUrl <- sprintf "http://localhost:%d" port

            do! waitForReady http baseUrl (TimeSpan.FromSeconds 120.0)

            let adminResult =
                startProc
                    "docker"
                    // `-u git`: the official gitea/gitea image runs Gitea (and owns its data/
                    // config) as the `git` user; `docker exec` defaults to the image's configured
                    // user, which does not reliably resolve to `git`, so `gitea admin user create`
                    // can fail to read the app config / open the sqlite DB. Pin the exec user.
                    [ "exec"
                      "-u"
                      "git"
                      containerName
                      "gitea"
                      "admin"
                      "user"
                      "create"
                      "--username"
                      owner
                      "--password"
                      password
                      "--email"
                      "vcsadmin@example.com"
                      "--admin"
                      "--must-change-password=false" ]
                    []
                    None

            if adminResult.ExitCode <> 0 then
                failwithf
                    "`gitea admin user create` failed (%d): stdout=%s stderr=%s"
                    adminResult.ExitCode
                    adminResult.StdOut
                    adminResult.StdErr

            let! createdToken = createToken http baseUrl owner password loginName
            token <- createdToken

            do! createRepo http baseUrl token repoName

            teaConfigDir <- newTempDir "tea-config"

            let loginResult =
                startProc
                    "tea"
                    [ "login"; "add"; "--name"; loginName; "--url"; baseUrl; "--token"; token ]
                    [ "XDG_CONFIG_HOME", teaConfigDir ]
                    None

            if loginResult.ExitCode <> 0 then
                failwithf
                    "`tea login add` failed (%d): stdout=%s stderr=%s"
                    loginResult.ExitCode
                    loginResult.StdOut
                    loginResult.StdErr

            repoParent <- newTempDir "workdir"
            clonePath <- Path.Combine(repoParent, repoName)
            initRepoWithRemote clonePath (sprintf "%s/%s/%s.git" baseUrl owner repoName)
        }

    [<OneTimeTearDown>]
    member _.OneTimeTearDown() =
        if loginName <> "" && teaConfigDir <> "" then
            try
                startProc "tea" [ "logout"; loginName ] [ "XDG_CONFIG_HOME", teaConfigDir ] None
                |> ignore
            with _ ->
                // Best-effort: the login may never have been added if setup failed early.
                ()

        if containerName <> "" then
            try
                startProc "docker" [ "rm"; "-f"; containerName ] [] None |> ignore
            with _ ->
                // Best-effort: a leaked container must not fail the run; remove it by hand if
                // this ever misses.
                ()

        deleteDirBestEffort teaConfigDir
        deleteDirBestEffort repoParent
        http.Dispose()

    [<Test>]
    member _.AuthStatusReportsAConfiguredLogin() : Task =
        task {
            let client = makeClient ()

            let! clientAuth = client.At(clonePath).AuthStatus()

            Assert.That(
                expectOk "AuthStatus (client)" clientAuth,
                Is.True,
                "the raw client should see the configured tea login"
            )

            let forge = Forge.FromGitea(clonePath, client)
            let! forgeAuth = forge.AuthStatus()

            Assert.That(
                expectOk "AuthStatus (forge)" forgeAuth,
                Is.True,
                "the forge facade should see the configured tea login"
            )
        }

    [<Test>]
    member _.PullRequestLifecycleRunsThroughTheForgeFacade() : Task =
        task {
            let branch = sprintf "feature-%s" (Guid.NewGuid().ToString("N").Substring(0, 8))

            do!
                createFileOnBranch
                    http
                    baseUrl
                    token
                    owner
                    repoName
                    (branch + ".txt")
                    (sprintf "content for %s\n" branch)
                    "main"
                    branch
                    (sprintf "Add %s" branch)

            let client = makeClient ()
            let forge = Forge.FromGitea(clonePath, client)

            let spec =
                PrCreate.Create(sprintf "Live PR %s" branch, "PR body").WithSource(branch).WithTarget "main"

            let! created = forge.PrCreate spec
            expectOk "PrCreate" created |> ignore

            let! listed = forge.PrList()
            let prs = expectOk "PrList" listed

            let number =
                match prs |> List.tryFind (fun pr -> pr.SourceBranch = branch) with
                | Some pr -> pr.Number
                | None -> failTest (sprintf "the created PR for %s was not found among %d listed PRs" branch prs.Length)

            let! opened = forge.PrView number
            let openedPr = expectOk "PrView" opened

            Assert.That(
                openedPr.State = ForgePrState.Open,
                Is.True,
                sprintf "expected an open PR, got %A" openedPr.State
            )

            let! commented = forge.PrComment(number, "Live review comment from the integration suite.")
            expectOk "PrComment" commented |> ignore

            let! merged = forge.PrMerge(number, PrMerge.Merge)
            expectOk "PrMerge" merged |> ignore

            let! afterMerge = forge.PrView number
            let mergedPr = expectOk "PrView (post-merge)" afterMerge

            Assert.That(
                mergedPr.State = ForgePrState.Merged,
                Is.True,
                sprintf "expected a merged PR, got %A" mergedPr.State
            )
        }

    [<Test>]
    member _.IssueLifecycleRunsThroughTheGiteaClient() : Task =
        task {
            let client = makeClient().At clonePath
            let title = sprintf "Live issue %s" (Guid.NewGuid().ToString("N").Substring(0, 8))

            let! created = client.IssueCreate(title, "Issue body from the integration suite.")
            expectOk "IssueCreate" created |> ignore

            let! listed = client.IssueList()
            let issues = expectOk "IssueList" listed

            let number =
                match
                    issues
                    |> List.tryFind (fun (issue: VcsToolkit.Gitea.Issue) -> issue.Title = title)
                with
                | Some issue -> issue.Number
                | None -> failTest (sprintf "the created issue '%s' was not found in IssueList" title)

            let! viewed = client.IssueView number
            let openedIssue = expectOk "IssueView" viewed

            Assert.That(
                openedIssue.State.Equals("open", StringComparison.OrdinalIgnoreCase),
                Is.True,
                sprintf "expected an open issue, got '%s'" openedIssue.State
            )

            let! commented = client.IssueComment(number, "Live issue comment.")
            expectOk "IssueComment" commented |> ignore

            let! closed = client.IssueClose number
            expectOk "IssueClose" closed |> ignore

            let! afterClose = client.IssueView number
            let closedIssue = expectOk "IssueView (post-close)" afterClose

            Assert.That(
                closedIssue.State.Equals("closed", StringComparison.OrdinalIgnoreCase),
                Is.True,
                sprintf "expected a closed issue, got '%s'" closedIssue.State
            )
        }

    [<Test>]
    member _.ReleaseListReturnsAPublishedRelease() : Task =
        task {
            let tag = sprintf "live-%s" (Guid.NewGuid().ToString("N").Substring(0, 8))
            do! createRelease http baseUrl token owner repoName tag (sprintf "Release %s" tag)

            let client = makeClient ()

            let! clientReleases = client.At(clonePath).ReleaseList()
            let releasesViaClient = expectOk "ReleaseList (client)" clientReleases

            Assert.That(
                releasesViaClient
                |> List.exists (fun (release: VcsToolkit.Gitea.Release) -> release.Tag = tag),
                Is.True,
                sprintf "the raw client's ReleaseList should contain %s" tag
            )

            let forge = Forge.FromGitea(clonePath, client)
            let! forgeReleases = forge.ReleaseList()
            let releasesViaForge = expectOk "ReleaseList (forge)" forgeReleases

            Assert.That(
                releasesViaForge |> List.exists (fun release -> release.Tag = tag),
                Is.True,
                sprintf "the forge's ReleaseList should contain %s" tag
            )
        }
