using System.ComponentModel;
using System.Diagnostics;

namespace Bazaar.Server.Vms.WindowsSandbox;

/// <summary>Runs <c>hcsdiag list</c> with <see cref="Process"/>. It needs an elevated server process (spike S-WSB step 9); without it the lookup fails closed.</summary>
public sealed class ProcessHcsDiagRunner(string executable = "hcsdiag.exe") : IHcsDiagRunner
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
