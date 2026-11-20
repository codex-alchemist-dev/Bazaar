using System.Diagnostics;

namespace Bazaar.Server.SealedGit;

/// <summary>Result of one git process.</summary>
public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// A tiny git process runner for callbacks of <see cref="ISealedGitRepo.WithWorkingTreeAsync{T}"/>. Own copy on purpose: modules must not
/// reference Bazaar.Server, where the audit anchor's <c>IGitRunner</c> lives. Runs with <c>GIT_DIR</c> set and no global or system config.
/// </summary>
public static class GitCommand
{
    public static async Task<GitResult> RunAsync(string gitDir, IReadOnlyList<string> args, string? workTree = null, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["GIT_DIR"] = gitDir;
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (workTree is not null)
        {
            psi.Environment["GIT_WORK_TREE"] = workTree;
            psi.WorkingDirectory = workTree;
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new GitResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}
