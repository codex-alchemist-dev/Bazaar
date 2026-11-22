using System.Diagnostics;
using System.Text;

namespace Bazaar.Server.VmMounts.Providers.Smb;

/// <summary>One process to run. Arguments are an argument list, never a shell string.</summary>
/// <param name="Tool">A tool name from the allow-list of <see cref="ProcessHostCommandRunner"/>.</param>
/// <param name="Arguments">The argument list.</param>
/// <param name="StandardInput">Text written to the process's stdin and then closed. Parameters of a constant script travel here.</param>
internal sealed record HostCommand(string Tool, IReadOnlyList<string> Arguments, string? StandardInput = null);

internal sealed record HostCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Launches an allow-listed system tool. The seam that keeps process launching out of every other class and lets tests run on Linux.</summary>
internal interface IHostCommandRunner
{
    ValueTask<HostCommandResult> RunAsync(HostCommand command, CancellationToken ct);
}

/// <summary>The allow-listed host tools.</summary>
internal static class HostTools
{
    public const string PowerShell = "powershell.exe";
}

/// <summary>
/// Runs <c>powershell.exe</c> for the SMB scripts. The tool is resolved to its absolute path under System32, so a planted executable on
/// PATH or in the current directory is never launched, and only allow-listed names run. Windows only: on any other OS it refuses.
/// </summary>
internal sealed class ProcessHostCommandRunner : IHostCommandRunner
{
    private const int _maxOutputChars = 256 * 1024;

    private static readonly Dictionary<string, string> _tools = new(StringComparer.OrdinalIgnoreCase)
    {
        [HostTools.PowerShell] = Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"),
    };

    public async ValueTask<HostCommandResult> RunAsync(HostCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The SMB scripts run on a Windows host only.");
        if (!_tools.TryGetValue(command.Tool, out var relative))
            throw new ArgumentException($"The tool '{command.Tool}' is not allowed.", nameof(command));

        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), relative))
        {
            RedirectStandardInput = command.StandardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in command.Arguments)
            info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        process.Start();
        try
        {
            var stdout = ReadCappedAsync(process.StandardOutput, ct);
            var stderr = ReadCappedAsync(process.StandardError, ct);
            if (command.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(command.StandardInput.AsMemory(), ct).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new HostCommandResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            // Keep draining so the child never blocks on a full pipe, but keep only a bounded prefix.
            if (text.Length < _maxOutputChars)
                text.Append(buffer, 0, Math.Min(read, _maxOutputChars - text.Length));
        }

        return text.ToString();
    }
}
