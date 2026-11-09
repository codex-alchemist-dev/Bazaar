namespace Bazaar.Server.Transports.Cloudflared;

/// <summary>
/// Starts a cloudflared child process. The real implementation uses an argument list and never a shell. Tests
/// replace it, so no test starts a real process.
/// </summary>
public interface ICloudflaredLauncher
{
    /// <summary>Starts the process. Throws when the file cannot be started (for example when it is missing).</summary>
    ICloudflaredProcess Start(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment);
}
