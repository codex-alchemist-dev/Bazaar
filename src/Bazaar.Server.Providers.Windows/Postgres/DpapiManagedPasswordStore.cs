using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Components.ManagedPostgres.Secrets;

namespace Bazaar.Server.Providers.Windows.Postgres;

/// <summary>
/// Stores the managed superuser password under DPAPI, user scope. Only the same Windows account can decrypt the file.
/// Windows-only APIs live here, behind the component's <see cref="IManagedPasswordStore"/> interface.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiManagedPasswordStore(string path) : IManagedPasswordStore
{
    public async Task<string?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var plain = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public async Task SaveAsync(string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var plain = Encoding.UTF8.GetBytes(password);
        byte[] protectedBytes;
        try
        {
            protectedBytes = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, protectedBytes, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }
}
