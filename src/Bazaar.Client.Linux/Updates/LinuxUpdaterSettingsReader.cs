using System.Globalization;
using Bazaar.Client.Updater.Linux;
using Bazaar.Contracts.Releases;

namespace Bazaar.Client.Linux.Updates;

/// <summary>
/// Builds <see cref="LinuxUpdaterSettings"/> from the environment, the way <c>ChatConnectionSettings.FromEnvironment</c> does
/// (doc 30 section 7). Operator settings come from <c>BAZAAR_UPDATE_*</c> variables; the key pin and the owner flag come from
/// <see cref="UpdateBuildConstants"/>. Limits may tighten the shipped defaults and never loosen them. No I/O.
/// </summary>
public static class LinuxUpdaterSettingsReader
{
    public const string ChannelVariable = "BAZAAR_UPDATE_CHANNEL";
    public const string SkipVersionsVariable = "BAZAAR_UPDATE_SKIP_VERSIONS";
    public const string GpgPathVariable = "BAZAAR_UPDATE_GPG_PATH";
    public const string GpgTimeoutVariable = "BAZAAR_UPDATE_GPG_TIMEOUT_SECONDS";
    public const string MaxPackageBytesVariable = "BAZAAR_UPDATE_MAX_PACKAGE_BYTES";
    public const string MaxExtractedBytesVariable = "BAZAAR_UPDATE_MAX_EXTRACTED_BYTES";
    public const string MaxArchiveEntriesVariable = "BAZAAR_UPDATE_MAX_ARCHIVE_ENTRIES";

    /// <exception cref="ArgumentException">A value is set but invalid, or loosens a shipped limit.</exception>
    public static LinuxUpdaterSettings FromEnvironment(
        Func<string, string?> env, LinuxPaths paths, string installedVersion, string architecture, UpdateBuildConstants build)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentException.ThrowIfNullOrWhiteSpace(installedVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(architecture);

        var settings = new LinuxUpdaterSettings
        {
            InstallRoot = paths.InstallRoot,
            InstalledVersion = installedVersion,
            Architecture = architecture,
            PinnedKeyFingerprint = build.PinnedKeyFingerprint,
            PinnedKeyPath = build.PinnedKeyPath,
            IsOwnerInstall = build.IsOwnerInstall,
        };

        string? channel = Clean(env(ChannelVariable));
        if (channel is not null)
        {
            if (!ReleaseChannels.IsKnown(channel))
            {
                throw new ArgumentException($"{ChannelVariable} must be one of {string.Join(", ", ReleaseChannels.All)}, got '{channel}'.");
            }

            settings = settings with { Channel = channel };
        }

        string? skip = Clean(env(SkipVersionsVariable));
        if (skip is not null)
        {
            settings = settings with
            {
                SkipVersions = skip.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            };
        }

        string? gpg = Clean(env(GpgPathVariable));
        if (gpg is not null) settings = settings with { GpgPath = gpg };

        long? timeout = ReadLong(env, GpgTimeoutVariable, 1, 3600);
        if (timeout is { } t) settings = settings with { GpgTimeout = TimeSpan.FromSeconds(t) };

        long? package = ReadLong(env, MaxPackageBytesVariable, 1, settings.MaxPackageBytes);
        if (package is { } p) settings = settings with { MaxPackageBytes = p };

        long? extracted = ReadLong(env, MaxExtractedBytesVariable, 1, settings.MaxExtractedBytes);
        if (extracted is { } x) settings = settings with { MaxExtractedBytes = x };

        long? entries = ReadLong(env, MaxArchiveEntriesVariable, 1, settings.MaxArchiveEntries);
        if (entries is { } e) settings = settings with { MaxArchiveEntries = (int)e };

        return settings;
    }

    private static long? ReadLong(Func<string, string?> env, string name, long min, long max)
    {
        string? raw = Clean(env(name));
        if (raw is null) return null;
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value < min || value > max)
        {
            throw new ArgumentException($"{name} must be a whole number from {min} to {max}, got '{raw}'.");
        }

        return value;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
