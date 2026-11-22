namespace Bazaar.Contracts.Releases;

/// <summary>
/// One downloadable build inside a <see cref="ReleaseManifest"/> (doc 30 sections 3, 4 and 7; doc 37 section 4b).
/// Immutable; the constructor validates every field and throws <see cref="ArgumentException"/>, so an instance is always
/// well formed, including one decoded from the wire. The feed stays untrusted until the updater has checked the hash and
/// the signature. Linux builds are signed by a detached OpenPGP <c>.asc</c> file; Windows builds carry an inline signature
/// checked against the pinned company certificate.
/// </summary>
public sealed record ReleaseArtifact
{
    /// <summary>Linux builds (tarball, AppImage); signed with a detached OpenPGP signature.</summary>
    public const string OsLinux = "linux";

    /// <summary>Windows builds (MSIX, Velopack); signed in-band by the company certificate (doc 37 section 4b).</summary>
    public const string OsWindows = "windows";

    /// <summary>Architecture <c>linux-x64</c> (the .NET runtime identifier).</summary>
    public const string ArchLinuxX64 = "linux-x64";

    /// <summary>Architecture <c>linux-arm64</c>.</summary>
    public const string ArchLinuxArm64 = "linux-arm64";

    /// <summary>Architecture <c>windows-x64</c>.</summary>
    public const string ArchWindowsX64 = "windows-x64";

    /// <summary>Architecture <c>windows-arm64</c>.</summary>
    public const string ArchWindowsArm64 = "windows-arm64";

    /// <summary>Self-contained tarball (<c>Bazaar-&lt;version&gt;-linux-&lt;arch&gt;.tar.gz</c>). Linux only.</summary>
    public const string FormatTarGz = "tar.gz";

    /// <summary>AppImage single file. Linux only.</summary>
    public const string FormatAppImage = "appimage";

    /// <summary>MSIX package. Windows only.</summary>
    public const string FormatMsix = "msix";

    /// <summary>Velopack package. Windows only.</summary>
    public const string FormatVelopack = "velopack";

    /// <summary>Upper bound for a URL, in characters.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>Upper bound for the base64 text of an inline signature (an RSA-8192 signature is 1368 characters).</summary>
    public const int MaxSignatureLength = 2048;

    /// <summary>Upper bound for an artifact size, in bytes (4 GiB). The updater refuses to download more.</summary>
    public const long MaxSizeBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Creates a validated artifact entry.</summary>
    /// <param name="os">Operating system, <see cref="OsLinux"/> or <see cref="OsWindows"/>.</param>
    /// <param name="architecture">Runtime identifier (<c>linux-x64</c>, <c>linux-arm64</c>, <c>windows-x64</c>, <c>windows-arm64</c>). Must belong to <paramref name="os"/>.</param>
    /// <param name="format">Linux: <c>tar.gz</c> or <c>appimage</c>. Windows: <c>msix</c> or <c>velopack</c>. Must belong to <paramref name="os"/>.</param>
    /// <param name="url">Package download URL: absolute https, or relative to the manifest's own URL.</param>
    /// <param name="sizeBytes">Package size in bytes, 1 to <see cref="MaxSizeBytes"/>.</param>
    /// <param name="sha256">Lowercase hex SHA-256 (64 characters) of the package bytes.</param>
    /// <param name="sha256Url">URL of the <c>.sha256</c> file next to the package. Required for Linux (cross-checked against <paramref name="sha256"/>); optional for Windows.</param>
    /// <param name="signatureUrl">URL of the detached OpenPGP signature (<c>.asc</c>). Required for Linux; optional for Windows, where it is not the trust path.</param>
    /// <param name="signature">Base64 RSASSA-PKCS1-v1_5 signature over SHA-256 of the package bytes, made with the company signing key (doc 37 section 4b). Required for Windows; must be absent for Linux.</param>
    /// <exception cref="ArgumentException">A field is missing, malformed or does not fit the OS.</exception>
    public ReleaseArtifact(
        string os, string architecture, string format, string url, long sizeBytes,
        string sha256, string? sha256Url, string? signatureUrl, string? signature = null)
    {
        Os = ReleaseValidation.Known(os, [OsLinux, OsWindows], nameof(os));
        Architecture = ReleaseValidation.Known(
            architecture, [ArchLinuxX64, ArchLinuxArm64, ArchWindowsX64, ArchWindowsArm64], nameof(architecture));
        if (!Architecture.StartsWith(Os + "-", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Architecture '{Architecture}' does not belong to os '{Os}'.", nameof(architecture));
        }

        bool linux = Os == OsLinux;
        Format = ReleaseValidation.Known(
            format, linux ? [FormatTarGz, FormatAppImage] : [FormatMsix, FormatVelopack], nameof(format));
        Url = ReleaseValidation.Url(url, nameof(url));
        if (sizeBytes is < 1 or > MaxSizeBytes)
        {
            throw new ArgumentException($"Size must be 1 to {MaxSizeBytes} bytes.", nameof(sizeBytes));
        }

        SizeBytes = sizeBytes;
        Sha256 = ReleaseValidation.LowerHex(sha256, 64, nameof(sha256));

        if (linux)
        {
            // Linux trust path: the .sha256 file and a detached OpenPGP signature, both fetched by URL.
            Sha256Url = ReleaseValidation.Url(sha256Url, nameof(sha256Url));
            SignatureUrl = ReleaseValidation.Url(signatureUrl, nameof(signatureUrl));
            if (signature is not null)
            {
                throw new ArgumentException("A Linux artifact is signed by its .asc file; it carries no inline signature.", nameof(signature));
            }
        }
        else
        {
            // Windows trust path: an inline signature checked against the pinned company certificate.
            Sha256Url = sha256Url is null ? null : ReleaseValidation.Url(sha256Url, nameof(sha256Url));
            SignatureUrl = signatureUrl is null ? null : ReleaseValidation.Url(signatureUrl, nameof(signatureUrl));
            Signature = ReleaseValidation.Base64(signature, nameof(signature));
        }
    }

    /// <summary>Operating system.</summary>
    public string Os { get; }

    /// <summary>Runtime identifier, such as <c>linux-x64</c>.</summary>
    public string Architecture { get; }

    /// <summary>Package format.</summary>
    public string Format { get; }

    /// <summary>Package download URL (https or manifest-relative).</summary>
    public string Url { get; }

    /// <summary>Package size in bytes.</summary>
    public long SizeBytes { get; }

    /// <summary>Lowercase hex SHA-256 of the package.</summary>
    public string Sha256 { get; }

    /// <summary>URL of the <c>.sha256</c> file. Always set for Linux; optional for Windows.</summary>
    public string? Sha256Url { get; }

    /// <summary>URL of the detached OpenPGP <c>.asc</c> signature. Always set for Linux; optional for Windows.</summary>
    public string? SignatureUrl { get; }

    /// <summary>Base64 inline signature for the pinned-certificate path. Always set for Windows; null for Linux.</summary>
    public string? Signature { get; }
}
