using System.Diagnostics;
using System.Text;

namespace Bazaar.Server.DataVolume.Windows;

/// <summary>
/// Runs the few Windows system tools the volume unit needs. The tool is resolved to its absolute path under System32, so a
/// planted executable on PATH or in the current directory is never launched, and only allow-listed names run.
/// </summary>
internal sealed class ProcessCommandRunner : ICommandRunner
{
    private const int _maxOutputChars = 256 * 1024;

    private static readonly Dictionary<string, string> _tools = new(StringComparer.OrdinalIgnoreCase)
    {
        [SystemTools.PowerShell] = Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"),
        [SystemTools.Diskpart] = "diskpart.exe",
        [SystemTools.Icacls] = "icacls.exe",
        [SystemTools.Mountvol] = "mountvol.exe",
    };

    public async ValueTask<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken)
    {
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
            var stdout = ReadCappedAsync(process.StandardOutput, cancellationToken);
            var stderr = ReadCappedAsync(process.StandardError, cancellationToken);
            if (command.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(command.StandardInput.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(cancellationToken);
            return new CommandResult(process.ExitCode, await stdout, await stderr);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            // Keep draining so the child never blocks on a full pipe, but keep only a bounded prefix.
            if (text.Length < _maxOutputChars)
                text.Append(buffer, 0, Math.Min(read, _maxOutputChars - text.Length));
        }

        return text.ToString();
    }
}

/// <summary>The allow-listed system tools.</summary>
internal static class SystemTools
{
    public const string PowerShell = "powershell.exe";
    public const string Diskpart = "diskpart.exe";
    public const string Icacls = "icacls.exe";
    public const string Mountvol = "mountvol.exe";
}
