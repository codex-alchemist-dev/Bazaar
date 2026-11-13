namespace Bazaar.Conduit.Ssh;

/// <summary>
/// Starts an ssh child process. The real implementation uses an argument list and never a shell. Tests replace it,
/// so no test starts a real process.
/// </summary>
public interface ISshProcessLauncher
{
    /// <summary>Starts the process. Throws when the file cannot be started (for example when it is missing).</summary>
    ISshProcess Start(string fileName, IReadOnlyList<string> arguments);
}
