using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Settings;

/// <summary>Actions that Tier 3 covers (doc 15 <c>security.tier3.actions</c>). Wire form is snake_case.</summary>
public enum Tier3Action
{
    Captcha,
    Sms,
    Id,
    Passkeys,
    RecoveryCodes,
    LegalTerms,
}

/// <summary>Where audit data may be exported (doc 15 <c>security.audit.export_target</c>). Off-host needs counsel.</summary>
public enum AuditExportTarget
{
    Local,
    OffHost,
    None,
}

/// <summary>
/// The <c>security.*</c> settings from the doc 15 settings table (DECISIONS-LOG round 13). Every key needs owner approval
/// (Tier 2), so a write is held until approval exists. The floors that bound these values are in <see cref="SecurityFloors"/>
/// and are enforced by <see cref="FloorCheckingSettingStore"/>. The skimmer key from that table belongs to doc 13 and is not here.
/// </summary>
public static class SecuritySettings
{
    private static readonly SettingRole _operatorOrOwner = SettingRole.Operator | SettingRole.Owner;

    /// <summary>The default Tier 3 list, in doc 15 order.</summary>
    public static readonly IReadOnlyList<Tier3Action> DefaultTier3Actions =
    [
        Tier3Action.Captcha,
        Tier3Action.Sms,
        Tier3Action.Id,
        Tier3Action.Passkeys,
        Tier3Action.RecoveryCodes,
        Tier3Action.LegalTerms,
    ];

    /// <summary>The settings as definitions, with the given approval tier. The shipped set uses <see cref="ApprovalTier.Tier2"/>.</summary>
    /// <remarks>
    /// Tests build the same keys with <see cref="ApprovalTier.None"/> so an accepted write can be exercised before the
    /// approval flow exists. The floor check looks keys up by name, so the copies behave the same.
    /// </remarks>
    public static IReadOnlyList<SettingDefinition> Build(ApprovalTier approval) =>
    [
        new SettingKey<int>("security.quota.public_items_per_platform_day", 5, SettingScope.Install, _operatorOrOwner,
            "Public items per platform per day.", approval, validate: AtLeast(0)),
        new SettingKey<int>("security.quota.public_issue_pr_per_repo_day", 1, SettingScope.Install, _operatorOrOwner,
            "Public issues and PRs per repo per day.", approval, validate: AtLeast(0)),
        new SettingKey<int>("security.quota.new_repos_per_week", 3, SettingScope.Install, _operatorOrOwner,
            "New repositories per week.", approval, validate: AtLeast(0)),
        new SettingKey<int>("security.quota.mail_sent_per_day", 20, SettingScope.Agent, _operatorOrOwner,
            "Mail sent per agent per day.", approval, validate: AtLeast(0)),
        new SettingKey<int>("security.quota.github_writes_per_minute", 30, SettingScope.Install, _operatorOrOwner,
            "GitHub writes per minute.", approval, validate: AtLeast(0)),
        new SettingKey<int>("security.approval.request_cap_per_agent_hour", 10, SettingScope.Agent, _operatorOrOwner,
            "Approval requests per agent per hour.", approval, validate: AtLeast(0)),
        new SettingKey<int>("security.approval.expiry_hours", 24, SettingScope.Install, _operatorOrOwner,
            "Hours before an approval request expires.", approval, validate: AtLeast(1)),
        new SettingKey<int>("security.approval.task_scope_minutes", 60, SettingScope.Install, _operatorOrOwner,
            "Minutes a task-scoped approval lasts. Floored at the maximum (see SecurityFloors).", approval, validate: AtLeast(1)),
        new SettingKey<Tier2WhenUntainted>("security.approval.tier2_when_untainted", Tier2WhenUntainted.Always, SettingScope.Install, _operatorOrOwner,
            "Whether Tier 2 asks for approval on every call (always) or only under hard taint (hard_taint_only).", approval),
        new SettingKey<IReadOnlyList<Tier3Action>>("security.tier3.actions", DefaultTier3Actions, SettingScope.Install, _operatorOrOwner,
            "Actions that need Tier 3 approval. Passkeys and recovery codes must stay in the list.", approval, validate: CheckDistinct),
        new SettingKey<int>("security.push.own_branch_untainted_tier", 0, SettingScope.Agent, _operatorOrOwner,
            "Approval tier for an untainted push to the agent's own branch. Merges stay Tier 2 regardless.", approval, validate: AtLeast(0)),
        new SettingKey<IReadOnlyList<string>>("security.egress.default_allowlist", ["package_feed", "github_api", "model_gateway"], SettingScope.Install, _operatorOrOwner,
            "Default egress allowlist: package feed, GitHub API, model gateway.", approval, validate: CheckNames),
        new SettingKey<bool>("security.backup.include_cookie_profiles", false, SettingScope.Install, _operatorOrOwner,
            "Include browser cookie profiles in backups. Needs counsel.", approval),
        new SettingKey<AuditExportTarget>("security.audit.export_target", AuditExportTarget.Local, SettingScope.Install, _operatorOrOwner,
            "Where audit data is exported. Off-host sends audit data out of the install; needs counsel.", approval),
        new SettingKey<int>("security.audit.export_interval_hours", 24, SettingScope.Install, _operatorOrOwner,
            "Hours between audit exports.", approval, validate: AtLeast(1)),
        new SettingKey<bool>("security.scam_mail.protocol_enabled", false, SettingScope.Install, _operatorOrOwner,
            "Whether the Nigerian Prince Protocol is enabled (risk wizard).", approval),
    ];

    /// <summary>The shipped set: every key needs owner approval.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } = Build(ApprovalTier.Tier2);

    /// <summary>True when <paramref name="key"/> is a <c>security.*</c> name, in the doc 15 table or not.</summary>
    public static bool IsSecurityName(string key) => key.StartsWith("security.", StringComparison.Ordinal);

    /// <summary>True when <paramref name="key"/> is one of the doc 15 table keys.</summary>
    public static bool IsKnown(string key) => All.Any(d => string.Equals(d.Name, key, StringComparison.Ordinal));

    /// <summary>Finds a shipped definition by name.</summary>
    public static bool TryFind(string key, out SettingDefinition? definition)
    {
        definition = All.FirstOrDefault(d => string.Equals(d.Name, key, StringComparison.Ordinal));
        return definition is not null;
    }

    private static Func<int, string?> AtLeast(int min) =>
        value => value >= min ? null : $"must be at least {min}; got {value}.";

    private static string? CheckDistinct(IReadOnlyList<Tier3Action> actions) =>
        actions.Distinct().Count() == actions.Count ? null : "an action is listed twice.";

    private static string? CheckNames(IReadOnlyList<string> names)
    {
        if (names.Any(string.IsNullOrWhiteSpace))
            return "an allowlist entry is empty.";
        return names.Distinct(StringComparer.Ordinal).Count() == names.Count ? null : "an allowlist entry is listed twice.";
    }
}
