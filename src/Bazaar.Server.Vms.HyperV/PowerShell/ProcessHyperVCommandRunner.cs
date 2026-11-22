using System.ComponentModel;
using System.Diagnostics;

namespace Bazaar.Server.Vms.HyperV.PowerShell;

/// <summary>
/// Runs <c>powershell.exe</c> (Windows PowerShell 5.1, which ships the Hyper-V module) with <see cref="Process"/>. Arguments go through
/// <see cref="ProcessStartInfo.ArgumentList"/>, so nothing is shell-parsed. A cancelled call kills the whole process tree.
/// </summary>
public sealed class ProcessHyperVCommandRunner(string executable = "powershell.exe") : IHyperVCommandRunner
{
    /// <inheritdoc />
    public async Task<HyperVCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = StartProcess(startInfo);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        return new HyperVCommandResult(process.ExitCode, await stdout, await stderr);
    }

    private Process StartProcess(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new HyperVCommandException($"Could not start '{executable}'.");
        }
        catch (Win32Exception ex)
        {
            throw new HyperVCommandException($"Could not start '{executable}': {ex.Message}", ex);
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
