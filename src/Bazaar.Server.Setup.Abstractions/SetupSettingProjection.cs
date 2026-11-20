using System.Text.Json;

namespace Bazaar.Server.Setup;

/// <summary>One install setting a plan sets, by registry key, with the plan path it came from.</summary>
/// <param name="Key">The registry key (doc 03), for example <c>host.status.bind</c>.</param>
/// <param name="PlanPath">The dotted plan path the value comes from, for example <c>settings.host.shutdown_timeout_seconds</c> or <c>network.statusBind</c>.</param>
/// <param name="Value">The value as the registry accepts it, before the registry canonicalizes it.</param>
public sealed record SetupProjectedSetting(string Key, string PlanPath, JsonElement Value);

/// <summary>
/// The install-scope settings a plan sets (doc 45 section 1.2: "the settings values (registry keys from doc 03 and setup-only keys)").
/// They are the <c>settings</c> map plus the plan fields that map to registry keys: <c>network.statusBind</c> and
/// <c>network.statusPort</c> (doc 03 <c>host.status.*</c>), <c>scamMailProtocol.enabled</c> (doc 15 <c>security.scam_mail.protocol_enabled</c>)
/// (only when on) <c>install.releaseChannel</c> (doc 30 <c>update.channel</c>, wire form snake_case), <c>install.updatePolicy.security</c> (doc 42 <c>deps.update.autoSecurityPatches</c>),
/// the backups section (<c>backup.target</c>, <c>backup.drill_interval_days</c>, <c>backup.no_target_acknowledged</c>; doc 33) and the email section
/// (<c>mail.enabled</c>, <c>mail.backend</c>; doc 22). The apply step writes them and the applier audits them from
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

    /// <summary>Registry key set by <c>install.updatePolicy.security</c> (doc 42 section 10; <c>automatic</c> is true).</summary>
    public const string AutoSecurityPatchesKey = "deps.update.autoSecurityPatches";

    /// <summary>Registry key set by <c>custody.mergeCredential.kind</c> (doc 14 <c>identity.merge_credential</c>; <c>owner_linked</c> is stored as <c>linked_user</c>).</summary>
    public const string MergeCredentialKey = "identity.merge_credential";

    /// <summary>Registry key set by <c>custody.mergeCredential.appIdRef</c>: a <c>secret://bazaar/</c> reference, or empty when there is none. Never a value.</summary>
    public const string MergeCredentialAppIdRefKey = "identity.merge_credential.app_id_ref";

    /// <summary>Registry key set by <c>models.defaultPreset</c>.</summary>
    public const string DefaultPresetKey = "gateway.default_preset";

    /// <summary>Registry key set by <c>company.source</c>.</summary>
    public const string CompanySourceKey = "company.source";

    /// <summary>Registry key set by <c>backups.target</c> (empty when the plan has none).</summary>
    public const string BackupTargetKey = "backup.target";

    /// <summary>Registry key set by <c>backups.drillIntervalDays</c>.</summary>
    public const string BackupDrillIntervalKey = "backup.drill_interval_days";

    /// <summary>Registry key set by <c>backups.acknowledgeNoTarget</c>, true only when the plan has no target and acknowledges it.</summary>
    public const string BackupNoTargetAcknowledgedKey = "backup.no_target_acknowledged";

    /// <summary>Registry key set by <c>email.enabled</c>.</summary>
    public const string MailEnabledKey = "mail.enabled";

    /// <summary>Registry key set by <c>email.backend</c> (wire form snake_case; <c>none</c> while email is off).</summary>
    public const string MailBackendKey = "mail.backend";

    /// <summary>The back end ids an enabled email section may name (doc 22 back ends table), in plan form.</summary>
    public static readonly IReadOnlyList<string> MailBackends = ["bazaar_client", "imap_smtp", "cloudflare", "builtin_server"];

    /// <summary>The registry keys a derived plan path feeds, or empty when the path is not one of the mapped plan fields.</summary>
    public static IReadOnlyList<string> DerivedKeysForPath(string planPath) => planPath switch
    {
        "network.statusBind" => [StatusBindKey],
        "network.statusPort" => [StatusPortKey],
        "scamMailProtocol.enabled" => [ScamMailKey],
        "install.releaseChannel" => [UpdateChannelKey],
        "install.updatePolicy.security" => [AutoSecurityPatchesKey],
        "custody.mergeCredential.kind" => [MergeCredentialKey],
        "custody.mergeCredential.appIdRef" => [MergeCredentialAppIdRefKey],
        "models.defaultPreset" => [DefaultPresetKey],
        "company.source" => [CompanySourceKey],
        "backups.target" => [BackupTargetKey, BackupNoTargetAcknowledgedKey],
        "backups.acknowledgeNoTarget" => [BackupNoTargetAcknowledgedKey],
        "backups.drillIntervalDays" => [BackupDrillIntervalKey],
        "email.enabled" => [MailEnabledKey, MailBackendKey],
        "email.backend" => [MailBackendKey],
        _ => [],
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
        // Doc 45 step 2. Only the security half has a registry key. The "other" half has none (every non-security update is manual
        // and nothing stores an automatic choice), so it is not projected and the apply step refuses "automatic" (see IsOtherUpdatePolicyBacked).
        Add(projected, AutoSecurityPatchesKey, "install.updatePolicy.security", Parse(string.Equals(plan.Install.UpdatePolicy.Security, "automatic", StringComparison.Ordinal) ? "true" : "false"));

        // Doc 45 steps 9, 10 and 13. The kind and the preset are always written, so a re-run that goes back to the default also resets
        // an earlier explicit row. The app id reference is written only when the plan carries one (owner_app); switching to the linked
        // credential leaves the old reference unread rather than blank. A blank company source is not a choice, so it writes nothing.
        Add(projected, MergeCredentialKey, "custody.mergeCredential.kind", Parse(JsonSerializer.Serialize(WireMergeKind(plan.Custody.MergeCredential.Kind), SetupPlanJsonContext.Default.String)));
        if (!string.IsNullOrEmpty(plan.Custody.MergeCredential.AppIdRef))
        {
            Add(projected, MergeCredentialAppIdRefKey, "custody.mergeCredential.appIdRef", Parse(JsonSerializer.Serialize(plan.Custody.MergeCredential.AppIdRef, SetupPlanJsonContext.Default.String)));
        }

        Add(projected, DefaultPresetKey, "models.defaultPreset", Parse(JsonSerializer.Serialize(plan.Models.DefaultPreset, SetupPlanJsonContext.Default.String)));
        if (!string.IsNullOrEmpty(plan.Company.Source))
        {
            Add(projected, CompanySourceKey, "company.source", Parse(JsonSerializer.Serialize(plan.Company.Source, SetupPlanJsonContext.Default.String)));
        }


        // Doc 45 step 8 (doc 33). The target is empty when the plan has none. The acknowledgement counts only for a plan with no target.
        var noTarget = plan.Backups.Target is null;
        Add(projected, BackupTargetKey, "backups.target", Parse(JsonSerializer.Serialize(plan.Backups.Target ?? string.Empty, SetupPlanJsonContext.Default.String)));
        Add(projected, BackupDrillIntervalKey, "backups.drillIntervalDays", Parse(JsonSerializer.Serialize(plan.Backups.DrillIntervalDays, SetupPlanJsonContext.Default.Int32)));
        Add(projected, BackupNoTargetAcknowledgedKey, "backups.acknowledgeNoTarget", Parse(noTarget && plan.Backups.AcknowledgeNoTarget ? "true" : "false"));

        // Doc 45 step 11 (doc 22). The back end is stored only while email is on, so an off plan leaves "none".
        Add(projected, MailEnabledKey, "email.enabled", Parse(plan.Email.Enabled ? "true" : "false"));
        Add(projected, MailBackendKey, "email.backend", Parse(JsonSerializer.Serialize(plan.Email.Enabled ? plan.Email.Backend : "none", SetupPlanJsonContext.Default.String)));
        return [.. projected.Values];
    }

    /// <summary>
    /// True when the email section is coherent: off with no back end, or on with one of <see cref="MailBackends"/>. The registry checks
    /// each key alone, so this rule across the two keys lives here, for the validator and the apply step both.
    /// </summary>
    public static bool IsEmailChoiceCoherent(SetupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Email.Enabled
            ? MailBackends.Contains(plan.Email.Backend, StringComparer.Ordinal)
            : plan.Email.Backend.Length == 0;
    }

    /// <summary>
    /// True when the plan's <c>install.updatePolicy.other</c> can be honoured: only <c>manual</c> can, because no store holds an automatic
    /// choice for non-security updates. A plan that asks for <c>automatic</c> there cannot be applied honestly.
    /// </summary>
    public static bool IsOtherUpdatePolicyBacked(SetupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return string.Equals(plan.Install.UpdatePolicy.Other, "manual", StringComparison.Ordinal);
    }

    private static void Add(SortedDictionary<string, SetupProjectedSetting> projected, string key, string path, JsonElement value) =>
        projected.TryAdd(key, new SetupProjectedSetting(key, path, value));

    /// <summary>The registry's enum wire form is snake_case, so the plan's <c>latest-commit</c> is <c>latest_commit</c>.</summary>
    private static string WireChannel(string channel) => channel.Replace('-', '_');

    /// <summary>The registry's merge credential enum is <c>owner_app</c> or <c>linked_user</c>; the plan says <c>owner_linked</c> for the second.</summary>
    private static string WireMergeKind(string kind) => string.Equals(kind, "owner_linked", StringComparison.Ordinal) ? "linked_user" : kind;

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
