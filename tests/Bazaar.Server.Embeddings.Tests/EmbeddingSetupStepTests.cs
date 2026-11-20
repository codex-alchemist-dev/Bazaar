using System.Text.Json;
using Bazaar.Server.Setup;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Embeddings.Tests;

/// <summary>The embedding setup step (doc 08 WP6, doc 45 step 9b): plan, validation, apply, rollback. In memory only.</summary>
public sealed class EmbeddingSetupStepTests
{
    private const string _canary = "sk-CANARY-do-not-leak-0123456789";

    private static SetupPlan PlanFor(string? choice)
    {
        var plan = new SetupPlan();
        if (choice is not null)
        {
            plan.Settings[EmbeddingSettings.EmbeddingProfileKey.Name] = JsonSerializer.SerializeToElement(choice);
        }

        return plan;
    }

    private static SetupApplyContext Context(string hash = "hash-1") =>
        new(hash, SetupEntryPath.Cli, "operator-test", null, null, SetupStateSnapshot.Empty, []);

    private static EmbeddingProviderOptions Local(int dimensions = 768, string? apiKeyReference = null, string baseUrl = "http://localhost:11434") => new()
    {
        Id = "local",
        Kind = EmbeddingProviderKind.OpenAiCompatible,
        ModelId = "nomic-embed-text",
        Dimensions = dimensions,
        BaseUrl = baseUrl,
        ApiKeyReference = apiKeyReference,
    };

    private static EmbeddingSetupStep Step(FakeProfileStore store, params EmbeddingProviderOptions[] providers) =>
        new(store, Options.Create(new EmbeddingOptions { Providers = [.. providers] }));

    private static EmbeddingProfile Profile(string provider = "local", string model = "nomic-embed-text", int dims = 768, bool active = true) =>
        new(Guid.NewGuid(), provider, model, dims, DateTimeOffset.UnixEpoch, active);

    [Fact]
    public async Task A_plan_without_the_setting_means_none_and_writes_nothing()
    {
        var store = new FakeProfileStore(null);
        var step = Step(store, Local());

        var planned = await step.PlanAsync(PlanFor(null), TestContext.Current.CancellationToken);
        var result = await step.ApplyAsync(PlanFor(null), Context(), TestContext.Current.CancellationToken);

        Assert.Equal(EmbeddingSetupAction.None, planned.Action);
        Assert.True(planned.IsValid);
        Assert.True(result.Succeeded);
        Assert.Equal(0, store.Creates);
        Assert.Equal(0, store.Activations);
    }

