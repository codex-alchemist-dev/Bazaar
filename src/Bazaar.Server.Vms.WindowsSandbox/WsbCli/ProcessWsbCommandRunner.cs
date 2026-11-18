using System.ComponentModel;
using System.Diagnostics;

namespace Bazaar.Server.Vms.WindowsSandbox.WsbCli;

/// <summary>Runs <c>wsb.exe</c> with <see cref="Process"/>. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, so nothing is shell-parsed.</summary>
public sealed class ProcessWsbCommandRunner(string executable = "wsb.exe") : IWsbCommandRunner
{
    /// <inheritdoc />
    public async Task<WsbCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
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

        return new WsbCommandResult(process.ExitCode, await stdout, await stderr);
    }

    private Process StartProcess(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new WindowsSandboxCliException($"Could not start '{executable}'.");
        }
        catch (Win32Exception ex)
        {
            throw new WindowsSandboxCliException($"Could not start '{executable}': {ex.Message}", ex);
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
