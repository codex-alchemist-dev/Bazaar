namespace Bazaar.Client.Updates;

/// <summary>
/// The release channel an install follows (doc 49 section 6). An install is on one channel at a time. LTSC, Alpha and
/// Latest-Commit from older docs are not channels of their own here: <c>alpha</c> parses as <see cref="Nightly"/> (deprecated alias).
/// </summary>
public enum UpdateChannel
{
    Stable,
    Beta,
    Nightly,
    Lts,
}

/// <summary>Setting-value mapping for <see cref="UpdateChannel"/>.</summary>
public static class UpdateChannels
{
    /// <summary>The channel an install follows until the operator or user picks another (doc 49: the production default).</summary>
    public const UpdateChannel Default = UpdateChannel.Stable;

    /// <summary>The setting value: <c>stable</c>, <c>beta</c>, <c>nightly</c> or <c>lts</c>.</summary>
    public static string ToSettingValue(UpdateChannel channel) => channel switch
    {
        UpdateChannel.Stable => "stable",
        UpdateChannel.Beta => "beta",
        UpdateChannel.Nightly => "nightly",
        UpdateChannel.Lts => "lts",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown update channel."),
    };

    /// <summary>Parses a setting value, ignoring case and surrounding space. <c>alpha</c> is a deprecated alias for nightly. Unknown values fail.</summary>
    public static bool TryParse(string? value, out UpdateChannel channel)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "stable":
                channel = UpdateChannel.Stable;
                return true;
            case "beta":
                channel = UpdateChannel.Beta;
                return true;
            case "nightly":
            case "alpha":
                channel = UpdateChannel.Nightly;
                return true;
            case "lts":
                channel = UpdateChannel.Lts;
                return true;
            default:
                channel = Default;
                return false;
        }
    }
}