    [Fact]
    public async Task None_chosen_explicitly_leaves_an_active_profile_alone()
    {
        var store = new FakeProfileStore(Profile());
        var result = await Step(store, Local()).ApplyAsync(PlanFor("none"), Context(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(0, store.Activations);
        Assert.NotNull(store.Active);
    }

    [Fact]
    public async Task A_configured_provider_is_planned_then_created_and_activated()
    {
        var store = new FakeProfileStore(null);
        var step = Step(store, Local());

        var planned = await step.PlanAsync(PlanFor("local"), TestContext.Current.CancellationToken);
        var result = await step.ApplyAsync(PlanFor("local"), Context(), TestContext.Current.CancellationToken);

        Assert.Equal(EmbeddingSetupAction.Activate, planned.Action);
        Assert.Equal(768, planned.Dimensions);
        Assert.Equal("http://localhost:11434", planned.BaseUrl);
        Assert.True(result.Succeeded);
        var active = Assert.IsType<EmbeddingProfile>(store.Active);
        Assert.Equal(("local", "nomic-embed-text", 768), (active.ProviderId, active.ModelId, active.Dimensions));
        Assert.Equal(1, store.Creates);
    }

    [Fact]
    public async Task Applying_twice_changes_nothing_the_second_time()
    {
        var store = new FakeProfileStore(null);
        var step = Step(store, Local());

        await step.ApplyAsync(PlanFor("local"), Context(), TestContext.Current.CancellationToken);
        var second = await step.ApplyAsync(PlanFor("local"), Context("hash-2"), TestContext.Current.CancellationToken);

        Assert.True(second.Succeeded);
        Assert.Equal(1, store.Creates);
        Assert.Equal(1, store.Activations);
        Assert.Equal(EmbeddingSetupAction.AlreadyActive, (await step.PlanAsync(PlanFor("local"), TestContext.Current.CancellationToken)).Action);
    }

    [Fact]
    public async Task A_crash_between_create_and_activate_resumes_on_the_existing_row()
    {
        var store = new FakeProfileStore(null);
        store.Rows.Add(Profile(active: false));

        var result = await Step(store, Local()).ApplyAsync(PlanFor("local"), Context(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(0, store.Creates);
        Assert.Single(store.Rows);
        Assert.True(store.Rows[0].Active);
    }

    [Fact]
    public async Task A_different_dimension_on_an_active_profile_is_refused_with_an_issue_and_nothing_is_written()
    {
        var store = new FakeProfileStore(Profile(dims: 768));
        var step = Step(store, Local(dimensions: 1024));

        var planned = await step.PlanAsync(PlanFor("local"), TestContext.Current.CancellationToken);
        var result = await step.ApplyAsync(PlanFor("local"), Context(), TestContext.Current.CancellationToken);

        Assert.Equal(EmbeddingSetupAction.Refused, planned.Action);
        var issue = Assert.Single(planned.Issues);
        Assert.Equal(SetupIssueSeverity.Error, issue.Severity);
        Assert.Equal("settings.memory.embedding_profile", issue.Path);
        Assert.Contains("dimension is fixed at setup", issue.Message, StringComparison.Ordinal);
        Assert.False(result.Succeeded);
        Assert.Equal(0, store.Creates);
        Assert.Equal(0, store.Activations);
        Assert.Equal(768, store.Active!.Dimensions);
    }

    [Fact]
    public async Task A_new_provider_with_the_same_dimension_is_a_planned_switch()
    {
        var store = new FakeProfileStore(Profile(provider: "old", model: "old-model"));

        var result = await Step(store, Local()).ApplyAsync(PlanFor("local"), Context(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("local", store.Active!.ProviderId);
        Assert.Equal(2, store.Rows.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("Not Valid")]
    public async Task An_unknown_or_malformed_provider_id_is_refused(string choice)
    {
        var step = Step(new FakeProfileStore(null), Local());

        var planned = await step.PlanAsync(PlanFor(choice), TestContext.Current.CancellationToken);

        Assert.Equal(EmbeddingSetupAction.Refused, planned.Action);
        Assert.DoesNotContain(planned.Issues, i => i.Message.Contains(choice, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_non_string_setting_is_refused()
    {
        var plan = new SetupPlan();
        plan.Settings[EmbeddingSettings.EmbeddingProfileKey.Name] = JsonSerializer.SerializeToElement(7);

        var planned = await Step(new FakeProfileStore(null), Local()).PlanAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(EmbeddingSetupAction.Refused, planned.Action);
    }

    [Theory]
    [InlineData(0, "http://localhost:11434")]
    [InlineData(5000, "http://localhost:11434")]
    [InlineData(768, "not a url")]
    [InlineData(768, "ftp://host")]
    [InlineData(768, "http://user:pw@host")]
    [InlineData(768, "http://host/?key=abc")]
    public async Task A_bad_provider_entry_is_refused(int dimensions, string baseUrl)
    {
        var step = Step(new FakeProfileStore(null), Local(dimensions, null, baseUrl));

        var planned = await step.PlanAsync(PlanFor("local"), TestContext.Current.CancellationToken);

        Assert.Equal(EmbeddingSetupAction.Refused, planned.Action);
        Assert.NotEmpty(planned.Issues);
    }

    [Fact]
    public async Task A_literal_key_in_the_provider_entry_is_refused_and_the_value_never_appears_in_any_output()
    {
        var store = new FakeProfileStore(null);
        var step = Step(store, Local(apiKeyReference: _canary, baseUrl: $"http://user:{_canary}@host"));

        var planned = await step.PlanAsync(PlanFor("local"), TestContext.Current.CancellationToken);
        var result = await step.ApplyAsync(PlanFor("local"), Context(), TestContext.Current.CancellationToken);

        Assert.Equal(EmbeddingSetupAction.Refused, planned.Action);
        Assert.Equal(0, store.Creates);
        var everything = JsonSerializer.Serialize(planned.Issues) + planned.BaseUrl + planned.ModelId + planned.ProviderId + result.Message;
        Assert.DoesNotContain(_canary, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("user:", everything, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("env:EMBEDDING_API_KEY")]
    [InlineData("secret://bazaar/embeddings/key")]
    public async Task A_key_reference_is_accepted_and_the_reference_text_never_appears_in_the_plan_or_result(string reference)
    {
        var store = new FakeProfileStore(null);
        var step = Step(store, Local(apiKeyReference: reference));

        var planned = await step.PlanAsync(PlanFor("local"), TestContext.Current.CancellationToken);
        var result = await step.ApplyAsync(PlanFor("local"), Context(), TestContext.Current.CancellationToken);

        Assert.True(planned.IsValid);
        Assert.True(result.Succeeded);
        var everything = System.Text.Json.JsonSerializer.Serialize(planned.Issues) + planned.BaseUrl + result.Message
            + string.Join('|', store.Rows.Select(r => $"{r.ProviderId}{r.ModelId}"));
        Assert.DoesNotContain(reference, everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_reactivates_the_previously_active_profile()
    {
        var previous = Profile(provider: "old", model: "old-model");
        var store = new FakeProfileStore(previous);
        var step = Step(store, Local());
        var context = Context();

        await step.ApplyAsync(PlanFor("local"), context, TestContext.Current.CancellationToken);
        Assert.Equal("local", store.Active!.ProviderId);
        await step.RollbackAsync(PlanFor("local"), context, CancellationToken.None);

        Assert.Equal(previous.ProfileId, store.Active!.ProfileId);
    }

    [Fact]
    public void The_step_has_a_stable_id_and_order()
    {
        var step = Step(new FakeProfileStore(null));

        Assert.Equal("embeddings.profile", step.Id);
        Assert.Equal(95, step.Order);
    }

    [Fact]
    public void The_component_registers_the_step_and_the_setting_defaults_to_none()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        new EmbeddingsComponent().AddServices(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        Assert.Contains(services, d => d.ServiceType == typeof(ISetupStep) && d.ImplementationType == typeof(EmbeddingSetupStep));
        Assert.Equal("none", EmbeddingSettings.EmbeddingProfileKey.Default);
    }
}
