using System.Diagnostics;

namespace Bazaar.Conduit.Ssh;

/// <summary>
/// Launches ssh with <see cref="System.Diagnostics.Process"/>. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>,
/// so no shell and no string concatenation is involved. Standard input is closed at once so ssh never waits on a prompt.
/// </summary>
public sealed class SystemSshProcessLauncher : ISshProcessLauncher
{
    /// <inheritdoc />
    public ISshProcess Start(string fileName, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("ssh did not start.");
        return new SystemSshProcess(process);
    }
}
