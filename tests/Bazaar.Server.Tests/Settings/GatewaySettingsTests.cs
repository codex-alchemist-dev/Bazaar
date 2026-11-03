using System.Text.Json;
using Bazaar.Server.Gateway;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Tests.Settings;

/// <summary>Doc 05 "Settings": the 13 <c>gateway.*</c> keys, their defaults, types and approval rules. Pure: no database.</summary>
public sealed class GatewaySettingsTests
{
    private static readonly SettingActor _operator = new("operator:dom", SettingRole.Operator);

    private static SettingDefinition Lookup(string name)
    {
        Assert.True(SettingRegistry.Known.TryGet(name, out var definition), $"'{name}' is not registered.");
        return definition!;
    }

    private static SettingEvaluation Eval(SettingDefinition definition, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return definition.Evaluate(_operator, doc.RootElement);
    }

    private static string? Check(SettingDefinition definition, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return definition.CheckValue(doc.RootElement);
    }

    [Fact]
    public void Known_registry_holds_the_thirteen_gateway_keys_from_doc_05_once_each()
    {
        string[] expected =
        [
            "gateway.delegated_route.mapping", "gateway.claude.order", "gateway.claude.pooled_when_no_native_key",
            "gateway.gemini.order", "gateway.outbound_proxy.ui", "gateway.multi_key.ui", "gateway.free_tier.ack_mode",
            "gateway.multi_account.enabled", "gateway.catalogue.refresh_hours", "gateway.long_prompt.warn_tokens",
            "gateway.long_prompt.hard_cap_tokens", "gateway.freellmapi.warn_on_version_change", "gateway.usage.retention_days",
        ];

        var gateway = SettingRegistry.Known.Definitions
            .Where(d => d.Name.StartsWith("gateway.", StringComparison.Ordinal))
            .Select(d => d.Name)
            .ToList();

        Assert.Equal(13, gateway.Count);
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), gateway.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(gateway.Count, gateway.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(GatewaySettings.All.Count, gateway.Count);
    }

    [Fact]
    public void Gateway_keys_are_install_scope_and_operator_changed()
    {
        Assert.All(GatewaySettings.All, d => Assert.Equal(SettingScope.Install, d.Scope));
        Assert.All(GatewaySettings.All, d => Assert.Equal(SettingRole.Operator, d.WhoMayChange));
    }

