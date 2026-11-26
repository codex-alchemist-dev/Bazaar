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
}
