using System.Text.Json;
using Bazaar.Server.Modules.LineMove;
using Bazaar.Server.Modules.Updates;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Tests.Modules.Settings;

/// <summary>Module manager operator keys (doc 42 sections 10 and 15, doc 49 section 6): registration, defaults, bounds and the ring order rule.</summary>
public sealed class ModuleManagerSettingsTests
{
    private static readonly SettingActor _operator = new("operator:dom", SettingRole.Operator);

    private static readonly string[] _expectedNames =
    [
        "deps.update.deferWarnDays",
        "deps.update.autoSecurityPatches",
        "releases.ring.canaryPercent",
        "releases.ring.earlyPercent",
        "releases.ring.broadPercent",
        "releases.gate.minimumSamples",
        "releases.gate.maxErrorRate",
        "lineMove.drillMaxAgeDays",
        "lineMove.retry.maxAttempts",
        "lineMove.retry.initialBackoffMinutes",
        "lineMove.retry.multiplier",
        "lineMove.retry.maxBackoffMinutes",
    ];

    private static SettingEvaluation Eval(string name, string json, SettingActor? actor = null)
    {
        Assert.True(SettingRegistry.Known.TryGet(name, out var definition));
        Assert.NotNull(definition);
        using var doc = JsonDocument.Parse(json);
        return definition.Evaluate(actor ?? _operator, doc.RootElement);
    }

    [Fact]
    public void Known_registry_holds_exactly_the_module_manager_keys()
    {
        var names = ModuleManagerSettings.All.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(_expectedNames.OrderBy(n => n, StringComparer.Ordinal), names);

        foreach (var definition in ModuleManagerSettings.All)
        {
            Assert.True(SettingRegistry.Known.Contains(definition), definition.Name);
        }
    }

    [Fact]
    public void Every_module_manager_key_is_install_scope_operator_changed_and_applied_at_once()
    {
        foreach (var definition in ModuleManagerSettings.All)
        {
            Assert.Equal(SettingScope.Install, definition.Scope);
            Assert.Equal(SettingRole.Operator, definition.WhoMayChange);
            Assert.Equal(ApprovalTier.None, definition.ApprovalTier);
        }
    }

