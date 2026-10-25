using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Providers.Windows;

/// <summary>
/// Resolves <c>dpapi:&lt;path&gt;</c> bootstrap references (doc 03). The file holds base64 of DPAPI-protected bytes,
/// protected at machine scope so the service account can read them after a restart. Errors name the reference, never the value.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretResolver : ISecretResolver
{
    private const string _prefix = "dpapi:";

    public bool CanResolve(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.StartsWith(_prefix, StringComparison.Ordinal);
    }

    public ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (!CanResolve(reference))
        {
            throw new SecretResolutionException(reference, "unsupported scheme; DPAPI resolves dpapi: only.");
        }

        // The platform check comes before any file access, so a non-Windows host never touches the path.
        if (!OperatingSystem.IsWindows())
        {
            throw new SecretResolutionException(reference, "DPAPI requires Windows.");
        }

        var path = reference[_prefix.Length..];
        if (path.Length == 0)
        {
            throw new SecretResolutionException(reference, "the file path is empty.");
        }

        return ResolveFileAsync(reference, path, cancellationToken);
    }

    private static async ValueTask<string> ResolveFileAsync(string reference, string path, CancellationToken cancellationToken)
    {
        string encoded;
        try
        {
            encoded = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            throw new SecretResolutionException(reference, "the file does not exist.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SecretResolutionException(reference, "the file could not be read.", ex);
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(encoded.Trim());
        }
        catch (FormatException ex)
        {
            throw new SecretResolutionException(reference, "the file does not hold base64 data.", ex);
        }

        byte[] plainBytes;
        try
        {
            plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException ex)
        {
            throw new SecretResolutionException(reference, "DPAPI could not unprotect the data on this machine.", ex);
        }
        finally
        {
            Array.Clear(protectedBytes);
        }

        try
        {
            // Trailing line endings are an artifact of how files are written, not part of the secret.
            var value = Encoding.UTF8.GetString(plainBytes).TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new SecretResolutionException(reference, "the protected value is empty.");
            }

            return value;
        }
        finally
        {
            Array.Clear(plainBytes);
        }
    }
}