    [Fact]
    public void Gateway_keys_are_not_in_the_host_registry()
    {
        Assert.DoesNotContain(SettingRegistry.Host.Definitions, d => d.Name.StartsWith("gateway.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("gateway.delegated_route.mapping", "\"one_route_per_profile\"", typeof(DelegatedRouteMapping))]
    [InlineData("gateway.claude.order", "\"native_first\"", typeof(ClaudeOrder))]
    [InlineData("gateway.claude.pooled_when_no_native_key", "true", typeof(bool))]
    [InlineData("gateway.gemini.order", "\"paid_first\"", typeof(GeminiOrder))]
    [InlineData("gateway.outbound_proxy.ui", "false", typeof(bool))]
    [InlineData("gateway.multi_key.ui", "false", typeof(bool))]
    [InlineData("gateway.free_tier.ack_mode", "\"required\"", typeof(FreeTierAckMode))]
    [InlineData("gateway.multi_account.enabled", "false", typeof(bool))]
    [InlineData("gateway.catalogue.refresh_hours", "0", typeof(int))]
    [InlineData("gateway.long_prompt.warn_tokens", "100000", typeof(int))]
    [InlineData("gateway.long_prompt.hard_cap_tokens", "0", typeof(int))]
    [InlineData("gateway.freellmapi.warn_on_version_change", "true", typeof(bool))]
    [InlineData("gateway.usage.retention_days", "0", typeof(int))]
    public void Each_key_has_its_documented_type_and_default(string name, string defaultJson, Type valueType)
    {
        var definition = Lookup(name);

        Assert.Equal(valueType, definition.ValueType);
        Assert.Equal(defaultJson, definition.DefaultValueJson);
        Assert.Null(Check(definition, definition.DefaultValueJson));
    }

    [Theory]
    [InlineData("gateway.delegated_route.mapping", "\"one_shared_route\"")]
    [InlineData("gateway.claude.order", "\"pooled_first\"")]
    [InlineData("gateway.gemini.order", "\"free_first\"")]
    [InlineData("gateway.free_tier.ack_mode", "\"required\"")]
    public void Enum_keys_use_snake_case_wire_names(string name, string json)
    {
        Assert.Equal(SettingWriteOutcome.Accepted, Eval(Lookup(name), json).Outcome);
    }

    [Fact]
    public void Free_tier_ack_mode_to_recorded_is_held_for_owner_approval()
    {
        var definition = Lookup("gateway.free_tier.ack_mode");

        var held = Eval(definition, "\"recorded\"");
        Assert.Equal(SettingWriteOutcome.PendingApproval, held.Outcome);
        Assert.Equal(ApprovalTier.Tier2, held.ApprovalTier);

        Assert.Equal(SettingWriteOutcome.Accepted, Eval(definition, "\"required\"").Outcome);
    }

    [Fact]
    public void Multi_account_enabled_is_held_for_owner_approval_and_disabled_is_applied()
    {
        var definition = Lookup("gateway.multi_account.enabled");

        var held = Eval(definition, "true");
        Assert.Equal(SettingWriteOutcome.PendingApproval, held.Outcome);
        Assert.Equal(ApprovalTier.Tier2, held.ApprovalTier);

        Assert.Equal(SettingWriteOutcome.Accepted, Eval(definition, "false").Outcome);
    }

    [Theory]
    [InlineData("1", SettingWriteOutcome.PendingApproval)]
    [InlineData("365", SettingWriteOutcome.PendingApproval)]
    [InlineData("0", SettingWriteOutcome.Accepted)]
    public void Usage_retention_is_held_for_owner_approval_only_when_non_zero(string json, SettingWriteOutcome expected)
    {
        var result = Eval(Lookup("gateway.usage.retention_days"), json);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(expected == SettingWriteOutcome.PendingApproval ? ApprovalTier.Tier2 : ApprovalTier.None, result.ApprovalTier);
    }

    [Fact]
    public void Keys_without_an_approval_flag_apply_at_once()
    {
        string[] unflagged =
        [
            "gateway.delegated_route.mapping", "gateway.claude.order", "gateway.claude.pooled_when_no_native_key",
            "gateway.gemini.order", "gateway.outbound_proxy.ui", "gateway.multi_key.ui", "gateway.catalogue.refresh_hours",
            "gateway.long_prompt.warn_tokens", "gateway.long_prompt.hard_cap_tokens", "gateway.freellmapi.warn_on_version_change",
        ];

        foreach (var name in unflagged)
            Assert.Equal(ApprovalTier.None, Lookup(name).ApprovalTier);

        Assert.Equal(ApprovalTier.Tier2, Lookup("gateway.free_tier.ack_mode").ApprovalTier);
        Assert.Equal(ApprovalTier.Tier2, Lookup("gateway.multi_account.enabled").ApprovalTier);
        Assert.Equal(ApprovalTier.Tier2, Lookup("gateway.usage.retention_days").ApprovalTier);
    }

    [Theory]
    [InlineData("gateway.catalogue.refresh_hours", "-1")]
    [InlineData("gateway.long_prompt.hard_cap_tokens", "-1")]
    [InlineData("gateway.usage.retention_days", "-1")]
    [InlineData("gateway.long_prompt.warn_tokens", "0")]
    public void Numeric_floors_reject_values_below_the_minimum(string name, string json)
    {
        Assert.Equal(SettingWriteOutcome.Rejected, Eval(Lookup(name), json).Outcome);
    }

    [Fact]
    public void Zero_is_the_documented_off_value_for_hard_cap_and_refresh()
    {
        Assert.Equal(SettingWriteOutcome.Accepted, Eval(Lookup("gateway.long_prompt.hard_cap_tokens"), "0").Outcome);
        Assert.Equal(SettingWriteOutcome.Accepted, Eval(Lookup("gateway.catalogue.refresh_hours"), "0").Outcome);
    }

    [Fact]
    public void Registry_refuses_a_gateway_key_registered_twice()
    {
        var key = GatewaySettings.CatalogueRefreshHours;

        Assert.Throws<ArgumentException>(() => new SettingRegistry([key, key]));
        Assert.Throws<ArgumentException>(() => new SettingRegistry([.. GatewaySettings.All, key]));
    }
}
