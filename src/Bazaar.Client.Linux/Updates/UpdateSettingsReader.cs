using System.Globalization;
using Bazaar.Client.Updater.Linux;
using Bazaar.CompanyIdentity;
using Bazaar.Contracts.Releases;

namespace Bazaar.Client.Linux.Updates;

/// <summary>
/// Builds the update settings (doc 30 sections 5 and 7). The feed address and its pin come from the build's company identity
/// when it has one, else from <c>BAZAAR_UPDATE_MANIFEST_URL</c> and <c>BAZAAR_UPDATE_SERVER_PIN</c>. The signing key comes
/// from <c>BAZAAR_UPDATE_KEY_FINGERPRINT</c> and the key file. Fails closed: with no address, no valid pin, no fingerprint or no
/// key file, updates are off with a reason. Limits may tighten the shipped defaults and never loosen them. Never throws on
/// bad input and does no I/O beyond the injected file check.
/// </summary>
public static class UpdateSettingsReader
{
    /// <summary>Set to <c>false</c> to turn updates off.</summary>
    public const string EnabledVariable = "BAZAAR_UPDATE_ENABLED";

    /// <summary>The release channel (<c>update.channel</c>).</summary>
    public const string ChannelVariable = "BAZAAR_UPDATE_CHANNEL";

    /// <summary>Feed address, used only when the build carries no server address.</summary>
    public const string ManifestUrlVariable = "BAZAAR_UPDATE_MANIFEST_URL";

    /// <summary>Server key-hash pin, used only when the build carries no server address.</summary>
    public const string ServerPinVariable = "BAZAAR_UPDATE_SERVER_PIN";

    /// <summary>Lowercase hex fingerprint (40 or 64 characters) of the pinned OpenPGP signing key.</summary>
    public const string KeyFingerprintVariable = "BAZAAR_UPDATE_KEY_FINGERPRINT";

    /// <summary>Path of the pinned OpenPGP public key file.</summary>
    public const string KeyPathVariable = "BAZAAR_UPDATE_KEY_PATH";

    /// <summary>Comma-separated SemVer versions never to install (<c>update.skip_versions</c>).</summary>
    public const string SkipVersionsVariable = "BAZAAR_UPDATE_SKIP_VERSIONS";

    /// <summary>Path of <c>gpg</c>.</summary>
    public const string GpgPathVariable = "BAZAAR_UPDATE_GPG_PATH";

    /// <summary>Seconds <c>gpg</c> may run.</summary>
    public const string GpgTimeoutVariable = "BAZAAR_UPDATE_GPG_TIMEOUT_SECONDS";

    /// <summary>Largest package, in bytes. May only tighten.</summary>
    public const string MaxPackageBytesVariable = "BAZAAR_UPDATE_MAX_PACKAGE_BYTES";

    /// <summary>Largest extracted size, in bytes. May only tighten.</summary>
    public const string MaxExtractedBytesVariable = "BAZAAR_UPDATE_MAX_EXTRACTED_BYTES";

    /// <summary>Most archive entries. May only tighten.</summary>
    public const string MaxArchiveEntriesVariable = "BAZAAR_UPDATE_MAX_ARCHIVE_ENTRIES";

    private const string _feedPath = "updates/latest";

