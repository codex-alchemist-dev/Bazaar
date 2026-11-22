using System.ComponentModel;
using System.Diagnostics;

namespace Bazaar.Server.Vms.WindowsSandbox;

/// <summary>
/// Runs <c>hcsdiag list</c> with <see cref="Process"/>. <c>hcsdiag</c> needs an administrator or a Hyper-V Administrators member, while
/// <c>wsb start</c> fails from an elevated process (spike S-WSB step 9), so the server stays non-elevated and may run this one call through
/// an elevation prefix such as <c>sudo</c>: <c>new ProcessHcsDiagRunner("sudo.exe", ["hcsdiag.exe"])</c>. Without privilege the lookup fails closed.
/// </summary>
/// <param name="executable">The program to start.</param>
/// <param name="leadingArguments">Arguments placed before <c>list</c>, for example the real <c>hcsdiag.exe</c> behind <c>sudo</c>.</param>
public sealed class ProcessHcsDiagRunner(string executable = "hcsdiag.exe", IReadOnlyList<string>? leadingArguments = null) : IHcsDiagRunner
{
    /// <inheritdoc />
    public async Task<string> ListAsync(CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in leadingArguments ?? [])
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("list");

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new WindowsSandboxVmIdException($"Could not start '{executable}'.");
        }
        catch (Win32Exception ex)
        {
            throw new WindowsSandboxVmIdException($"Could not start '{executable}': {ex.Message}");
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
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

                throw;
            }

            if (process.ExitCode != 0)
            {
                throw new WindowsSandboxVmIdException($"hcsdiag list failed with exit code {process.ExitCode}: {(await stderr).Trim()}");
            }

            return await stdout.ConfigureAwait(false);
        }
    }
}
