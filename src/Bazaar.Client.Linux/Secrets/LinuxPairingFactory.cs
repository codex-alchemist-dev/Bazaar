using Bazaar.Client.Core.Pairing;

namespace Bazaar.Client.Linux.Secrets;

/// <summary>
/// Builds the pairing flow for the Linux client with the Secret Service keyring as its credential store (doc 30, doc 32 section 5).
/// Pure construction: no keyring call happens until the flow is used, so starting the app never blocks on the keyring.
/// </summary>
public static class LinuxPairingFactory
{
    /// <summary>
    /// Returns a <see cref="PairingFlow"/> that stores the refresh credential under <paramref name="serverId"/>. The
    /// <paramref name="clientFor"/> factory supplies the HTTP client for a server address, as <see cref="PairingFlow"/> expects.
    /// </summary>
    public static PairingFlow Create(string serverId, Func<Uri, PairingClient> clientFor, ISecretToolRunner? runner = null)
    {
        ArgumentNullException.ThrowIfNull(clientFor);
        var store = new SecretServiceRefreshCredentialStore(runner ?? new ProcessSecretToolRunner(), serverId);
        return new PairingFlow(store, clientFor);
    }

    /// <summary>
    /// True when <c>secret-tool</c> is found on <c>PATH</c>. The UI uses this to explain a missing keyring before the first pairing
    /// attempt, instead of failing at the moment the credential is saved. Pure except for reading <paramref name="path"/>.
    /// </summary>
    public static bool IsSecretToolAvailable(string? path, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (fileExists(Path.Combine(directory, "secret-tool")))
            {
                return true;
            }
        }

        return false;
    }
}
