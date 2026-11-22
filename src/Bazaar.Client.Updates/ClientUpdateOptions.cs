using System.Text.RegularExpressions;
using Bazaar.Updates;

namespace Bazaar.Client.Updates;

/// <summary>
/// Updater settings, bound from configuration. No feed URL is built in: the operator (or the company build, doc 37) sets
/// <see cref="FeedBaseUrl"/> and may override single channels in <see cref="ChannelFeeds"/>.
/// </summary>
public sealed partial class ClientUpdateOptions
{
    /// <summary>The channel to follow. Defaults to stable.</summary>
    public UpdateChannel Channel { get; set; } = UpdateChannels.Default;

    /// <summary>
    /// Base URL of the Velopack feed directory. Every channel shares it, because Velopack names its feed file after the
    /// channel (<c>releases.win-x64-beta.json</c>). HTTPS, or HTTP on loopback.
    /// </summary>
    public Uri? FeedBaseUrl { get; set; }

    /// <summary>Per-channel feed overrides, for operators that host channels in different places.</summary>
    public Dictionary<UpdateChannel, Uri> ChannelFeeds { get; } = [];

    /// <summary>The Velopack target runtime id the packages were packed for (<c>pack-client.ps1 -Runtime</c>).</summary>
    public string Runtime { get; set; } = "win-x64";

    /// <summary>The feed for a channel: its override, else the shared base. Null when neither is configured.</summary>
    public Uri? ResolveFeed(UpdateChannel channel) =>
        ChannelFeeds.TryGetValue(channel, out var feed) ? feed : FeedBaseUrl;

    /// <summary>
    /// The Velopack channel name for a bazaar channel: <c>&lt;runtime&gt;-&lt;channel&gt;</c>, the same name
    /// <c>packaging/velopack/pack-client.ps1</c> gives <c>vpk pack --channel</c>.
    /// </summary>
    public string VelopackChannel(UpdateChannel channel)
    {
        if (!RuntimePattern().IsMatch(Runtime))
        {
            throw new InvalidOperationException($"Runtime '{Runtime}' is not a valid RID.");
        }

        return $"{Runtime}-{UpdateChannels.ToSettingValue(channel)}";
    }

    /// <summary>True when the feed passes the same transport rule as a package URL.</summary>
    public static bool IsSecureFeed(Uri feed) => UpdateDecision.IsSecureTransport(feed);

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex RuntimePattern();
}