    /// <summary>Reads the settings. <paramref name="fileExists"/> is the only I/O.</summary>
    public static UpdateConfiguration Read(
        Func<string, string?> env,
        UpdatePaths paths,
        UpdateBuildIdentity build,
        string installedVersion,
        string architecture,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (string.Equals(Clean(env(EnabledVariable)), "false", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateConfiguration.Off($"Updates are turned off ({EnabledVariable}=false).");
        }

        var feed = ReadFeed(env, build, out string? pin, out string? feedProblem);
        if (feed is null)
        {
            return UpdateConfiguration.Off(feedProblem!);
        }

        string? fingerprint = Clean(env(KeyFingerprintVariable));
        if (!IsFingerprint(fingerprint))
        {
            return UpdateConfiguration.Off($"No signing key is pinned: {KeyFingerprintVariable} must be 40 or 64 lowercase hex characters.");
        }

        string keyPath = Clean(env(KeyPathVariable)) ?? paths.DefaultKeyFile;
        if (!fileExists(keyPath))
        {
            return UpdateConfiguration.Off($"The pinned signing key file is missing: {keyPath}.");
        }

        if (string.IsNullOrWhiteSpace(installedVersion) || string.IsNullOrWhiteSpace(architecture))
        {
            return UpdateConfiguration.Off("The installed version or architecture is unknown.");
        }

        var settings = new LinuxUpdaterSettings
        {
            InstallRoot = paths.InstallRoot,
            InstalledVersion = installedVersion,
            Architecture = architecture,
            PinnedKeyFingerprint = fingerprint!,
            PinnedKeyPath = keyPath,
        };

        string? channel = Clean(env(ChannelVariable));
        if (channel is not null)
        {
            if (!ReleaseChannels.IsKnown(channel))
            {
                return UpdateConfiguration.Off($"{ChannelVariable} must be one of {string.Join(", ", ReleaseChannels.All)}, got '{channel}'.");
            }

            settings = settings with { Channel = channel };
        }

        string? skip = Clean(env(SkipVersionsVariable));
        if (skip is not null)
        {
            settings = settings with { SkipVersions = skip.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
        }

        string? gpg = Clean(env(GpgPathVariable));
        if (gpg is not null)
        {
            settings = settings with { GpgPath = gpg };
        }

        string? problem = null;
        long? timeout = ReadLimit(env, GpgTimeoutVariable, 3600, ref problem);
        long? package = ReadLimit(env, MaxPackageBytesVariable, settings.MaxPackageBytes, ref problem);
        long? extracted = ReadLimit(env, MaxExtractedBytesVariable, settings.MaxExtractedBytes, ref problem);
        long? entries = ReadLimit(env, MaxArchiveEntriesVariable, settings.MaxArchiveEntries, ref problem);
        if (problem is not null)
        {
            return UpdateConfiguration.Off(problem);
        }

        if (timeout is { } t) settings = settings with { GpgTimeout = TimeSpan.FromSeconds(t) };
        if (package is { } p) settings = settings with { MaxPackageBytes = p };
        if (extracted is { } x) settings = settings with { MaxExtractedBytes = x };
        if (entries is { } e) settings = settings with { MaxArchiveEntries = (int)e };

        return new UpdateConfiguration { Settings = settings, ManifestUri = feed, ServerKeyHashPin = pin };
    }

    private static Uri? ReadFeed(Func<string, string?> env, UpdateBuildIdentity build, out string? pin, out string? problem)
    {
        pin = null;
        problem = null;
        Uri? feed;
        if (build.ServerAddress is { IsAbsoluteUri: true } address)
        {
            if (!Uri.TryCreate(address, _feedPath, out feed))
            {
                problem = "The company server address cannot form an update feed address.";
                return null;
            }

            pin = build.KeyHashPin;
        }
        else
        {
            string? raw = Clean(env(ManifestUrlVariable));
            if (raw is null)
            {
                problem = "No update feed: this build carries no company server address and no manifest address is set.";
                return null;
            }

            if (!Uri.TryCreate(raw, UriKind.Absolute, out feed))
            {
                problem = $"{ManifestUrlVariable} is not an absolute address.";
                return null;
            }

            pin = Clean(env(ServerPinVariable));
        }

        if (!string.IsNullOrEmpty(feed.UserInfo))
        {
            problem = "The update feed address must not carry user information.";
            return null;
        }

        if (string.Equals(feed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            if (!ServerPin.IsWellFormed(pin))
            {
                problem = "The update feed is https but no valid server key-hash pin is present.";
                return null;
            }

            return feed;
        }

        if (string.Equals(feed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && feed.IsLoopback)
        {
            pin = null;
            return feed;
        }

        problem = "The update feed must be https (plain http is allowed on loopback only).";
        return null;
    }

    private static bool IsFingerprint(string? value) =>
        value is { Length: 40 or 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static long? ReadLimit(Func<string, string?> env, string name, long max, ref string? problem)
    {
        string? raw = Clean(env(name));
        if (raw is null)
        {
            return null;
        }

        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) || parsed < 1 || parsed > max)
        {
            problem ??= $"{name} must be a whole number from 1 to {max}, got '{raw}'.";
            return null;
        }

        return parsed;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
