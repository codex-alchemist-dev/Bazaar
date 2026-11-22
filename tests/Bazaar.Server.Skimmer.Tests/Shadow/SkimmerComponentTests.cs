using Bazaar.Core;
using Bazaar.Server.Gateway;
using Bazaar.Server.Skimmer;
using Bazaar.Server.Skimmer.Shadow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Bazaar.Server.Tests.Skimmer.Shadow;

/// <summary>The Skimmer component: off by default, settings from configuration, gatekeeper policy seeded at start (docs 05 and 13).</summary>
public sealed class SkimmerComponentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingSeed : IGatekeeperPolicySeed
    {
        public List<string> Models { get; } = [];

        public Task<bool> EnsureAsync(string model, CancellationToken ct)
        {
            Models.Add(model);
            return Task.FromResult(true);
        }
    }

    private sealed class NoGateway : IModelGateway
    {
        public IAsyncEnumerable<Bazaar.Contracts.Gateway.ModelEvent> CallAsync(Bazaar.Contracts.Gateway.ModelRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("not called");

        public Task<Bazaar.Contracts.Gateway.EmbeddingResult> EmbedAsync(Bazaar.Contracts.Gateway.EmbeddingRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("not called");
    }

    private static ServiceProvider Build(Dictionary<string, string?> config, RecordingSeed seed)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create("Host=localhost;Database=x"));
        services.AddSingleton<IModelGateway>(new NoGateway());
        services.AddSingleton<IGatekeeperPolicySeed>(seed);
        services.AddSingleton<IShadowMessageSource>(new NoSource());
        new SkimmerComponent().AddServices(services, configuration);
        return services.BuildServiceProvider();
    }

    private sealed class NoSource : IShadowMessageSource
    {
        public Task<IReadOnlyList<ShadowMessage>> ReadAfterAsync(Guid gcId, ShadowAgentInfo agent, long afterSeq, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ShadowMessage>>([]);
    }

    [Fact]
    public void The_component_is_off_by_default_and_named_skimmer()
    {
        Assert.False(SkimmerComponent.ManifestDefinition.EnabledByDefault);
        Assert.Equal("skimmer", SkimmerComponent.ManifestDefinition.Id);
        Assert.Equal("gatekeeper", SkimmerComponent.GatekeeperSubject);
    }

    [Fact]
    public void Settings_default_to_shadow_and_read_overrides()
    {
        using var defaults = Build([], new RecordingSeed());
        var d = defaults.GetRequiredService<ShadowSettings>();
        Assert.Equal((SkimmerMode.Shadow, 7000), (d.Mode, d.ThresholdBasisPoints));

        using var custom = Build(new() { ["Bazaar:Skimmer:Mode"] = "off", ["Bazaar:Skimmer:ThresholdBp"] = "8000", ["Bazaar:Skimmer:DmAlwaysWakes"] = "false" }, new RecordingSeed());
        var c = custom.GetRequiredService<ShadowSettings>();
        Assert.Equal((SkimmerMode.Off, 8000, false), (c.Mode, c.ThresholdBasisPoints, c.EffectiveRules.DmAlwaysWakes));
        Assert.NotNull(custom.GetRequiredService<ShadowConsumer>());
    }

    [Fact]
    public async Task Start_seeds_the_gatekeeper_policy_with_the_scorer_model()
    {
        var seed = new RecordingSeed();
        using var provider = Build(new() { ["Bazaar:Skimmer:ScorerModel"] = "anthropic-direct:claude-haiku-5-5" }, seed);

        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(Ct);
        }

        Assert.Equal(["anthropic-direct:claude-haiku-5-5"], seed.Models);
    }
}
