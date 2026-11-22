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

    /// <summary>Opens the provider for <paramref name="kind"/> (null or blank means the platform default) with its key file under <paramref name="dataRoot"/>.</summary>
    public static IKekProvider Create(string? kind, string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var keyPath = Path.Combine(dataRoot, "keys", "kek.bin");
        var chosen = string.IsNullOrWhiteSpace(kind) ? DefaultKind : kind.Trim().ToLowerInvariant();

        switch (chosen)
        {
            case "file":
                return FileKekProvider.Open(keyPath);
            case "dpapi":
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("identity.kek_provider 'dpapi' needs a Windows host. Use 'file' on this host (doc 14).");
                return CreateDpapi(keyPath);
            case "openbao":
                throw new NotSupportedException("identity.kek_provider 'openbao' is not built in this release (doc 14).");
            default:
                throw new ArgumentException(
                    $"identity.kek_provider '{kind}' is not one of: {string.Join(", ", AllowedKinds)}.", nameof(kind));
        }
    }

    /// <summary>
    /// Opens the provider like <see cref="Create"/>, except that the <c>file</c> kind seals its key file with <paramref name="keyring"/>
    /// where it is usable (doc 61 Q2) and otherwise keeps the protected file with an audited warning. The other kinds are unchanged.
    /// </summary>
    public static async ValueTask<IKekProvider> CreateAsync(
        string? kind,
        string dataRoot,
        IKekKeyring? keyring,
        KekKeyringMode mode,
        IKekCustodyAuditSink audit,
        TimeProvider? time = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var chosen = string.IsNullOrWhiteSpace(kind) ? DefaultKind : kind.Trim().ToLowerInvariant();
        if (chosen != "file")
            return Create(kind, dataRoot);

        return await FileKekProvider.OpenAsync(Path.Combine(dataRoot, "keys", "kek.bin"), keyring, mode, audit, time, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses <c>Bazaar:Identity:KekKeyring</c>: blank or <c>auto</c> is <see cref="KekKeyringMode.Auto"/>, <c>required</c> tightens it. Anything else fails.</summary>
    public static KekKeyringMode ParseKeyringMode(string? value) =>
        (value?.Trim().ToLowerInvariant()) switch
        {
            null or "" or "auto" => KekKeyringMode.Auto,
            "required" => KekKeyringMode.Required,
            _ => throw new ArgumentException($"Bazaar:Identity:KekKeyring '{value}' is not one of: auto, required (doc 61 Q2).", nameof(value)),
        };

    /// <summary>The OS keyring for the <c>file</c> provider: DPAPI on Windows, the Secret Service (libsecret) on Linux, none elsewhere.</summary>
    public static IKekKeyring? DefaultKeyring()
    {
        if (OperatingSystem.IsWindows())
            return KeyProtectorKekKeyring.ForDpapi();
        if (OperatingSystem.IsLinux())
            return new Bazaar.Secrets.Keyring.SecretServiceKekKeyring(new Bazaar.Secrets.Keyring.ProcessSecretToolRunner());
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static DpapiKekProvider CreateDpapi(string keyPath) => DpapiKekProvider.Open(keyPath, new DpapiKeyProtector());
}
