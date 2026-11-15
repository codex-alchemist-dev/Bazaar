using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Components.ManagedPostgres.Secrets;

/// <summary>
/// The Linux key for <see cref="AesGcmManagedPasswordStore"/>. The preferred source is a systemd credential named
/// <c>bazaar-managed-store-key</c>: 32 raw bytes supplied with <c>LoadCredential=</c> or <c>LoadCredentialEncrypted=</c>, so the
/// key lives outside the data directory. Without that credential, the key is derived with HKDF-SHA256 from <c>/etc/machine-id</c>.
/// The fallback ties the store to this host, so a copied data directory cannot be read elsewhere. It does not protect against a
/// reader with root on the same host, who can read both the store and the machine id. Use the systemd credential where possible.
/// </summary>
public sealed class LinuxManagedStoreKeyProvider : IManagedStoreKeyProvider
{
    public const string CredentialName = "bazaar-managed-store-key";
    private const int _keySize = 32;
    private const string _machineIdPath = "/etc/machine-id";

    private static readonly byte[] _info = "bazaar-managed-postgres-superuser-store"u8.ToArray();

    private readonly string? _credentialsDirectory;
    private readonly string _machineIdFile;

    public LinuxManagedStoreKeyProvider()
        : this(Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY"), _machineIdPath)
    {
    }

    /// <summary>Reads the credential from <paramref name="credentialsDirectory"/> when set, and derives the fallback key from <paramref name="machineIdFile"/>.</summary>
    public LinuxManagedStoreKeyProvider(string? credentialsDirectory, string machineIdFile)
    {
        _credentialsDirectory = string.IsNullOrWhiteSpace(credentialsDirectory) ? null : credentialsDirectory;
        _machineIdFile = machineIdFile;
    }

    public async Task<byte[]> GetKeyAsync(CancellationToken cancellationToken)
    {
        if (_credentialsDirectory is not null)
        {
            var credential = Path.Combine(_credentialsDirectory, CredentialName);
            if (File.Exists(credential))
            {
                var key = await File.ReadAllBytesAsync(credential, cancellationToken);
                if (key.Length != _keySize)
                {
                    throw new InvalidOperationException($"The systemd credential {CredentialName} must hold {_keySize} bytes.");
                }

                return key;
            }
        }

        if (!File.Exists(_machineIdFile))
        {
            throw new InvalidOperationException("No managed store key: there is no systemd credential and no machine id to derive one from.");
        }

        var machineId = (await File.ReadAllTextAsync(_machineIdFile, cancellationToken)).Trim();
        if (machineId.Length == 0)
        {
            throw new InvalidOperationException("No managed store key: the machine id file is empty.");
        }

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(machineId), _keySize, salt: null, info: _info);
    }
}
