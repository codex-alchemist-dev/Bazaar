using System.Diagnostics;

namespace Bazaar.Server.Security.Audit.Anchors;

/// <summary>
/// Runs the system <c>git</c> executable. Credentials come from the environment or from the remote's own configuration
/// (a credential helper, an SSH agent). This class never reads or writes a secret. Prompts are disabled, so a missing
/// credential fails the push instead of waiting for input.
/// </summary>
public sealed class ProcessGitRunner : IGitRunner
{
    /// <inheritdoc />
    public async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);

        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(info) ?? throw new InvalidOperationException("The git executable could not be started.");
        // Stderr is drained so a chatty git cannot block on a full pipe. Its text is never returned, because it can echo remote URLs.
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var registration = ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process already exited.
            }
        });

        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await stderr.ConfigureAwait(false);
        return new GitResult(process.ExitCode, stdout);
    }
}
