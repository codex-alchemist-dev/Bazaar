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

        Assert.Null(sp.GetService<IVaultQueryEmbedder>());
    }

    [Fact]
    public void The_query_embedder_is_offered_when_a_profile_is_active()
    {
        var provider = new FakeEmbeddingProvider();
        var profile = new EmbeddingProfile(Guid.NewGuid(), "fake", provider.Descriptor.ModelId, provider.Descriptor.Dimensions, DateTimeOffset.UnixEpoch, true);
        var services = Compose(Config());
        services.Replace(ServiceDescriptor.Singleton<IActiveEmbeddingSelector>(new StubSelector(new ActiveEmbedding(profile, provider))));
        using var sp = services.BuildServiceProvider();

        var embedder = Assert.IsType<VaultQueryEmbedder>(sp.GetService<IVaultQueryEmbedder>());

        Assert.Equal(profile.ProfileId, embedder.ProfileId);
    }

    [Fact]
    public void A_profile_mismatch_offers_no_embedder_so_search_stays_lexical_only()
    {
        var services = Compose(Config());
        services.Replace(ServiceDescriptor.Singleton<IActiveEmbeddingSelector>(new StubSelector(null, new EmbeddingProfileMismatchException("mismatch"))));
        using var sp = services.BuildServiceProvider();

        Assert.Null(sp.GetService<IVaultQueryEmbedder>());
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
