using System.Text.Json;

namespace Bazaar.Server.Setup;

/// <summary>One install setting a plan sets, by registry key, with the plan path it came from.</summary>
/// <param name="Key">The registry key (doc 03), for example <c>host.status.bind</c>.</param>
/// <param name="PlanPath">The dotted plan path the value comes from, for example <c>settings.host.shutdown_timeout_seconds</c> or <c>network.statusBind</c>.</param>
/// <param name="Value">The value as the registry accepts it, before the registry canonicalizes it.</param>
public sealed record SetupProjectedSetting(string Key, string PlanPath, JsonElement Value);

/// <summary>
/// The install-scope settings a plan sets (doc 45 section 1.2: "the settings values (registry keys from doc 03 and setup-only keys)").
/// They are the <c>settings</c> map plus four plan fields that map to one registry key each: <c>network.statusBind</c> and
/// <c>network.statusPort</c> (doc 03 <c>host.status.*</c>), <c>scamMailProtocol.enabled</c> (doc 15 <c>security.scam_mail.protocol_enabled</c>)
/// (only when on) and <c>install.releaseChannel</c> (doc 30 <c>update.channel</c>, wire form snake_case). The apply step writes them and the applier audits them from
/// this one list, so both agree on what a plan changes. Pure: nothing is stored.
/// </summary>
public static class SetupSettingProjection
{
    /// <summary>Registry key set by <c>network.statusBind</c>.</summary>
    public const string StatusBindKey = "host.status.bind";

    /// <summary>Registry key set by <c>network.statusPort</c>.</summary>
    public const string StatusPortKey = "host.status.port";

    /// <summary>Registry key set by <c>scamMailProtocol.enabled</c>.</summary>
    public const string ScamMailKey = "security.scam_mail.protocol_enabled";

    /// <summary>Registry key set by <c>install.releaseChannel</c>.</summary>
    public const string UpdateChannelKey = "update.channel";

    /// <summary>The registry key a derived plan path maps to, or null when the path is not one of the four mapped plan fields.</summary>
    public static string? DerivedKeyForPath(string planPath) => planPath switch
    {
        "network.statusBind" => StatusBindKey,
        "network.statusPort" => StatusPortKey,
        "scamMailProtocol.enabled" => ScamMailKey,
        "install.releaseChannel" => UpdateChannelKey,
        _ => null,
    };

    /// <summary>Every setting the plan sets, sorted by key (ordinal). A key set in both places is the validator's error, so it appears once here.</summary>
    public static IReadOnlyList<SetupProjectedSetting> Project(SetupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var projected = new SortedDictionary<string, SetupProjectedSetting>(StringComparer.Ordinal);
        foreach (var (key, value) in plan.Settings)
        {
            projected[key] = new SetupProjectedSetting(key, "settings." + key, value);
        }

        Add(projected, StatusBindKey, "network.statusBind", Parse(JsonSerializer.Serialize(plan.Network.StatusBind, SetupPlanJsonContext.Default.String)));
        Add(projected, StatusPortKey, "network.statusPort", Parse(JsonSerializer.Serialize(plan.Network.StatusPort, SetupPlanJsonContext.Default.Int32)));
        if (plan.ScamMailProtocol.Enabled)
        {
            // Off is the default and the safe state, and the registry holds even an explicit write of the key for owner approval, so
            // an off plan writes nothing. Turning it on is a held plan that never reaches the apply (doc 45 section 1.11).
            Add(projected, ScamMailKey, "scamMailProtocol.enabled", Parse("true"));
        }

        Add(projected, UpdateChannelKey, "install.releaseChannel", Parse(JsonSerializer.Serialize(WireChannel(plan.Install.ReleaseChannel), SetupPlanJsonContext.Default.String)));
        return [.. projected.Values];
    }

    private static void Add(SortedDictionary<string, SetupProjectedSetting> projected, string key, string path, JsonElement value) =>
        projected.TryAdd(key, new SetupProjectedSetting(key, path, value));

    /// <summary>The registry's enum wire form is snake_case, so the plan's <c>latest-commit</c> is <c>latest_commit</c>.</summary>
    private static string WireChannel(string channel) => channel.Replace('-', '_');

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
