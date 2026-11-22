using System.Text.Json;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Server.Tests.Setup;

/// <summary>The settings a plan sets: the settings map plus the plan fields that map to one registry key each (doc 45 section 1.2).</summary>
public sealed class SetupSettingProjectionTests
{
    private static SetupPlan ValidPlan() => SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known).Plan!;

    [Fact]
    public void The_valid_plan_projects_the_map_the_network_block_and_the_channel_sorted_by_key()
    {
        var projected = SetupSettingProjection.Project(ValidPlan());

        Assert.Equal(
            ["deps.update.autoSecurityPatches", "host.shutdown_timeout_seconds", "host.status.bind", "host.status.port", "update.channel"],
            projected.Select(s => s.Key));
        Assert.Equal(["install.updatePolicy.security", "settings.host.shutdown_timeout_seconds", "network.statusBind", "network.statusPort", "install.releaseChannel"], projected.Select(s => s.PlanPath));
    }

    [Fact]
    public void Latest_commit_uses_the_registry_snake_case_wire_form()
    {
        var plan = SetupPlanLoader.Load(SetupFixtures.With("\"releaseChannel\": \"beta\"", "\"releaseChannel\": \"latest-commit\""), RegistrySetupSettingCatalog.Known).Plan!;

        var channel = Assert.Single(SetupSettingProjection.Project(plan), s => s.Key == "update.channel");

        Assert.Equal("latest_commit", channel.Value.GetString());
        Assert.True(RegistrySetupSettingCatalog.Known.TryGet("update.channel", out var definition));
        Assert.Null(definition!.CheckValue(channel.Value));
    }

    [Fact]
    public void The_scam_mail_key_is_projected_only_when_the_protocol_is_on()
    {
        var off = SetupSettingProjection.Project(ValidPlan());
        var on = SetupSettingProjection.Project(new SetupPlan { ScamMailProtocol = new SetupScamMailSection { Enabled = true } });

        Assert.DoesNotContain(off, s => s.Key == "security.scam_mail.protocol_enabled");
        var scam = Assert.Single(on, s => s.Key == "security.scam_mail.protocol_enabled");
        Assert.Equal(JsonValueKind.True, scam.Value.ValueKind);
    }

    [Theory]
    [InlineData("network.statusBind", "host.status.bind")]
    [InlineData("network.statusPort", "host.status.port")]
    [InlineData("scamMailProtocol.enabled", "security.scam_mail.protocol_enabled")]
    [InlineData("install.releaseChannel", "update.channel")]
    [InlineData("install.updatePolicy.security", "deps.update.autoSecurityPatches")]
    public void Each_mapped_plan_path_names_its_registry_key(string path, string key) =>
        Assert.Equal(key, SetupSettingProjection.DerivedKeyForPath(path));

    [Fact]
    public void An_unmapped_path_names_no_key() =>
        Assert.Null(SetupSettingProjection.DerivedKeyForPath("install.companyName"));

    [Fact]
    public void Every_projected_value_of_the_valid_plan_passes_the_real_registry()
    {
        foreach (var setting in SetupSettingProjection.Project(ValidPlan()))
        {
            Assert.True(RegistrySetupSettingCatalog.Known.TryGet(setting.Key, out var definition), setting.Key);
            Assert.Null(definition!.CheckValue(setting.Value));
            Assert.True(definition.Evaluate("setup:plan", setting.Value).Accepted, setting.Key);
        }
    }

    [Fact]
    public void Setting_the_release_channel_in_the_settings_map_too_is_one_error()
    {
        var plan = SetupFixtures.With("\"settings\": { \"host.shutdown_timeout_seconds\": 20 }", "\"settings\": { \"update.channel\": \"stable\" }");

        var result = SetupPlanLoader.Load(plan, RegistrySetupSettingCatalog.Known);

        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == "settings.update.channel" && issue.Message.Contains("already set by install.releaseChannel"));
    }

    [Theory]
    [InlineData("automatic", JsonValueKind.True)]
    [InlineData("manual", JsonValueKind.False)]
    public void The_security_update_policy_projects_the_auto_patch_flag(string security, JsonValueKind expected)
    {
        var plan = new SetupPlan { Install = new SetupInstallSection { UpdatePolicy = new SetupUpdatePolicySection { Security = security } } };

        var flag = Assert.Single(SetupSettingProjection.Project(plan), s => s.Key == "deps.update.autoSecurityPatches");

        Assert.Equal(expected, flag.Value.ValueKind);
        Assert.Equal("install.updatePolicy.security", flag.PlanPath);
        Assert.True(RegistrySetupSettingCatalog.Known.TryGet(flag.Key, out var definition));
        Assert.True(definition!.IsInstallScope);
        Assert.Null(definition.CheckValue(flag.Value));
        Assert.True(definition.Evaluate("setup:plan", flag.Value).Accepted);
    }

    [Theory]
    [InlineData("manual", true)]
    [InlineData("automatic", false)]
    public void Only_a_manual_other_update_policy_is_backed(string other, bool backed)
    {
        var plan = new SetupPlan { Install = new SetupInstallSection { UpdatePolicy = new SetupUpdatePolicySection { Other = other } } };

        Assert.Equal(backed, SetupSettingProjection.IsOtherUpdatePolicyBacked(plan));
    }

    [Fact]
    public void An_automatic_other_update_policy_is_a_validation_error()
    {
        var plan = SetupFixtures.With("\"other\": \"manual\"", "\"other\": \"automatic\"");

        var result = SetupPlanLoader.Load(plan, RegistrySetupSettingCatalog.Known);

        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == "install.updatePolicy.other");
    }

    [Fact]
    public void Setting_the_auto_patch_flag_in_the_settings_map_too_is_one_error()
    {
        var plan = SetupFixtures.With("\"settings\": { \"host.shutdown_timeout_seconds\": 20 }", "\"settings\": { \"deps.update.autoSecurityPatches\": true }");

        var result = SetupPlanLoader.Load(plan, RegistrySetupSettingCatalog.Known);

        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == "settings.deps.update.autoSecurityPatches" && issue.Message.Contains("already set by install.updatePolicy.security"));
    }
}
