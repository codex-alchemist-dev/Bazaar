using System.Text.Json;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.BackupPolicy.Tests;

/// <summary>The backup policy keys (doc 33, doc 45 step 8): registration, safe defaults, the target rules and the approval rule.</summary>
public sealed class BackupPolicySettingsTests
{
    private static readonly SettingActor _operator = new("setup:plan", SettingRole.Operator);

    private static readonly SettingRegistry _known = SettingRegistry.WithContributions([new BackupPolicySettingContribution()]);

    private static SettingEvaluation Eval(string name, string json, SettingActor? actor = null)
    {
        Assert.True(_known.TryGet(name, out var definition));
        Assert.NotNull(definition);
        using var doc = JsonDocument.Parse(json);
        return definition.Evaluate(actor ?? _operator, doc.RootElement);
    }

    [Fact]
    public void The_contribution_registers_exactly_the_three_keys()
    {
        var names = new BackupPolicySettingContribution().Settings.Select(d => d.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["backup.drill_interval_days", "backup.no_target_acknowledged", "backup.target"], names);
        foreach (var definition in BackupPolicySettings.All)
        {
            Assert.True(_known.Contains(definition), definition.Name);
            Assert.Equal(SettingScope.Install, definition.Scope);
            Assert.Equal(SettingRole.Operator, definition.WhoMayChange);
        }
    }

    [Fact]
    public void Defaults_are_the_safe_ones()
    {
        Assert.Equal(string.Empty, BackupPolicySettings.Target.Default);
        Assert.Equal(90, BackupPolicySettings.DrillIntervalDays.Default);
        Assert.False(BackupPolicySettings.NoTargetAcknowledged.Default);
    }

    [Theory]
    [InlineData("\"file:/mnt/backup/bazaar\"")]
    [InlineData("\"file:D:\\\\Backups\\\\Bazaar\"")]
    [InlineData("\"\"")]
    public void A_local_or_empty_target_is_accepted_without_approval(string json)
    {
        var evaluation = Eval("backup.target", json);

        Assert.Equal(SettingWriteOutcome.Accepted, evaluation.Outcome);
    }

    [Theory]
    [InlineData("\"s3:https://s3.example.invalid/bucket\"")]
    [InlineData("\"b2:bucket/path\"")]
    [InlineData("\"custom:my-target\"")]
    public void A_target_off_the_install_is_held_for_owner_approval(string json)
    {
        var evaluation = Eval("backup.target", json);

        Assert.Equal(SettingWriteOutcome.PendingApproval, evaluation.Outcome);
        Assert.Equal(ApprovalTier.Tier2, evaluation.ApprovalTier);
    }

    [Theory]
    [InlineData("\"no-colon\"")]
    [InlineData("\"file:\"")]
    [InlineData("\":/path\"")]
    [InlineData("\"ftp:host/path\"")]
    [InlineData("\"FILE:/path\"")]
    [InlineData("\"file:/with space\"")]
    [InlineData("\"s3:user:pa55@host/bucket\"")]
    [InlineData("42")]
    public void A_malformed_target_is_rejected(string json)
    {
        var evaluation = Eval("backup.target", json);

        Assert.Equal(SettingWriteOutcome.Rejected, evaluation.Outcome);
    }

    [Fact]
    public void A_rejected_target_reason_never_repeats_the_value()
    {
        var evaluation = Eval("backup.target", "\"s3:user:pa55@host/bucket\"");

        Assert.DoesNotContain("pa55", evaluation.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("365", true)]
    [InlineData("0", false)]
    [InlineData("366", false)]
    [InlineData("-1", false)]
    public void The_drill_interval_is_one_to_365_days(string json, bool accepted)
    {
        var evaluation = Eval("backup.drill_interval_days", json);

        Assert.Equal(accepted ? SettingWriteOutcome.Accepted : SettingWriteOutcome.Rejected, evaluation.Outcome);
    }

    [Fact]
    public void The_acknowledgement_is_a_plain_flag_and_wants_a_bool()
    {
        Assert.Equal(SettingWriteOutcome.Accepted, Eval("backup.no_target_acknowledged", "true").Outcome);
        Assert.Equal(SettingWriteOutcome.Rejected, Eval("backup.no_target_acknowledged", "\"yes\"").Outcome);
    }

    [Fact]
    public void A_non_operator_may_not_change_the_target()
    {
        var evaluation = Eval("backup.target", "\"file:/x\"", new SettingActor("agent:x", SettingRole.Agent));

        Assert.Equal(SettingWriteOutcome.Rejected, evaluation.Outcome);
    }

    [Fact]
    public void The_component_registers_the_contribution_and_declares_no_dependency()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        new BackupPolicyComponent().AddServices(services, configuration);

        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<ISettingContribution>());
        Assert.Empty(BackupPolicyComponent.ManifestDefinition.DependsOn);
    }
}
