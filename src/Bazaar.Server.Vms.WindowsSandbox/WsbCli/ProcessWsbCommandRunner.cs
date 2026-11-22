using System.ComponentModel;
using System.Diagnostics;
using System.Text;

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
        var stdout = ReadCappedAsync(process.StandardOutput);
        var stderr = ReadCappedAsync(process.StandardError);

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

    /// <summary>Most characters kept from each of stdout and stderr. The rest is read and dropped so the child never blocks on a full pipe.</summary>
    public const int MaxCapturedChars = 4 * 1024 * 1024;

    private static async Task<string> ReadCappedAsync(TextReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            var room = MaxCapturedChars - text.Length;
            if (room > 0)
            {
                text.Append(buffer, 0, Math.Min(read, room));
            }
        }

        return text.ToString();
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
