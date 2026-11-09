using System.Diagnostics;

namespace Bazaar.Server.Transports.Cloudflared;

/// <summary>
/// Launches cloudflared with <see cref="System.Diagnostics.Process"/>. Arguments go through
/// <see cref="ProcessStartInfo.ArgumentList"/>, so no shell and no string concatenation is involved.
/// </summary>
public sealed class SystemCloudflaredLauncher : ICloudflaredLauncher
{
    /// <inheritdoc />
    public ICloudflaredProcess Start(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);

        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("cloudflared did not start.");
        return new SystemCloudflaredProcess(process);
    }
}
