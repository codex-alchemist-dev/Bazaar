using Bazaar.Server.Settings;

namespace Bazaar.Server.Gateway;

/// <summary>
/// The <c>gateway.*</c> settings from doc 05 ("Settings"), all install scope and operator-changed. Defaults follow the table.
/// Keys marked "needs counsel" keep their protective default; a change that lowers one needs owner approval (Tier 2).
/// Floors live in the validators, so they hold for every write path.
/// </summary>
public static class GatewaySettings
{
    public static readonly SettingKey<DelegatedRouteMapping> DelegatedRouteMappingKey = new(
        "gateway.delegated_route.mapping", DelegatedRouteMapping.OneRoutePerProfile, SettingScope.Install, SettingRole.Operator,
        "Delegated routes: one route per profile, or one route shared by all profiles.");

    public static readonly SettingKey<ClaudeOrder> ClaudeOrderKey = new(
        "gateway.claude.order", ClaudeOrder.NativeFirst, SettingScope.Install, SettingRole.Operator,
        "Claude lane: try the owner's native API key first, or the pooled free source first.");

    public static readonly SettingKey<bool> ClaudePooledWhenNoNativeKey = new(
        "gateway.claude.pooled_when_no_native_key", true, SettingScope.Install, SettingRole.Operator,
        "Claude lane: use the pooled source when no native API key is configured.");

    public static readonly SettingKey<GeminiOrder> GeminiOrderKey = new(
        "gateway.gemini.order", GeminiOrder.PaidFirst, SettingScope.Install, SettingRole.Operator,
        "Gemini: try paid keys first, or free keys first.");

    public static readonly SettingKey<bool> OutboundProxyUi = new(
        "gateway.outbound_proxy.ui", false, SettingScope.Install, SettingRole.Operator,
        "Show the outbound proxy settings in the setup wizard and advanced settings.");

    public static readonly SettingKey<bool> MultiKeyUi = new(
        "gateway.multi_key.ui", false, SettingScope.Install, SettingRole.Operator,
        "Show the multiple-keys-per-provider settings in the setup wizard and advanced settings.");

    // TODO(component): doc 05 says this flag is per provider. The registry has no per-provider key shape yet; this is one
    // install-wide bool until the component model (PR #125, not merged) defines provider components.
    public static readonly SettingKey<bool> MultiAccountEnabled = new(
        "gateway.multi_account.enabled", false, SettingScope.Install, SettingRole.Operator,
        "Allow multi-account use. Needs counsel and provider-terms review before enabling; enabling needs owner approval (Tier 2).",
        approvalTier: ApprovalTier.Tier2,
        requiresApprovalFor: enabled => enabled);

    public static readonly SettingKey<FreeTierAckMode> FreeTierAckModeKey = new(
        "gateway.free_tier.ack_mode", FreeTierAckMode.Required, SettingScope.Install, SettingRole.Operator,
        "Free-tier data terms: required acknowledgement, or recorded (needs counsel). Recording needs owner approval (Tier 2).",
        approvalTier: ApprovalTier.Tier2,
        requiresApprovalFor: mode => mode == FreeTierAckMode.Recorded);

    public static readonly SettingKey<int> CatalogueRefreshHours = new(
        "gateway.catalogue.refresh_hours", 0, SettingScope.Install, SettingRole.Operator,
        "Hours between automatic catalogue refreshes. 0 means only release updates and the manual check.",
        validate: AtLeast(0));

    public static readonly SettingKey<int> LongPromptWarnTokens = new(
        "gateway.long_prompt.warn_tokens", 100000, SettingScope.Install, SettingRole.Operator,
        "Prompt size in tokens above which the gateway emits a warning event.",
        validate: AtLeast(1));

    public static readonly SettingKey<int> LongPromptHardCapTokens = new(
        "gateway.long_prompt.hard_cap_tokens", 0, SettingScope.Install, SettingRole.Operator,
        "Prompt size in tokens above which a call is refused. 0 means off.",
        validate: AtLeast(0));

    public static readonly SettingKey<bool> FreeLlmApiWarnOnVersionChange = new(
        "gateway.freellmapi.warn_on_version_change", true, SettingScope.Install, SettingRole.Operator,
        "Warn when the FreeLLMAPI version changes.");

    public static readonly SettingKey<int> UsageRetentionDays = new(
        "gateway.usage.retention_days", 0, SettingScope.Install, SettingRole.Operator,
        "Days of usage records kept. 0 means indefinite (needs counsel). Setting a non-zero value needs owner approval (Tier 2).",
        approvalTier: ApprovalTier.Tier2,
        validate: AtLeast(0),
        requiresApprovalFor: days => days != 0);

    /// <summary>Every gateway key, in doc 05 table order.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        DelegatedRouteMappingKey,
        ClaudeOrderKey,
        ClaudePooledWhenNoNativeKey,
        GeminiOrderKey,
        OutboundProxyUi,
        MultiKeyUi,
        FreeTierAckModeKey,
        MultiAccountEnabled,
        CatalogueRefreshHours,
        LongPromptWarnTokens,
        LongPromptHardCapTokens,
        FreeLlmApiWarnOnVersionChange,
        UsageRetentionDays,
    ];

    private static Func<int, string?> AtLeast(int min) =>
        value => value >= min ? null : $"must be at least {min}; got {value}.";
}
