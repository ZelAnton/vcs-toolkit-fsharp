module VcsToolkit.Git.ListFilesIntegrationTests

open System
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open VcsToolkit.Git
open VcsToolkit.TestKit

[<TestFixture>]
type ListFilesTests() =

    let requireGit () =
        try
            Raw.git "." [ "--version" ]
        with _ ->
            Assert.Ignore "git not available on PATH"

    [<Test>]
    member _.PreservesCrLfInAUnixFilenameForWorkingCopyAndRevisionListings() : Task =
        task {
            if OperatingSystem.IsWindows() then
                Assert.Ignore "Windows filenames cannot contain CR or LF characters"

            requireGit ()
            use repo = GitSandbox.Init "list-files-crlf"
            let path = "literal\r\nline-break.txt"
            repo.CommitFile(path, "content\n", "add CRLF filename")
            let git = Git.Create()

            match! git.ListFiles(repo.Path, None) with
            | Error e -> Assert.Fail $"working-copy ListFiles failed: {e}"
            | Ok paths -> Assert.That(paths, Does.Contain path, "the literal CRLF must round-trip unchanged")

            match! git.ListFiles(repo.Path, Some "HEAD") with
            | Error e -> Assert.Fail $"revision ListFiles failed: {e}"
            | Ok paths -> Assert.That(paths, Does.Contain path, "the literal CRLF must round-trip unchanged")
        }

    [<Test>]
    member _.GitAtListFilesMatchesTheRootListingWhenBoundToASubdirectory() : Task =
        task {
            requireGit ()
            use repo = GitSandbox.Init "list-files-at-root"
            repo.CommitFile("sub/a.txt", "a\n", "add nested file")
            repo.CommitFile("top.txt", "top\n", "add root file")
            let git = Git.Create()
            let subdir = Path.Combine(repo.Path, "sub")

            let! rootResult = git.ListFiles(repo.Path, None)
            let! boundResult = git.At(subdir).ListFiles None

            match rootResult, boundResult with
            | Ok rootPaths, Ok boundPaths ->
                Assert.That((boundPaths = rootPaths), Is.True)
                Assert.That(boundPaths, Does.Contain "sub/a.txt")
                Assert.That(boundPaths, Does.Contain "top.txt")
                Assert.That(boundPaths, Does.Not.Contain "a.txt")
            | Error e, _ -> Assert.Fail $"root ListFiles failed: {e}"
            | _, Error e -> Assert.Fail $"bound ListFiles failed: {e}"
        }
