using System.Runtime.Versioning;

namespace Bazaar.Identity.Kek;

/// <summary>
/// Chooses and opens the host KEK provider from <c>identity.kek_provider</c> (doc 14). The default is <c>dpapi</c> on a Windows
/// host and <c>file</c> elsewhere. An unknown value, <c>openbao</c> without its module loaded or <c>dpapi</c> off Windows fails here, never
/// falls back to another provider.
/// </summary>
public static class KekProviderFactory
{
    /// <summary>The setting's allowed values, as documented in doc 14.</summary>
    public static IReadOnlyList<string> AllowedKinds { get; } = ["dpapi", "file", "openbao"];

    /// <summary>The platform default: <c>dpapi</c> on Windows, <c>file</c> elsewhere.</summary>
    public static string DefaultKind => OperatingSystem.IsWindows() ? "dpapi" : "file";

    /// <summary>
    /// The key file for a kind, under <paramref name="dataRoot"/>. Each kind has its own file, so the source and target of a switch
    /// coexist while the re-wrap runs. <c>file</c> keeps the historical <c>keys/kek.bin</c>.
    /// </summary>
    public static string KeyPathFor(string kind, string dataRoot) => kind switch
    {
        "file" => Path.Combine(dataRoot, "keys", "kek.bin"),
        "dpapi" => Path.Combine(dataRoot, "keys", "kek-dpapi.bin"),
        _ => throw new ArgumentException($"No key file is defined for identity.kek_provider '{kind}'.", nameof(kind)),
    };

    /// <summary>Opens a provider that must already exist (the re-wrap source). It never creates a key.</summary>
    /// <exception cref="FileNotFoundException">The key file for the kind is absent.</exception>
    public static IKekProvider OpenExisting(string kind, string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var chosen = Normalise(kind);
        if (chosen is "openbao" || !File.Exists(KeyPathFor(chosen, dataRoot)))
            throw new FileNotFoundException($"No existing KEK file for identity.kek_provider '{chosen}'.", KeyPathFor(chosen, dataRoot));
        return Create(chosen, dataRoot);
    }

    /// <summary>The kind the setting selects: lower case, with the platform default for a blank value.</summary>
    public static string NormaliseKind(string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? DefaultKind : kind.Trim().ToLowerInvariant();

    private static string Normalise(string? kind) => NormaliseKind(kind);

    /// <summary>Opens the provider for <paramref name="kind"/> (null or blank means the platform default) with its key file under <paramref name="dataRoot"/>.</summary>
    public static IKekProvider Create(string? kind, string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var chosen = Normalise(kind);
        var keyPath = chosen is "file" or "dpapi" ? KeyPathFor(chosen, dataRoot) : string.Empty;

        switch (chosen)
        {
            case "file":
                return FileKekProvider.Open(keyPath);
            case "dpapi":
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("identity.kek_provider 'dpapi' needs a Windows host. Use 'file' on this host (doc 14).");
                return CreateDpapi(keyPath);
            case "openbao":
                throw new NotSupportedException("identity.kek_provider 'openbao' needs the identity-kek-openbao module to be loaded (doc 14).");
            default:
                throw new ArgumentException(
                    $"identity.kek_provider '{kind}' is not one of: {string.Join(", ", AllowedKinds)}.", nameof(kind));
        }
    }

    [SupportedOSPlatform("windows")]
    private static DpapiKekProvider CreateDpapi(string keyPath) => DpapiKekProvider.Open(keyPath, new DpapiKeyProtector());
}
