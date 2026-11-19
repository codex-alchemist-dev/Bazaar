namespace Bazaar.Contracts.Releases;

/// <summary>
/// One downloadable build inside a <see cref="ReleaseManifest"/> (doc 30 sections 3, 4 and 7). Immutable; the
/// constructor validates every field and throws <see cref="ArgumentException"/>, so an instance is always well formed,
/// including one decoded from the wire. The feed stays untrusted until the updater has checked the hash and the
/// detached signature.
/// </summary>
public sealed record ReleaseArtifact
{
    /// <summary>The only operating system with a built artifact today.</summary>
    public const string OsLinux = "linux";

    /// <summary>Architecture <c>linux-x64</c> (the .NET runtime identifier).</summary>
    public const string ArchLinuxX64 = "linux-x64";

    /// <summary>Architecture <c>linux-arm64</c>.</summary>
    public const string ArchLinuxArm64 = "linux-arm64";

    /// <summary>Self-contained tarball (<c>Bazaar-&lt;version&gt;-linux-&lt;arch&gt;.tar.gz</c>).</summary>
    public const string FormatTarGz = "tar.gz";

    /// <summary>AppImage single file.</summary>
    public const string FormatAppImage = "appimage";

    /// <summary>Upper bound for a URL, in characters.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>Upper bound for an artifact size, in bytes (4 GiB). The updater refuses to download more.</summary>
    public const long MaxSizeBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Creates a validated artifact entry.</summary>
    /// <param name="os">Operating system, <see cref="OsLinux"/>.</param>
    /// <param name="architecture">Runtime identifier: <see cref="ArchLinuxX64"/> or <see cref="ArchLinuxArm64"/>. Must belong to <paramref name="os"/>.</param>
    /// <param name="format">Package format: <see cref="FormatTarGz"/> or <see cref="FormatAppImage"/>.</param>
    /// <param name="url">Package download URL: absolute https, or relative to the manifest's own URL.</param>
    /// <param name="sizeBytes">Package size in bytes, 1 to <see cref="MaxSizeBytes"/>.</param>
    /// <param name="sha256">Lowercase hex SHA-256 (64 characters) of the package bytes.</param>
    /// <param name="sha256Url">URL of the <c>.sha256</c> file next to the package; the updater cross-checks it against <paramref name="sha256"/>.</param>
    /// <param name="signatureUrl">URL of the detached OpenPGP signature (<c>.asc</c>) over the package bytes.</param>
    /// <exception cref="ArgumentException">A field is missing or malformed.</exception>
    public ReleaseArtifact(
        string os, string architecture, string format, string url, long sizeBytes,
        string sha256, string sha256Url, string signatureUrl)
    {
        Os = ReleaseValidation.Known(os, [OsLinux], nameof(os));
        Architecture = ReleaseValidation.Known(architecture, [ArchLinuxX64, ArchLinuxArm64], nameof(architecture));
        if (!Architecture.StartsWith(Os + "-", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Architecture '{Architecture}' does not belong to os '{Os}'.", nameof(architecture));
        }

        Format = ReleaseValidation.Known(format, [FormatTarGz, FormatAppImage], nameof(format));
        Url = ReleaseValidation.Url(url, nameof(url));
        if (sizeBytes is < 1 or > MaxSizeBytes)
        {
            throw new ArgumentException($"Size must be 1 to {MaxSizeBytes} bytes.", nameof(sizeBytes));
        }

        SizeBytes = sizeBytes;
        Sha256 = ReleaseValidation.LowerHex(sha256, 64, nameof(sha256));
        Sha256Url = ReleaseValidation.Url(sha256Url, nameof(sha256Url));
        SignatureUrl = ReleaseValidation.Url(signatureUrl, nameof(signatureUrl));
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

    /// <summary>URL of the <c>.sha256</c> file.</summary>
    public string Sha256Url { get; }

    /// <summary>URL of the detached OpenPGP <c>.asc</c> signature.</summary>
    public string SignatureUrl { get; }
}