    [Fact]
    public void No_key_stands_in_for_a_decided_invariant()
    {
        // Beta-never-auto and GA-major-needs-approval are DECIDED invariants, not settings (CLAUDE.md).
        Assert.DoesNotContain(SettingRegistry.Known.Definitions, d => d.Name.Contains("beta", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(SettingRegistry.Known.Definitions, d => d.Name.Contains("major", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Defaults_match_the_design_values()
    {
        Assert.Equal(60, ModuleManagerSettings.DeferWarnDays.Default);
        Assert.True(ModuleManagerSettings.AutoSecurityPatches.Default);
        Assert.Equal(1, ModuleManagerSettings.RingCanaryPercent.Default);
        Assert.Equal(10, ModuleManagerSettings.RingEarlyPercent.Default);
        Assert.Equal(100, ModuleManagerSettings.RingBroadPercent.Default);
        Assert.Equal(30, ModuleManagerSettings.GateMinimumSamples.Default);
        Assert.Equal(0.01, ModuleManagerSettings.GateMaxErrorRate.Default);
        Assert.Equal(90, ModuleManagerSettings.LineMoveDrillMaxAgeDays.Default);
        Assert.Equal(3, ModuleManagerSettings.LineMoveRetryMaxAttempts.Default);
        Assert.Equal(10, ModuleManagerSettings.LineMoveRetryInitialBackoffMinutes.Default);
        Assert.Equal(2.0, ModuleManagerSettings.LineMoveRetryMultiplier.Default);
        Assert.Equal(60, ModuleManagerSettings.LineMoveRetryMaxBackoffMinutes.Default);
    }

    [Fact]
    public void Default_retry_settings_build_the_default_retry_policy()
    {
        var policy = ModuleManagerSettings.RetryPolicyFrom(
            ModuleManagerSettings.LineMoveRetryMaxAttempts.Default,
            ModuleManagerSettings.LineMoveRetryInitialBackoffMinutes.Default,
            ModuleManagerSettings.LineMoveRetryMultiplier.Default,
            ModuleManagerSettings.LineMoveRetryMaxBackoffMinutes.Default);

        Assert.Equal(LineMoveRetryPolicy.Default, policy);
    }

    [Fact]
    public void A_maximum_backoff_below_the_initial_backoff_is_refused()
    {
        Assert.NotNull(ModuleManagerSettings.RetryOrderError(initialBackoffMinutes: 30, maxBackoffMinutes: 10));
        Assert.Null(ModuleManagerSettings.RetryOrderError(initialBackoffMinutes: 10, maxBackoffMinutes: 10));

        Assert.Throws<ArgumentException>(() => ModuleManagerSettings.RetryPolicyFrom(3, 30, 2.0, 10));
    }

    [Fact]
    public void A_retry_policy_outside_its_ranges_is_refused_before_any_move_runs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModuleManagerSettings.RetryPolicyFrom(11, 10, 2.0, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModuleManagerSettings.RetryPolicyFrom(3, 10, 0.5, 60));
    }

    [Fact]
    public void Defer_warn_default_equals_the_defer_aging_default()
    {
        Assert.Equal(ModuleManagerSettingNames.DefaultDeferWarnDays, ModuleManagerSettings.DeferWarnDays.Default);
        Assert.Equal(ModuleManagerSettingNames.DeferWarnDaysSettingKey, ModuleManagerSettings.DeferWarnDays.Name);
    }

    [Fact]
    public void Every_default_passes_its_own_check()
    {
        foreach (var definition in ModuleManagerSettings.All)
        {
            using var doc = JsonDocument.Parse(definition.DefaultValueJson);
            Assert.Null(definition.CheckValue(doc.RootElement));
        }
    }

    [Fact]
    public void Default_ring_percentages_keep_the_order()
    {
        Assert.Null(ModuleManagerSettings.RingOrderError(
            ModuleManagerSettings.RingCanaryPercent.Default,
            ModuleManagerSettings.RingEarlyPercent.Default,
            ModuleManagerSettings.RingBroadPercent.Default));
    }

    [Theory]
    [InlineData("6", SettingWriteOutcome.Rejected)]
    [InlineData("7", SettingWriteOutcome.Accepted)]
    [InlineData("60", SettingWriteOutcome.Accepted)]
    [InlineData("365", SettingWriteOutcome.Accepted)]
    [InlineData("366", SettingWriteOutcome.Rejected)]
    [InlineData("-1", SettingWriteOutcome.Rejected)]
    [InlineData("\"60\"", SettingWriteOutcome.Rejected)]
    public void Defer_warn_days_is_from_seven_to_365_days(string json, SettingWriteOutcome expected)
    {
        Assert.Equal(expected, Eval("deps.update.deferWarnDays", json).Outcome);
    }

    [Theory]
    [InlineData("true", SettingWriteOutcome.Accepted)]
    [InlineData("false", SettingWriteOutcome.Accepted)]
    [InlineData("1", SettingWriteOutcome.Rejected)]
    public void Auto_security_patches_is_a_bool(string json, SettingWriteOutcome expected)
    {
        Assert.Equal(expected, Eval("deps.update.autoSecurityPatches", json).Outcome);
    }

    [Theory]
    [InlineData("releases.ring.canaryPercent", "0", SettingWriteOutcome.Accepted)]
    [InlineData("releases.ring.canaryPercent", "100", SettingWriteOutcome.Accepted)]
    [InlineData("releases.ring.canaryPercent", "-1", SettingWriteOutcome.Rejected)]
    [InlineData("releases.ring.canaryPercent", "101", SettingWriteOutcome.Rejected)]
    [InlineData("releases.ring.earlyPercent", "0", SettingWriteOutcome.Accepted)]
    [InlineData("releases.ring.earlyPercent", "101", SettingWriteOutcome.Rejected)]
    [InlineData("releases.ring.broadPercent", "100", SettingWriteOutcome.Accepted)]
    [InlineData("releases.ring.broadPercent", "-5", SettingWriteOutcome.Rejected)]
    [InlineData("releases.ring.broadPercent", "101", SettingWriteOutcome.Rejected)]
    public void Ring_percentages_are_from_0_to_100(string name, string json, SettingWriteOutcome expected)
    {
        Assert.Equal(expected, Eval(name, json).Outcome);
    }

    [Theory]
    [InlineData("0", SettingWriteOutcome.Rejected)]
    [InlineData("1", SettingWriteOutcome.Accepted)]
    [InlineData("30", SettingWriteOutcome.Accepted)]
    public void Gate_minimum_samples_is_at_least_one(string json, SettingWriteOutcome expected)
    {
        Assert.Equal(expected, Eval("releases.gate.minimumSamples", json).Outcome);
    }

    [Theory]
    [InlineData("0", SettingWriteOutcome.Accepted)]
    [InlineData("0.01", SettingWriteOutcome.Accepted)]
    [InlineData("1", SettingWriteOutcome.Accepted)]
    [InlineData("-0.01", SettingWriteOutcome.Rejected)]
    [InlineData("1.01", SettingWriteOutcome.Rejected)]
    [InlineData("\"0.01\"", SettingWriteOutcome.Rejected)]
    public void Gate_max_error_rate_is_from_0_to_1(string json, SettingWriteOutcome expected)
    {
        Assert.Equal(expected, Eval("releases.gate.maxErrorRate", json).Outcome);
    }

    [Theory]
    [InlineData("0", SettingWriteOutcome.Rejected)]
    [InlineData("1", SettingWriteOutcome.Accepted)]
    [InlineData("90", SettingWriteOutcome.Accepted)]
    public void Drill_max_age_is_at_least_one_day(string json, SettingWriteOutcome expected)
    {
        Assert.Equal(expected, Eval("lineMove.drillMaxAgeDays", json).Outcome);
    }

    [Fact]
    public void A_rejected_value_names_the_key_in_its_reason()
    {
        var evaluation = Eval("releases.ring.canaryPercent", "150");
        Assert.Equal(SettingWriteOutcome.Rejected, evaluation.Outcome);
        Assert.Contains("releases.ring.canaryPercent", evaluation.Reason);
    }

    [Fact]
    public void An_agent_actor_cannot_change_a_module_manager_key()
    {
        var agent = new SettingActor("agent:charles", SettingRole.Agent);
        var evaluation = Eval("deps.update.deferWarnDays", "7", agent);
        Assert.Equal(SettingWriteOutcome.Rejected, evaluation.Outcome);
    }

    [Theory]
    [InlineData(1, 10, 100)]
    [InlineData(10, 10, 10)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 100, 100)]
    public void Ring_order_holds_when_canary_is_not_above_early_or_broad(int canary, int early, int broad)
    {
        Assert.Null(ModuleManagerSettings.RingOrderError(canary, early, broad));
    }

    [Theory]
    [InlineData(11, 10, 100)]
    [InlineData(20, 10, 100)]
    public void Ring_order_rejects_canary_above_early(int canary, int early, int broad)
    {
        var error = ModuleManagerSettings.RingOrderError(canary, early, broad);
        Assert.NotNull(error);
        Assert.Contains("canaryPercent", error);
    }

    [Theory]
    [InlineData(1, 50, 40)]
    [InlineData(0, 5, 4)]
    public void Ring_order_rejects_early_above_broad(int canary, int early, int broad)
    {
        var error = ModuleManagerSettings.RingOrderError(canary, early, broad);
        Assert.NotNull(error);
        Assert.Contains("earlyPercent", error);
    }
}
