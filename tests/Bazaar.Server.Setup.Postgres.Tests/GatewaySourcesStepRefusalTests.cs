using Bazaar.Server.Setup.Postgres;
using Npgsql;

namespace Bazaar.Server.Setup.Postgres.Tests;

/// <summary>
/// What the gateway sources step refuses before it touches the database. The data source points nowhere and is never opened, so
/// these run on any host.
/// </summary>
public sealed class GatewaySourcesStepRefusalTests
{
    private const string _canary = "canary-secret-0000";

    private static SetupApplyContext Context() =>
        new("plan-hash-1", SetupEntryPath.Cli, "operator-test", null, null, SetupStateSnapshot.Empty, []);

    private static SetupPlan Plan(params SetupModelSource[] sources) =>
        new() { Models = new SetupModelsSection { Sources = [.. sources] } };

    private static async Task<SetupStepResult> ApplyAsync(SetupPlan plan, IModelSourceCatalogue? catalogue = null)
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Timeout=1");
        return await new GatewaySourcesStep(dataSource, catalogue).ApplyAsync(plan, Context(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void The_step_has_a_stable_id_covers_the_models_section_and_runs_before_the_settings_step()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none");
        var step = new GatewaySourcesStep(dataSource);

        Assert.Equal("models.sources", step.Id);
        Assert.Equal(["models"], step.Covers);
        Assert.True(step.Order < InstallSettingsStep.StepOrder);
        Assert.All(step.Covers, id => Assert.True(SetupPlanSections.IsKnown(id), id));
    }

    [Fact]
    public async Task A_source_the_catalogue_does_not_know_fails_the_step_and_does_not_invent_a_base_url()
    {
        var result = await ApplyAsync(Plan(new SetupModelSource { Id = "groq", KeyRef = "secret://bazaar/providers/groq/key" }));

        Assert.False(result.Succeeded);
        Assert.Contains("groq", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret://", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_that_takes_a_key_but_has_none_fails_the_step()
    {
        var result = await ApplyAsync(Plan(new SetupModelSource { Id = "freellmapi" }));

        Assert.False(result.Succeeded);
        Assert.Contains("freellmapi", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_reference_outside_the_providers_path_fails_the_step_and_is_not_echoed()
    {
        var result = await ApplyAsync(Plan(new SetupModelSource { Id = "freellmapi", KeyRef = "secret://bazaar/owner/" + _canary }));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(_canary, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_literal_key_fails_the_step_and_is_not_echoed()
    {
        var result = await ApplyAsync(Plan(new SetupModelSource { Id = "freellmapi", KeyRef = _canary }));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(_canary, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_local_runtime_with_a_key_fails_the_step()
    {
        var result = await ApplyAsync(Plan(new SetupModelSource { Id = "ollama", KeyRef = "secret://bazaar/providers/ollama/key" }));

        Assert.False(result.Succeeded);
        Assert.Contains("ollama", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_and_its_alias_in_one_plan_fail_the_step()
    {
        var result = await ApplyAsync(Plan(
            new SetupModelSource { Id = "anthropic", KeyRef = "secret://bazaar/providers/anthropic/key" },
            new SetupModelSource { Id = "anthropic-owner", KeyRef = "secret://bazaar/providers/anthropic/other" }));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task An_operator_catalogue_replaces_the_default_lookup()
    {
        var catalogue = new ModelSourceCatalogue([new ModelSourceEntry("my-proxy", "reverse_proxy", "openai_chat", "https://proxy.example.invalid/v1", "bearer", [])]);

        var result = await ApplyAsync(Plan(new SetupModelSource { Id = "freellmapi", KeyRef = "secret://bazaar/providers/freellmapi/key" }), catalogue);

        Assert.False(result.Succeeded);
        Assert.Contains("freellmapi", result.Message, StringComparison.Ordinal);
    }
}
