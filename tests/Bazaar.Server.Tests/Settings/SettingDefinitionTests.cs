using System.Text.Json;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Tests.Settings;

/// <summary>Registry contents, type checks, floors and approval rules. Pure: no database.</summary>
public sealed class SettingDefinitionTests
{
    private static readonly SettingActor Operator = new("operator:dom", SettingRole.Operator);
    private static readonly SettingActor AgentActor = new("agent:charles", SettingRole.Agent);

    private static readonly SettingKey<bool> Flag = new("test.flag", false, SettingScope.Install, SettingRole.Operator, "A test flag.");

    private static SettingEvaluation Eval(SettingDefinition definition, string json, SettingActor? actor = null)
    {
        using var doc = JsonDocument.Parse(json);
        return definition.Evaluate(actor ?? Operator, doc.RootElement);
    }

    private static string? Check(SettingDefinition definition, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return definition.CheckValue(doc.RootElement);
    }

    [Fact]
    public void Host_registry_holds_every_doc_03_key()
    {
        string[] expected =
        [
            "host.provider.service_host", "host.provider.vm", "host.provider.cache", "host.provider.search",
            "host.readiness.required_roles", "host.shutdown_timeout_seconds", "host.service.restart_delays_minutes",
            "host.service.failure_reset_hours", "host.maintenance_window", "host.db.startup_give_up_minutes",
            "host.status.bind", "host.status.port", "host.service_account", "host.log.retention_days",
            "host.log.max_total_mb", "host.health.disk_used_warn_percent", "host.health.memory_growth_warn_percent",
        ];

        var names = SettingRegistry.Host.Definitions.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), names);
        Assert.All(SettingRegistry.Host.Definitions, d => Assert.Equal(SettingScope.Install, d.Scope));
        Assert.All(SettingRegistry.Host.Definitions, d => Assert.Equal(SettingRole.Operator, d.WhoMayChange));
    }

    [Fact]
    public void Every_registered_default_passes_its_own_check()
    {
        foreach (var definition in SettingRegistry.Host.Definitions)
            Assert.Null(Check(definition, definition.DefaultValueJson));
    }

    [Fact]
    public void Registry_refuses_duplicate_names()
    {
        var other = new SettingKey<bool>("test.flag", true, SettingScope.Install, SettingRole.Operator, "Duplicate.");
        Assert.Throws<ArgumentException>(() => new SettingRegistry([Flag, other]));
    }

    [Fact]
    public void Key_refuses_an_invalid_default()
    {
        Assert.Throws<ArgumentException>(() => new SettingKey<int>(
            "test.bad_default", 0, SettingScope.Install, SettingRole.Operator, "Bad.", validate: v => v > 0 ? null : "must be positive"));
    }

    [Fact]
    public void Bool_accepts_true_and_rejects_text()
    {
        Assert.Equal(SettingWriteOutcome.Accepted, Eval(Flag, "true").Outcome);
        Assert.Equal(SettingWriteOutcome.Rejected, Eval(Flag, "\"true\"").Outcome);
        Assert.Equal(SettingWriteOutcome.Rejected, Eval(Flag, "1").Outcome);
    }

    [Fact]
    public void Int_rejects_text_fractions_and_null()
    {
        var timeout = HostSettings.ShutdownTimeoutSeconds;
        Assert.Equal(SettingWriteOutcome.Rejected, Eval(timeout, "\"thirty\"").Outcome);
        Assert.Equal(SettingWriteOutcome.Rejected, Eval(timeout, "\"30\"").Outcome);
        Assert.Equal(SettingWriteOutcome.Rejected, Eval(timeout, "30.5").Outcome);
        Assert.Equal(SettingWriteOutcome.Rejected, Eval(timeout, "null").Outcome);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("121")]
    public void Int_range_is_enforced_with_a_clear_reason(string json)
    {
        var result = Eval(HostSettings.ShutdownTimeoutSeconds, json);
        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Contains("from 5 to 120", result.Reason);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("120")]
    public void Int_range_bounds_are_inclusive(string json)
    {
        Assert.Equal(SettingWriteOutcome.Accepted, Eval(HostSettings.ShutdownTimeoutSeconds, json).Outcome);
    }

    [Fact]
    public void Enum_uses_snake_case_names_and_rejects_unknown_names_and_integers()
    {
        var accepted = Eval(HostSettings.ProviderServiceHost, "\"windows_service\"");
        Assert.Equal(SettingWriteOutcome.Accepted, accepted.Outcome);
        Assert.Equal("\"windows_service\"", accepted.ValueJson);

        var unknown = Eval(HostSettings.ProviderServiceHost, "\"bogus\"");
        Assert.Equal(SettingWriteOutcome.Rejected, unknown.Outcome);
        Assert.Contains("host.provider.service_host", unknown.Reason);

        Assert.Equal(SettingWriteOutcome.Rejected, Eval(HostSettings.ProviderServiceHost, "0").Outcome);
    }

    [Fact]
    public void Actor_without_the_required_role_is_rejected()
    {
        var result = Eval(HostSettings.ShutdownTimeoutSeconds, "30", AgentActor);
        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Contains("only by Operator", result.Reason);
    }

    [Theory]
    [InlineData("\"Administrator\"")]
    [InlineData("\"administrator\"")]
    [InlineData("\"BUILTIN\\\\Administrator\"")]
    [InlineData("\"DESKTOP-1\\\\ADMINISTRATOR\"")]
    [InlineData("\"Administrator@nuc\"")]
    public void Service_account_floor_rejects_administrator(string json)
    {
        var result = Eval(HostSettings.ServiceAccount, json);
        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Contains("never be Administrator", result.Reason);
    }

    [Fact]
    public void Service_account_change_that_clears_the_floor_is_held_for_owner_approval()
    {
        var result = Eval(HostSettings.ServiceAccount, "\"bazaar-ops\"");
        Assert.Equal(SettingWriteOutcome.PendingApproval, result.Outcome);
        Assert.Equal(ApprovalTier.Tier2, result.ApprovalTier);
    }

    [Fact]
    public void Service_account_rejects_blank_names()
    {
        Assert.Equal(SettingWriteOutcome.Rejected, Eval(HostSettings.ServiceAccount, "\"  \"").Outcome);
    }

    [Fact]
    public void Status_bind_loopback_is_accepted_and_anything_wider_is_held()
    {
        Assert.Equal(SettingWriteOutcome.Accepted, Eval(HostSettings.StatusBindAddress, "\"loopback\"").Outcome);

        foreach (var wider in new[] { "\"lan\"", "\"tailscale\"" })
        {
            var result = Eval(HostSettings.StatusBindAddress, wider);
            Assert.Equal(SettingWriteOutcome.PendingApproval, result.Outcome);
            Assert.Equal(ApprovalTier.Tier2, result.ApprovalTier);
        }
    }

    [Fact]
    public void Postgres_is_a_floor_for_required_roles()
    {
        var withoutPostgres = Eval(HostSettings.ReadinessRequiredRoles, "[\"search\", \"service_host\"]");
        Assert.Equal(SettingWriteOutcome.Rejected, withoutPostgres.Outcome);
        Assert.Contains("postgres", withoutPostgres.Reason);

        var tighter = Eval(HostSettings.ReadinessRequiredRoles, "[\"postgres\", \"search\", \"service_host\", \"cache\"]");
        Assert.Equal(SettingWriteOutcome.Accepted, tighter.Outcome);
    }

    [Theory]
    [InlineData("[\"postgres\", \"tape_drive\"]", "unknown readiness role")]
    [InlineData("[\"postgres\", \"cache\", \"cache\"]", "listed twice")]
    public void Required_roles_reject_unknown_and_duplicate_names(string json, string reason)
    {
        var result = Eval(HostSettings.ReadinessRequiredRoles, json);
        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Contains(reason, result.Reason);
    }

    [Theory]
    [InlineData("\"04:00\"", SettingWriteOutcome.Accepted)]
    [InlineData("\"23:59\"", SettingWriteOutcome.Accepted)]
    [InlineData("\"25:00\"", SettingWriteOutcome.Rejected)]
    [InlineData("\"4:00\"", SettingWriteOutcome.Rejected)]
    [InlineData("\"noon\"", SettingWriteOutcome.Rejected)]
    public void Maintenance_window_must_be_local_time(string json, SettingWriteOutcome expected)
    {
        Assert.Equal(expected, Eval(HostSettings.MaintenanceWindow, json).Outcome);
    }

    [Theory]
    [InlineData("[]", SettingWriteOutcome.Rejected)]
    [InlineData("[1, 1, 5]", SettingWriteOutcome.Accepted)]
    [InlineData("[-1]", SettingWriteOutcome.Rejected)]
    [InlineData("[1441]", SettingWriteOutcome.Rejected)]
    public void Restart_delays_are_a_short_list_of_minutes(string json, SettingWriteOutcome expected)
    {
        Assert.Equal(expected, Eval(HostSettings.ServiceRestartDelaysMinutes, json).Outcome);
    }

    [Fact]
    public void Status_port_zero_is_the_unset_placeholder_and_real_ports_must_be_in_range()
    {
        Assert.Equal(SettingWriteOutcome.Accepted, Eval(HostSettings.StatusPort, "0").Outcome);
        Assert.Equal(SettingWriteOutcome.Accepted, Eval(HostSettings.StatusPort, "8080").Outcome);
        Assert.Equal(SettingWriteOutcome.Rejected, Eval(HostSettings.StatusPort, "70000").Outcome);
        Assert.Equal(SettingWriteOutcome.Rejected, Eval(HostSettings.StatusPort, "-1").Outcome);
    }

    [Fact]
    public void Accepted_evaluation_carries_the_canonical_value()
    {
        var result = Eval(HostSettings.ReadinessRequiredRoles, "[ \"postgres\" ,\"cache\" ]");
        Assert.Equal(SettingWriteOutcome.Accepted, result.Outcome);
        Assert.Equal("[\"postgres\",\"cache\"]", result.ValueJson);
    }
}
