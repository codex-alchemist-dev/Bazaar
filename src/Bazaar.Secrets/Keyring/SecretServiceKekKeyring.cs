using System.Security.Cryptography;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Secrets.Keyring;

/// <summary>
/// Seals the <c>file</c> KEK provider's key file with the Linux Secret Service (libsecret, through <c>secret-tool</c>; doc 61 Q2).
/// The keyring holds a random 32-byte wrapping key under <c>service=bazaar-kek-file</c>, <c>account=&lt;entry&gt;</c>; the key file holds
/// the KEK sealed under it with AES-256-GCM (<c>nonce | ciphertext | tag</c>). The wrapping key is never derived from the KEK.
/// Only a clean "no such item" answer creates an entry; any other answer throws, so an unreadable entry is never replaced.
/// </summary>
public sealed class SecretServiceKekKeyring(ISecretToolRunner runner) : IKekKeyring
{
    /// <summary>The kind written into a key file sealed by this keyring.</summary>
    public const string KindName = "libsecret";

    private const string _service = "bazaar-kek-file";
    private const int _keyBytes = 32;
    private const int _maxReasonLength = 200;

    private readonly ISecretToolRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public string Kind => KindName;

    public async ValueTask<KekKeyringProbe> ProbeAsync(string entryName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
        SecretToolResult result;
        try
        {
            result = await _runner.RunAsync(["lookup", "service", _service, "account", entryName], null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is KekCustodyException or IOException or InvalidOperationException)
        {
            return KekKeyringProbe.Unavailable(Short(ex.Message));
        }

        if (result.ExitCode == 1 && result.StandardOutput.Length == 0 && result.StandardError.Length == 0)
        {
            return new KekKeyringProbe(true, false, null);
        }

        if (result.ExitCode == 0 && result.StandardOutput.Trim().Length > 0)
        {
            return new KekKeyringProbe(true, true, null);
        }

        // The secret-tool error text names the bus or collection, never a value.
        return KekKeyringProbe.Unavailable($"secret-tool exit {result.ExitCode}: {Short(result.StandardError.Trim())}");
    }

    public async ValueTask<byte[]> SealAsync(string entryName, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
    {
        var wrappingKey = await LookupAsync(entryName, cancellationToken).ConfigureAwait(false)
            ?? await CreateAsync(entryName, cancellationToken).ConfigureAwait(false);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var sealedKey = new byte[12 + key.Length + 16];
            nonce.CopyTo(sealedKey, 0);
            using var aes = new AesGcm(wrappingKey, 16);
            aes.Encrypt(nonce, key.Span, sealedKey.AsSpan(12, key.Length), sealedKey.AsSpan(12 + key.Length), associatedData.Span);
            return sealedKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    public async ValueTask<byte[]> OpenAsync(string entryName, ReadOnlyMemory<byte> sealedKey, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
    {
        if (sealedKey.Length < 12 + 16)
        {
            throw new KekKeyringException("The sealed KEK is too short.");
        }

        var wrappingKey = await LookupAsync(entryName, cancellationToken).ConfigureAwait(false)
            ?? throw new KekKeyringException($"The keyring has no entry '{entryName}' for the sealed KEK file. Restore the keyring or the KEK from the recovery bundle (doc 48 section 6).");
        var plain = new byte[sealedKey.Length - 28];
        try
        {
            using var aes = new AesGcm(wrappingKey, 16);
            var span = sealedKey.Span;
            aes.Decrypt(span[..12], span[12..^16], span[^16..], plain, associatedData.Span);
            return plain;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plain);
            throw new KekKeyringException("The sealed KEK file does not authenticate under the keyring entry.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    private async Task<byte[]> CreateAsync(string entryName, CancellationToken cancellationToken)
    {
        var fresh = RandomNumberGenerator.GetBytes(_keyBytes);
        try
        {
            var stored = await Run(["store", $"--label=Bazaar KEK file {entryName}", "service", _service, "account", entryName], Convert.ToBase64String(fresh), cancellationToken).ConfigureAwait(false);
            if (stored.ExitCode != 0)
            {
                throw new KekKeyringException($"The keyring refused to store entry '{entryName}': {Short(stored.StandardError.Trim())}");
            }

            // Read back, so a silent store failure cannot leave a key file sealed under a key the keyring does not hold.
            var readBack = await LookupAsync(entryName, cancellationToken).ConfigureAwait(false)
                ?? throw new KekKeyringException($"Keyring entry '{entryName}' was stored but could not be read back.");
            if (!CryptographicOperations.FixedTimeEquals(readBack, fresh))
            {
                CryptographicOperations.ZeroMemory(readBack);
                throw new KekKeyringException($"Keyring entry '{entryName}' read back does not match what was stored.");
            }

            return readBack;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fresh);
        }
    }

    /// <summary>Returns the entry's key, or null only for a clean "no such item" (exit 1, no output). Anything else throws.</summary>
    private async Task<byte[]?> LookupAsync(string entryName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
        var result = await Run(["lookup", "service", _service, "account", entryName], null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1 && result.StandardOutput.Length == 0 && result.StandardError.Length == 0)
        {
            return null;
        }

        if (result.ExitCode != 0 || result.StandardOutput.Trim().Length == 0)
        {
            throw new KekKeyringException($"The keyring could not answer for entry '{entryName}' (exit {result.ExitCode}): {Short(result.StandardError.Trim())}");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(result.StandardOutput.Trim());
        }
        catch (FormatException)
        {
            throw new KekKeyringException($"Keyring entry '{entryName}' is not valid base64.");
        }

        if (key.Length != _keyBytes)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new KekKeyringException($"Keyring entry '{entryName}' is not {_keyBytes} bytes.");
        }

        return key;
    }

    private async Task<SecretToolResult> Run(IReadOnlyList<string> arguments, string? input, CancellationToken cancellationToken)
    {
        try
        {
            return await _runner.RunAsync(arguments, input, cancellationToken).ConfigureAwait(false);
        }
        catch (KekCustodyException ex)
        {
            throw new KekKeyringException("The keyring is unavailable: " + Short(ex.Message), ex);
        }
    }

    private static string Short(string text) => text.Length <= _maxReasonLength ? text : text[.._maxReasonLength];
}
