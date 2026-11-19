using System.Runtime.Versioning;

namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// Chooses and opens the host KEK provider from <c>identity.kek_provider</c> (doc 14). The default is <c>dpapi</c> on a Windows
/// host and <c>file</c> elsewhere. An unknown value, <c>openbao</c> (not built yet) or <c>dpapi</c> off Windows fails here, never
/// falls back to another provider.
/// </summary>
public static class KekProviderFactory
{
    /// <summary>The setting's allowed values, as documented in doc 14.</summary>
    public static IReadOnlyList<string> AllowedKinds { get; } = ["dpapi", "file", "openbao"];

    /// <summary>The platform default: <c>dpapi</c> on Windows, <c>file</c> elsewhere.</summary>
    public static string DefaultKind => OperatingSystem.IsWindows() ? "dpapi" : "file";

    /// <summary>
    /// Opens the provider for <paramref name="kind"/> (null or blank means the platform default) with its key file under
    /// <paramref name="keyRoot"/> (<c>keys/kek.bin</c>). <paramref name="mode"/> decides whether a missing key is created.
    /// </summary>
    /// <exception cref="KekMissingException">The key is missing or empty and <paramref name="mode"/> is <see cref="KekOpenMode.OpenExisting"/>.</exception>
    public static IKekProvider Create(string? kind, string keyRoot, KekOpenMode mode = KekOpenMode.CreateIfMissing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyRoot);
        var keyPath = Path.Combine(keyRoot, "keys", "kek.bin");
        var chosen = string.IsNullOrWhiteSpace(kind) ? DefaultKind : kind.Trim().ToLowerInvariant();

        switch (chosen)
        {
            case "file":
                return FileKekProvider.Open(keyPath, mode);
            case "dpapi":
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("identity.kek_provider 'dpapi' needs a Windows host. Use 'file' on this host (doc 14).");
                return CreateDpapi(keyPath, mode);
            case "openbao":
                throw new NotSupportedException("identity.kek_provider 'openbao' is not built in this release (doc 14).");
            default:
                throw new ArgumentException(
                    $"identity.kek_provider '{kind}' is not one of: {string.Join(", ", AllowedKinds)}.", nameof(kind));
        }
    }

    [SupportedOSPlatform("windows")]
    private static DpapiKekProvider CreateDpapi(string keyPath, KekOpenMode mode) => DpapiKekProvider.Open(keyPath, new DpapiKeyProtector(), mode);
}
