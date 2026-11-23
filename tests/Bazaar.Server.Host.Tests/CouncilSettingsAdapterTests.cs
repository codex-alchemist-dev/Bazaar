using Bazaar.Server.Council.Service;
using Bazaar.Server.Council.Settings;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.CouncilAdapters;
using Bazaar.Server.Knowledge;
using Bazaar.Server.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

public sealed class CouncilSettingsAdapterTests
{
    private sealed class MapStore : ISettingStore
    {
        public Dictionary<string, object> Values { get; } = [];

        public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryGetValue(key.Name, out var v) ? (T)v : key.Default);

        public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingWriteResult> SetJsonAsync(string key, System.Text.Json.JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Defaults_produce_default_options()
    {
        var options = await new SettingsCouncilOptionsProvider(new MapStore()).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new CouncilOptions(), options);
    }

    [Fact]
    public async Task Stored_values_reach_the_options_and_the_pilot_settings()
    {
        var store = new MapStore();
        store.Values[CouncilSettings.QuorumMin.Name] = 5;
        store.Values[CouncilSettings.OwnerVotes.Name] = true;
        store.Values[CouncilSettings.ReasonOrderKey.Name] = ReasonOrder.Fixed;
        store.Values[KnowledgeSettings.CouncilFollowupDissentShareKey.Name] = 0.5m;
        store.Values[CouncilSettings.PilotTargetVotes.Name] = 30;
        var provider = new SettingsCouncilOptionsProvider(store);

        var options = await provider.LoadAsync(TestContext.Current.CancellationToken);
        var pilot = await provider.LoadPilotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, options.QuorumMin);
        Assert.True(options.OwnerVotes);
        Assert.Equal(ReasonOrder.Fixed, options.ReasonOrder);
        Assert.Equal(0.5m, options.FollowupDissentShare);
        Assert.Equal(30, pilot.TargetVotes);
    }

    [Fact]
    public async Task The_dissent_share_is_read_live_on_every_call()
    {
        var store = new MapStore();
        var source = new SettingsCouncilDissentShareSource(store);

        Assert.Equal(0.33m, await source.GetFollowupDissentShareAsync(TestContext.Current.CancellationToken));
        store.Values[KnowledgeSettings.CouncilFollowupDissentShareKey.Name] = 0.6m;
        Assert.Equal(0.6m, await source.GetFollowupDissentShareAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Composition_replaces_the_default_options_and_adds_the_dissent_source_when_a_store_exists()
    {
        var store = new MapStore();
        store.Values[CouncilSettings.QuorumMin.Name] = 4;
        var services = new ServiceCollection();
        services.AddSingleton(new CouncilOptions());
        services.AddSingleton<ISettingStore>(store);

        services.AddBazaarCouncilSettings();
        using var provider = services.BuildServiceProvider();

        Assert.Equal(4, provider.GetRequiredService<CouncilOptions>().QuorumMin);
        Assert.IsType<SettingsCouncilDissentShareSource>(provider.GetRequiredService<ICouncilDissentShareSource>());
    }

    [Fact]
    public void Composition_leaves_the_defaults_when_there_is_no_settings_store()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new CouncilOptions());

        services.AddBazaarCouncilSettings();
        using var provider = services.BuildServiceProvider();

        Assert.Equal(3, provider.GetRequiredService<CouncilOptions>().QuorumMin);
        Assert.Null(provider.GetService<ICouncilDissentShareSource>());
    }
}
