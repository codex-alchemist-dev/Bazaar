using System.ComponentModel;
using System.Diagnostics;

namespace Bazaar.Server.Setup;

/// <summary>
/// An <see cref="IGitFetcher"/> that runs the <c>git</c> command-line program (doc 45 section 1.8). It needs no forge or git server.
/// The steps are: init an empty repository without templates or hooks, fetch the one pinned commit (shallow first, a full fetch if the
/// remote refuses that), check HEAD is that commit, check the files out without symlinks or filters, then remove the <c>.git</c> folder.
/// </summary>
/// <remarks>
/// The process is run without a shell, with each argument passed separately, with no terminal prompt, no system or user git
/// configuration, no hooks, no submodules and LFS smudging off. <c>GIT_ALLOW_PROTOCOL</c> is the operator's scheme list. Git's own
/// output is read and dropped: it can hold the URL, so it never reaches a message, a log or an issue.
/// </remarks>
public sealed class GitCliFetcher : IGitFetcher
{
    private readonly GitTemplateOptions _options;

    /// <param name="options">The operator's git choices.</param>
    public GitCliFetcher(GitTemplateOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<GitFetchResult> FetchAsync(GitFetchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (GitTemplateRules.CheckUrl(request.Url, _options.AllowedSchemes) is not null
            || !GitTemplateRules.IsCommitId(request.Commit)
            || !Path.IsPathFullyQualified(request.Destination)
            || (Directory.Exists(request.Destination) && Directory.EnumerateFileSystemEntries(request.Destination).Any())
            || File.Exists(request.Destination))
        {
            return new GitFetchResult(GitFetchStatus.InvalidRequest, "the repository URL, commit or destination was refused.");
        }

        using var timeout = new CancellationTokenSource(_options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await RunAsync(request, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GitFetchResult(GitFetchStatus.TimedOut, "the git fetch took longer than the configured limit and was stopped.");
        }
    }

    private async Task<GitFetchResult> RunAsync(GitFetchRequest request, CancellationToken token)
    {
        var destination = Path.TrimEndingDirectorySeparator(request.Destination);
        Directory.CreateDirectory(destination);

        var init = await TryGitAsync(["init", "-q", "--template=", destination], destination, token);
        if (init is null)
        {
            return new GitFetchResult(GitFetchStatus.GitUnavailable, "the git program could not be started.");
        }

        if (init != 0)
        {
            return new GitFetchResult(GitFetchStatus.FetchFailed, "git could not prepare a folder for the template.");
        }

        var shallow = await TryGitAsync(["fetch", "-q", "--depth", "1", "--no-tags", "--no-recurse-submodules", "--", request.Url, request.Commit], destination, token);
        if (shallow != 0)
        {
            // Some remotes refuse a fetch of a bare commit id. Fetch every branch and tag, then look for the commit.
            var full = await TryGitAsync(
                ["fetch", "-q", "--no-recurse-submodules", "--", request.Url, "+refs/heads/*:refs/bazaar/heads/*", "+refs/tags/*:refs/bazaar/tags/*"],
                destination,
                token);
            if (full != 0)
            {
                return new GitFetchResult(GitFetchStatus.FetchFailed, "git could not fetch the template repository.");
            }
        }

        if (await TryGitAsync(["cat-file", "-e", request.Commit + "^{commit}"], destination, token) != 0)
        {
            return new GitFetchResult(GitFetchStatus.CommitNotFound, "the repository does not hold the pinned commit.");
        }

        var checkout = await TryGitAsync(
            ["-c", "core.symlinks=false", "-c", "core.autocrlf=false", "-c", "core.fsmonitor=false", "checkout", "-q", "--force", "--detach", request.Commit],
            destination,
            token);
        if (checkout != 0)
        {
            return new GitFetchResult(GitFetchStatus.FetchFailed, "git could not check out the pinned commit.");
        }

        var head = await CaptureHeadAsync(destination, token);
        if (!string.Equals(head, request.Commit, StringComparison.Ordinal))
        {
            return new GitFetchResult(GitFetchStatus.CommitMismatch, "the checked-out commit is not the pinned one.");
        }

        DeleteGitFolder(Path.Combine(destination, ".git"));
        return new GitFetchResult(GitFetchStatus.Ok, null);
    }

    /// <summary>Runs git and returns its exit code, or null when the program could not be started. Output is read and dropped.</summary>
    private async Task<int?> TryGitAsync(IReadOnlyList<string> arguments, string workingDirectory, CancellationToken token)
    {
        using var process = StartGit(arguments, workingDirectory);
        if (process is null)
        {
            return null;
        }

        try
        {
            var drainOut = process.StandardOutput.ReadToEndAsync(token);
            var drainErr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            await Task.WhenAll(drainOut, drainErr);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }
    }

    private async Task<string?> CaptureHeadAsync(string workingDirectory, CancellationToken token)
    {
        using var process = StartGit(["rev-parse", "--verify", "HEAD"], workingDirectory);
        if (process is null)
        {
            return null;
        }

        try
        {
            var output = process.StandardOutput.ReadToEndAsync(token);
            var drainErr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            await drainErr;
            return process.ExitCode == 0 ? (await output).Trim() : null;
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }
    }

    private Process? StartGit(IReadOnlyList<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(_options.GitExecutable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

        // Settings that must hold for every call, before the sub-command, so a hostile repository cannot loosen them.
        foreach (var setting in new[] { "core.hooksPath=" + nullDevice, "protocol.ext.allow=never", "submodule.recurse=false", "credential.helper=", "core.askPass=" })
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(setting);
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_CONFIG_GLOBAL"] = nullDevice;
        info.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
        info.Environment["GIT_ALLOW_PROTOCOL"] = string.Join(':', _options.AllowedSchemes.Select(s => s.ToLowerInvariant()));
        info.Environment["GIT_ASKPASS"] = "";
        info.Environment["SSH_ASKPASS"] = "";
        info.Environment["LC_ALL"] = "C";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_SSH_COMMAND")))
        {
            info.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        }

        try
        {
            var process = Process.Start(info);
            process?.StandardInput.Close();
            return process;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process ended between the check and the kill.
        }
    }

    /// <summary>Removes the git folder. Pack files are read-only on Windows, so the attribute is cleared first.</summary>
    private static void DeleteGitFolder(string gitFolder)
    {
        if (!Directory.Exists(gitFolder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(gitFolder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(gitFolder, recursive: true);
    }
}
