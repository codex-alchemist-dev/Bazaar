using Bazaar.Core;
using Bazaar.Server.Embeddings;
using Bazaar.Server.Host.Components.Audit;
using Bazaar.Server.Host.Components.Embeddings;
using Bazaar.Server.Host.Components.Settings;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;
using Bazaar.Server.Vault.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Tests;

/// <summary>Host wiring of the Embeddings module (doc 08 WP6 detail): component list, setting, source adapter and the Vault query embedder.</summary>
public sealed class EmbeddingsCompositionTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static ServiceCollection Compose(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBazaarComponents(configuration, [new AuditComponent(), new SettingsComponent(), new EmbeddingsComponent()]);
        services.AddBazaarEmbeddingsHost(configuration);
        return services;
    }

    [Fact]
    public void The_host_lists_the_embeddings_component()
    {
        var component = Assert.Single(HostComponents.All, c => c is EmbeddingsComponent);

        Assert.Equal("embeddings", component.Manifest.Id);
    }

    [Fact]
    public void The_setting_is_registered_with_the_composed_registry_and_defaults_to_none()
    {
        using var sp = Compose(Config()).BuildServiceProvider();

        var registry = sp.GetRequiredService<SettingRegistry>();

        Assert.True(registry.TryGet("memory.embedding_profile", out var definition));
        Assert.Equal("\"none\"", definition!.DefaultValueJson);
        Assert.Equal("none", EmbeddingSettings.EmbeddingProfileKey.Default);
    }

    [Fact]
    public void The_setting_store_adapter_replaces_the_default_source()
    {
        var services = Compose(Config());

        var source = Assert.Single(services, d => d.ServiceType == typeof(IEmbeddingSettingSource));

        Assert.Equal(typeof(SettingStoreEmbeddingSettingSource), source.ImplementationType);
    }

    [Fact]
    public void The_setup_step_is_registered_for_the_setup_applier()
    {
        var services = Compose(Config());

        Assert.Contains(services, d => d.ServiceType == typeof(ISetupStep) && d.ImplementationType == typeof(EmbeddingSetupStep));
    }

    [Fact]
    public void No_query_embedder_is_offered_while_the_setting_is_none()
    {
        var services = Compose(Config());
        services.Replace(ServiceDescriptor.Singleton<IActiveEmbeddingSelector>(new StubSelector(null)));
        using var sp = services.BuildServiceProvider();

        var embedder = Assert.IsType<ActiveVaultQueryEmbedder>(sp.GetRequiredService<IVaultQueryEmbedder>());
        Assert.False(embedder.IsAvailable);
        Assert.Equal(Guid.Empty, embedder.ProfileId);
    }

    [Fact]
    public void The_query_embedder_is_offered_when_a_profile_is_active()
    {
        var provider = new FakeEmbeddingProvider();
        var profile = new EmbeddingProfile(Guid.NewGuid(), "fake", provider.Descriptor.ModelId, provider.Descriptor.Dimensions, DateTimeOffset.UnixEpoch, true);
        var services = Compose(Config());
        services.Replace(ServiceDescriptor.Singleton<IActiveEmbeddingSelector>(new StubSelector(new ActiveEmbedding(profile, provider))));
        using var sp = services.BuildServiceProvider();

        var embedder = sp.GetRequiredService<IVaultQueryEmbedder>();

        Assert.True(embedder.IsAvailable);
        Assert.Equal(profile.ProfileId, embedder.ProfileId);
    }

    [Fact]
    public void A_profile_mismatch_offers_no_embedder_so_search_stays_lexical_only()
    {
        var services = Compose(Config());
        services.Replace(ServiceDescriptor.Singleton<IActiveEmbeddingSelector>(new StubSelector(null, new EmbeddingProfileMismatchException("mismatch"))));
        using var sp = services.BuildServiceProvider();

        Assert.False(sp.GetRequiredService<IVaultQueryEmbedder>().IsAvailable);
    }

    [Fact]
    public void The_switch_threshold_adapter_replaces_the_default_source()
    {
        var services = Compose(Config());

        var source = Assert.Single(services, d => d.ServiceType == typeof(IEmbeddingSwitchSettingSource));

        Assert.Equal(typeof(SettingStoreEmbeddingSwitchSettingSource), source.ImplementationType);
    }

    [Fact]
    public async Task The_threshold_adapter_reads_the_setting_and_falls_back_to_the_default()
    {
        var store = new StubSettingStore();
        var source = new SettingStoreEmbeddingSwitchSettingSource(store);

        Assert.Equal(1.0m, await source.GetSwitchThresholdAsync(CancellationToken.None));

        store.Threshold = 0.8m;
        Assert.Equal(0.8m, await source.GetSwitchThresholdAsync(CancellationToken.None));
    }

    [Fact]
    public void The_query_embedder_follows_an_activation_without_a_restart()
    {
        var first = Active();
        var second = Active();
        var selector = new SwitchableSelector(first);
        var status = new StubStatus();
        var embedder = new ActiveVaultQueryEmbedder(selector, status, TimeProvider.System);

        Assert.Equal(first.Profile.ProfileId, embedder.ProfileId);

        selector.Current = second;
        Assert.Equal(first.Profile.ProfileId, embedder.ProfileId); // no activation seen yet, interval not elapsed

        status.Snapshot = EmbeddingStatusSnapshot.Initial with { LastActivatedAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        Assert.Equal(second.Profile.ProfileId, embedder.ProfileId);
        Assert.Equal(2, selector.Calls);
    }

    [Fact]
    public void The_query_embedder_re_resolves_after_the_refresh_interval()
    {
        var selector = new SwitchableSelector(null);
        var clock = new ManualTime();
        var embedder = new ActiveVaultQueryEmbedder(selector, new StubStatus(), clock);

        Assert.False(embedder.IsAvailable);

        var active = Active();
        selector.Current = active;
        clock.Now += ActiveVaultQueryEmbedder.RefreshInterval;

        Assert.True(embedder.IsAvailable);
        Assert.Equal(active.Profile.ProfileId, embedder.ProfileId);
    }

    [Fact]
    public async Task Embedding_with_no_active_profile_is_refused_so_search_never_calls_it()
    {
        var embedder = new ActiveVaultQueryEmbedder(new SwitchableSelector(null), new StubStatus(), TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => embedder.EmbedAsync("q", CancellationToken.None));
    }

    private static ActiveEmbedding Active()
    {
        var provider = new FakeEmbeddingProvider();
        var profile = new EmbeddingProfile(Guid.NewGuid(), "fake", provider.Descriptor.ModelId, provider.Descriptor.Dimensions, DateTimeOffset.UnixEpoch, true);
        return new ActiveEmbedding(profile, provider);
    }

    private sealed class SwitchableSelector(ActiveEmbedding? current) : IActiveEmbeddingSelector
    {
        public ActiveEmbedding? Current { get; set; } = current;

        public int Calls { get; private set; }

        public Task<ActiveEmbedding?> ResolveAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Current);
        }
    }

    private sealed class StubStatus : IEmbeddingStatus
    {
        public EmbeddingStatusSnapshot Snapshot { get; set; } = EmbeddingStatusSnapshot.Initial;

        public Task<EmbeddingStatusSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubSettingStore : ISettingStore
    {
        public decimal? Threshold { get; set; }

        public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) =>
            Task.FromResult(key.Name == EmbeddingSettings.SwitchThresholdKey.Name && Threshold is { } t ? (T)(object)t : key.Default);

        public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingWriteResult> SetJsonAsync(string key, System.Text.Json.JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void Nothing_is_wired_when_the_embeddings_component_is_disabled()
    {
        var services = Compose(Config(("Bazaar:Components:embeddings:Enabled", "false")));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IVaultQueryEmbedder));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IEmbeddingSettingSource));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISetupStep));
    }

    private sealed class StubSelector(ActiveEmbedding? active, Exception? failure = null) : IActiveEmbeddingSelector
    {
        public Task<ActiveEmbedding?> ResolveAsync(CancellationToken cancellationToken) =>
            failure is null ? Task.FromResult(active) : Task.FromException<ActiveEmbedding?>(failure);
    }
}
