using System.Diagnostics;

namespace Bazaar.Server.SealedGit;

/// <summary>Result of one git process.</summary>
public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// A tiny git process runner for callbacks of <see cref="ISealedGitRepo.WithWorkingTreeAsync{T}"/>. Own copy on purpose: modules must not
/// reference Bazaar.Server, where the audit anchor's <c>IGitRunner</c> lives.
/// Hardening: every inherited <c>GIT_*</c> variable is removed first; system and global config are off; hooks are redirected to an empty
/// folder; fsmonitor, auto gc and detached gc are off. Callers must put <see cref="EndOfOptions"/> before any argument that comes from
/// outside (a ref name, a path) so a value that starts with <c>-</c> cannot become an option.
/// </summary>
public static class GitCommand
{
    /// <summary>Put this before user-supplied revisions or paths, for example <c>["log", "--end-of-options", rev]</c>.</summary>
    public const string EndOfOptions = "--end-of-options";

    /// <summary>Returns <c>--</c> followed by the paths, for commands that take pathspecs.</summary>
    public static IReadOnlyList<string> PathSeparated(IEnumerable<string> paths) => ["--", .. paths];

    /// <param name="gitDir">The bare repo folder.</param>
    /// <param name="args">Git arguments (without <c>git</c>).</param>
    /// <param name="workTree">Optional work tree.</param>
    /// <param name="gitPath">Absolute path of the git executable (<see cref="SealedGitOptions.GitPath"/>); null resolves <c>git</c> on PATH.</param>
    /// <param name="cancellationToken">On cancellation the whole git process tree is killed before this returns.</param>
    public static Task<GitResult> RunAsync(string gitDir, IReadOnlyList<string> args, string? workTree = null, string? gitPath = null, CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(gitDir)) ?? Path.GetFullPath(gitDir);
        return RunWithHooksDirAsync(gitDir, args, workTree, gitPath, Path.Combine(parent, ".nohooks"), cancellationToken);
    }

    /// <summary>As <see cref="RunAsync"/>, with the empty hooks folder at <paramref name="noHooks"/> (used where the git dir's parent is a working tree).</summary>
    internal static async Task<GitResult> RunWithHooksDirAsync(string gitDir, IReadOnlyList<string> args, string? workTree, string? gitPath, string noHooks, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(noHooks);

        var psi = new ProcessStartInfo(gitPath ?? "git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            psi.Environment.Remove(key);
        }

        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.hooksPath=" + noHooks);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.fsmonitor=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("gc.auto=0");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("gc.autoDetach=false");
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
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new GitResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}
