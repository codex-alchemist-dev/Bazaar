using Bazaar.Client.Core.Pairing;

namespace Bazaar.Client.Linux.Secrets;

/// <summary>
/// Keeps the refresh credential in the user's Secret Service keyring (doc 30, Linux PAL; doc 32, section 5). The secret travels
/// on stdin only, never in argv, so it does not appear in the process list. A missing keyring entry reads as absent, so the
/// device pairs again; a missing or unreachable keyring throws <see cref="SecretStoreUnavailableException"/> instead of
/// silently losing the credential.
/// </summary>
/// <param name="runner">The <c>secret-tool</c> runner.</param>
/// <param name="serverId">The paired server's identifier, stored as the <c>server</c> attribute so several servers do not collide.</param>
public sealed class SecretServiceRefreshCredentialStore(ISecretToolRunner runner, string serverId) : IRefreshCredentialStore
{
    private const string _application = "bazaar";
    private const string _purpose = "refresh-credential";
    private const int _notFoundExitCode = 1;

    private readonly ISecretToolRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly string _serverId = !string.IsNullOrWhiteSpace(serverId)
        ? serverId
        : throw new ArgumentException("A server identifier is required.", nameof(serverId));

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(["lookup", .. Attributes()], null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == _notFoundExitCode && string.IsNullOrEmpty(result.StandardOutput))
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            throw new SecretStoreUnavailableException($"Reading the refresh credential failed with exit code {result.ExitCode}.");
        }

        // secret-tool prints the secret followed by one newline; the credential itself has no newline.
        return result.StandardOutput.TrimEnd('\n');
    }

    public async Task SaveAsync(string refreshCredential, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshCredential);
        var result = await _runner.RunAsync(
            ["store", "--label", $"Bazaar refresh credential ({_serverId})", .. Attributes()],
            refreshCredential,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new SecretStoreUnavailableException($"Storing the refresh credential failed with exit code {result.ExitCode}.");
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(["clear", .. Attributes()], null, cancellationToken).ConfigureAwait(false);
        // Clearing an absent entry is a success: sign-out must be idempotent.
        if (result.ExitCode != 0 && result.ExitCode != _notFoundExitCode)
        {
            throw new SecretStoreUnavailableException($"Clearing the refresh credential failed with exit code {result.ExitCode}.");
        }
    }

    private string[] Attributes() => ["application", _application, "purpose", _purpose, "server", _serverId];
}
