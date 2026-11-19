using System.Security.Cryptography;

namespace Bazaar.Secrets.Keyring;

/// <summary>
/// KEK held in a Secret Service keyring (Linux). The 32-byte key is created on first use and read back on later starts,
/// stored under attributes <c>service=bazaar-kek</c>, <c>account=&lt;keyId&gt;</c>. Wrapping then runs in process via
/// <see cref="SoftwareKekProvider"/>, so the keyring holds custody and the hot path does not call out.
/// Only a clean "no such item" answer creates a key. Any other failure throws, because creating a key over an
/// unreadable one would orphan every scope wrapped under it.
/// </summary>
public sealed class SecretServiceKekProvider : IKekProvider, IDisposable
{
    private const string _service = "bazaar-kek";
    private const int _keyBytes = 32;

    private readonly SoftwareKekProvider _inner;

    private SecretServiceKekProvider(SoftwareKekProvider inner) => _inner = inner;

    public string CurrentKeyId => _inner.CurrentKeyId;

    public Task<ReadOnlyMemory<byte>> WrapAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken ct) =>
        _inner.WrapAsync(key, associatedData, ct);

    public Task<ReadOnlyMemory<byte>> UnwrapAsync(string keyId, ReadOnlyMemory<byte> wrapped, ReadOnlyMemory<byte> associatedData, CancellationToken ct) =>
        _inner.UnwrapAsync(keyId, wrapped, associatedData, ct);

    public void Dispose() => _inner.Dispose();

    /// <summary>Opens the KEK for <paramref name="keyId"/>, creating it in the keyring when there is no item yet.</summary>
    /// <exception cref="KekCustodyException">The keyring is unavailable, locked, or holds a malformed key.</exception>
    public static async Task<SecretServiceKekProvider> OpenAsync(ISecretToolRunner runner, string keyId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        var key = await LoadOrCreateAsync(runner, keyId, ct).ConfigureAwait(false);
        try
        {
            return new SecretServiceKekProvider(new SoftwareKekProvider(keyId, key));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task<byte[]> LoadOrCreateAsync(ISecretToolRunner runner, string keyId, CancellationToken ct)
    {
        var existing = await LookupAsync(runner, keyId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var fresh = RandomNumberGenerator.GetBytes(_keyBytes);
        try
        {
            var stored = await runner.RunAsync(
                ["store", $"--label=Bazaar KEK {keyId}", "service", _service, "account", keyId],
                Convert.ToBase64String(fresh),
                ct).ConfigureAwait(false);
            if (stored.ExitCode != 0)
            {
                throw new KekCustodyException($"The keyring refused to store KEK '{keyId}': {stored.StandardError.Trim()}");
            }

            // Read back what the keyring holds, so a silent store failure cannot leave scopes wrapped under a key we do not have.
            var readBack = await LookupAsync(runner, keyId, ct).ConfigureAwait(false)
                ?? throw new KekCustodyException($"KEK '{keyId}' was stored but could not be read back.");
            if (!CryptographicOperations.FixedTimeEquals(readBack, fresh))
            {
                CryptographicOperations.ZeroMemory(readBack);
                throw new KekCustodyException($"KEK '{keyId}' read back from the keyring does not match what was stored.");
            }

            return readBack;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fresh);
        }
    }

    /// <summary>Returns the stored key, or null only for a clean "no such item" (exit 1, no output). Anything else throws.</summary>
    private static async Task<byte[]?> LookupAsync(ISecretToolRunner runner, string keyId, CancellationToken ct)
    {
        var result = await runner.RunAsync(["lookup", "service", _service, "account", keyId], null, ct).ConfigureAwait(false);
        if (result.ExitCode == 1 && result.StandardOutput.Length == 0 && result.StandardError.Length == 0)
        {
            return null;
        }

        if (result.ExitCode != 0 || result.StandardOutput.Trim().Length == 0)
        {
            throw new KekCustodyException($"The keyring could not answer for KEK '{keyId}' (exit {result.ExitCode}): {result.StandardError.Trim()}");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(result.StandardOutput.Trim());
        }
        catch (FormatException ex)
        {
            throw new KekCustodyException($"KEK '{keyId}' in the keyring is not valid base64.", ex);
        }

        if (key.Length != _keyBytes)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new KekCustodyException($"KEK '{keyId}' in the keyring is not {_keyBytes} bytes.");
        }

        return key;
    }
}
