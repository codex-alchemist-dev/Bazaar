using System.Text.Json;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.Setup.Postgres;
using Npgsql;

namespace Bazaar.Server.Setup.Postgres.Tests;

/// <summary>
/// What the step refuses before it touches the database. The data source points nowhere and is never opened, so these run on any host.
/// </summary>
public sealed class InstallSettingsStepRefusalTests
{
    private const string _canary = "canary-secret-0000";

    private static SetupApplyContext Context() =>
        new("plan-hash-1", SetupEntryPath.Cli, "operator-test", null, null, SetupStateSnapshot.Empty, []);

    private static async Task<SetupStepResult> ApplyAsync(SetupPlan plan)
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Timeout=1");
        return await new InstallSettingsStep(dataSource, RegistrySetupSettingCatalog.Known)
            .ApplyAsync(plan, Context(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void The_step_has_a_stable_id_and_runs_after_the_earlier_configuration_steps()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none");
        var step = new InstallSettingsStep(dataSource, RegistrySetupSettingCatalog.Known);

        Assert.Equal("install.settings", step.Id);
        Assert.Equal(1500, step.Order);
    }

    [Fact]
    public async Task A_value_the_registry_holds_for_approval_fails_the_step_and_names_the_key_only()
    {
        var plan = new SetupPlan { Network = new SetupNetworkSection { StatusBind = "lan" } };

        var result = await ApplyAsync(plan);

        Assert.False(result.Succeeded);
        Assert.Contains("host.status.bind", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_canary, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_the_registry_does_not_know_fails_the_step()
    {
        var plan = new SetupPlan { Settings = { ["no.such.key"] = JsonSerializer.SerializeToElement(_canary) } };

        var result = await ApplyAsync(plan);

        Assert.False(result.Succeeded);
        Assert.Contains("no.such.key", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_canary, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_of_the_wrong_form_fails_the_step_and_is_not_echoed()
    {
        var result = await ApplyAsync(new SetupPlan { Network = new SetupNetworkSection { StatusBind = _canary } });

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(_canary, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_merge_credential_reference_that_is_not_a_secret_store_reference_fails_the_step_and_is_not_echoed()
    {
        var plan = new SetupPlan
        {
            Custody = new SetupCustodySection { MergeCredential = new SetupMergeCredentialSection { Kind = "owner_app", AppIdRef = "env:" + _canary } },
        };

        var result = await ApplyAsync(plan);

        Assert.False(result.Succeeded);
        Assert.Contains("identity.merge_credential.app_id_ref", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_canary, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_company_source_the_registry_does_not_accept_fails_the_step_and_is_not_echoed()
    {
        var result = await ApplyAsync(new SetupPlan { Company = new SetupCompanySection { Source = "https://" + _canary } });

        Assert.False(result.Succeeded);
        Assert.Contains("company.source", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_canary, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_automatic_update_policy_for_the_rest_fails_the_step_because_no_setting_stores_it()
    {
        var plan = new SetupPlan { Install = new SetupInstallSection { UpdatePolicy = new SetupUpdatePolicySection { Other = "automatic" } } };

        var result = await ApplyAsync(plan);

        Assert.False(result.Succeeded);
        Assert.Contains("install.updatePolicy.other", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_step_covers_the_update_policy_the_merge_credential_and_the_company_source_but_not_the_sections_it_cannot_store()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none");
        var covers = new InstallSettingsStep(dataSource, RegistrySetupSettingCatalog.Known).Covers;

        Assert.Contains("install.updatePolicy", covers);
        Assert.Contains("git", covers);
        Assert.Contains("company", covers);
        // The models section is the gateway sources step's: this step writes only the preset half of it.
        Assert.DoesNotContain(covers, id => id is "privacy" or "backups" or "models" or "email" or "modules");
        Assert.All(covers, id => Assert.True(SetupPlanSections.IsKnown(id), id));
    }
}
