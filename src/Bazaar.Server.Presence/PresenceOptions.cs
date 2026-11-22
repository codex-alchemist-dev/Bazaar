using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.Presence;

/// <summary>How much of the model-written activity label reaches clients (<c>observatory.activity_label_mode</c>, doc 23).</summary>
public enum ActivityLabelMode
{
    /// <summary>Only the enumerated activity kind; the label is dropped.</summary>
    KindsOnly,

    /// <summary>The kind and a label of at most 80 characters.</summary>
    KindsAndLabel,
}

/// <summary>
/// Operator settings for presence (docs/design/23, Settings). Defaults are the doc defaults. They bind from <c>Bazaar:Presence</c>;
/// the settings registry takes over the same keys once a module can read it (the store lives in Bazaar.Server, which a module may
/// not reference). <c>presence.reorder_window_ms</c> is read by the client receiver, not here.
/// </summary>
public sealed class PresenceOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Bazaar:Presence";

    /// <summary><c>presence.idle_label</c> (owned by doc 09): <c>working</c> or <c>idle</c>.</summary>
    public string IdleLabel { get; set; } = "working";

    /// <summary><c>observatory.activity_label_mode</c>.</summary>
    public ActivityLabelMode ActivityLabelMode { get; set; } = ActivityLabelMode.KindsAndLabel;

    /// <summary>How long a row may go without a turn event before it shows <c>unknown</c> (doc 23, "Restart": 10 minutes).</summary>
    public TimeSpan UnknownAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the sweep looks for rows to mark <c>unknown</c>. [Not in the doc: an implementation choice.]</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a snapshot row lives in the cache. The cache is best effort, so a row that expires is simply a miss. [Not in the doc.]</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The key prefix of snapshot rows in the cache provider.</summary>
    public string CacheKeyPrefix { get; set; } = "presence:row:";
}

/// <summary>Reads and checks <see cref="PresenceOptions"/>. A value that does not parse or is out of range fails at start.</summary>
public static class PresenceOptionsReader
{
    /// <summary>Reads the section; absent keys keep the defaults.</summary>
    /// <exception cref="InvalidOperationException">A value does not parse or is out of range.</exception>
    public static PresenceOptions Read(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var o = new PresenceOptions();
        if (section["IdleLabel"] is { Length: > 0 } idle)
        {
            o.IdleLabel = idle;
        }

        if (section["ActivityLabelMode"] is { Length: > 0 } mode)
        {
            o.ActivityLabelMode = Enum.TryParse<ActivityLabelMode>(mode, true, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"{SectionKey("ActivityLabelMode")} must be KindsOnly or KindsAndLabel.");
        }

        o.UnknownAfter = Seconds(section, "UnknownAfterS", o.UnknownAfter);
        o.SweepInterval = Seconds(section, "SweepIntervalS", o.SweepInterval);
        o.CacheTtl = Seconds(section, "CacheTtlS", o.CacheTtl);
        Validate(o);
        return o;
    }

    /// <summary>Throws when <paramref name="o"/> holds a value outside its range.</summary>
    public static void Validate(PresenceOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (o.IdleLabel is not ("working" or "idle"))
        {
            throw new InvalidOperationException("presence.idle_label must be 'working' or 'idle'.");
        }

        if (o.UnknownAfter <= TimeSpan.Zero || o.SweepInterval <= TimeSpan.Zero || o.CacheTtl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Presence durations must be greater than zero.");
        }
    }

    private static string SectionKey(string key) => PresenceOptions.SectionName + ":" + key;

    private static TimeSpan Seconds(IConfigurationSection section, string key, TimeSpan fallback) =>
        section[key] is { Length: > 0 } text
            ? int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s > 0
                ? TimeSpan.FromSeconds(s)
                : throw new InvalidOperationException($"{SectionKey(key)} must be a whole number of seconds above zero.")
            : fallback;
}
