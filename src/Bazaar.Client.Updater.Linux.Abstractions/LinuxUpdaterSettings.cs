using Bazaar.Contracts.Releases;

namespace Bazaar.Client.Updater.Linux;

/// <summary>
/// Operator settings and build constants for the Linux updater (doc 30 sections 4, 5 and 7). The channel and the limits are
/// operator settings with safe defaults; the pinned key and fingerprint come from the generated company identity of the
/// build (doc 37 section 4a), not from a setting.
/// </summary>
public sealed record LinuxUpdaterSettings
{
    /// <summary>Default for <see cref="MaxExtractedBytes"/>, 4 GiB.</summary>
    public const long DefaultMaxExtractedBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Default for <see cref="MaxArchiveEntries"/>.</summary>
    public const int DefaultMaxArchiveEntries = 100_000;

    /// <summary>Absolute install root holding <c>releases/&lt;version&gt;</c>, <c>current</c> and <c>previous</c>.</summary>
    public required string InstallRoot { get; init; }

    /// <summary>SemVer of the running build. A manifest at or below it is never installed.</summary>
    public required string InstalledVersion { get; init; }

    /// <summary>Runtime identifier of this machine, <c>linux-x64</c> or <c>linux-arm64</c>.</summary>
    public required string Architecture { get; init; }

    /// <summary>Lowercase hex fingerprint of the signing key baked into the build.</summary>
    public required string PinnedKeyFingerprint { get; init; }

    /// <summary>Path of the armored public key file baked into the build.</summary>
    public required string PinnedKeyPath { get; init; }

    /// <summary>Setting <c>update.channel</c>: one of <see cref="ReleaseChannels.All"/>. Default Stable.</summary>
    public string Channel { get; init; } = ReleaseChannels.Stable;

    /// <summary>True on the owner's install. <see cref="ReleaseChannels.LatestCommit"/> is refused everywhere else (doc 30 section 5).</summary>
    public bool IsOwnerInstall { get; init; }

    /// <summary>Setting <c>update.gpg_path</c>: the <c>gpg</c> executable. Default <c>gpg</c> on the PATH.</summary>
    public string GpgPath { get; init; } = "gpg";

    /// <summary>Setting <c>update.gpg_timeout_seconds</c>: how long one <c>gpg</c> call may run. Default 60 seconds.</summary>
    public TimeSpan GpgTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Setting <c>update.max_package_bytes</c>: an operator ceiling below the manifest's own bound. Default the contract maximum, 4 GiB.</summary>
    public long MaxPackageBytes { get; init; } = ReleaseArtifact.MaxSizeBytes;

    /// <summary>Setting <c>update.max_extracted_bytes</c>: bytes the tarball may expand to. Default 4 GiB.</summary>
    public long MaxExtractedBytes { get; init; } = DefaultMaxExtractedBytes;

    /// <summary>Setting <c>update.max_archive_entries</c>: entries the tarball may hold. Default 100000.</summary>
    public int MaxArchiveEntries { get; init; } = DefaultMaxArchiveEntries;
}
