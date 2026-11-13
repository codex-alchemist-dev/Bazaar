namespace Bazaar.Client.Linux.Secrets;

/// <summary>The outcome of one <c>secret-tool</c> invocation.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="StandardOutput">Everything the tool wrote to stdout.</param>
public sealed record SecretToolResult(int ExitCode, string StandardOutput);

/// <summary>
/// Runs <c>secret-tool</c>, the libsecret command line for the freedesktop Secret Service (GNOME Keyring, KWallet's
/// Secret Service bridge, KeePassXC). Behind an interface so the store can be tested without a keyring.
/// </summary>
public interface ISecretToolRunner
{
    /// <summary>
    /// Runs the tool with <paramref name="arguments"/> as separate argv entries, never through a shell. <paramref name="standardInput"/>
    /// is written to stdin and is the only place a secret value may appear.
    /// </summary>
    /// <exception cref="SecretStoreUnavailableException">The tool is not installed or cannot be started.</exception>
    Task<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput, CancellationToken cancellationToken);
}

/// <summary>Raised when no Secret Service backend can be reached, so credentials cannot be read or written safely.</summary>
public sealed class SecretStoreUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
