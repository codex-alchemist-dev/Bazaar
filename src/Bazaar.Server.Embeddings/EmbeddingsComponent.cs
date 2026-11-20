using Bazaar.Core;
using Bazaar.Server.Embeddings.Indexing;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Embeddings;

/// <summary>
/// The embedding provider adapter as a Bazaar Component (docs/design/08, "WP6 detail"; docs/design/40, "Embeddings module"). Registers the
/// providers from <c>Bazaar:Embeddings</c>, the <c>memory.embedding_profile</c> setting key and the indexer. The host registers
/// <c>NpgsqlDataSource</c> and, to go beyond lexical-only search, an <see cref="IEmbeddingSettingSource"/> over its setting store;
/// until then the setting reads as <c>none</c> and the indexer stays idle.
/// </summary>
public sealed class EmbeddingsComponent : IBazaarComponent
{
    /// <summary>The component manifest.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "embeddings",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["embeddings:provider"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<EmbeddingOptions>().Bind(configuration.GetSection(EmbeddingOptions.SectionName));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISettingContribution, EmbeddingSettingContribution>());
        services.TryAddSingleton<IEmbeddingSettingSource, DefaultEmbeddingSettingSource>();
        services.TryAddSingleton<IEmbeddingProviderRegistry>(sp => new EmbeddingProviderRegistry(
            sp.GetRequiredService<IOptions<EmbeddingOptions>>(),
            sp.GetService<IGatewayEmbeddingClient>(),
            sp.GetService<Bazaar.Server.Secrets.ISecretResolver>()));
        services.TryAddSingleton<IEmbeddingProfileStore, PostgresEmbeddingProfileStore>();
        services.TryAddSingleton<IActiveEmbeddingSelector, ActiveEmbeddingSelector>();
        services.TryAddSingleton<IEmbeddingIndexStore, PostgresEmbeddingIndexStore>();
        services.TryAddSingleton<IPersonalDataBlockDetector, FencedPersonalDataBlockDetector>();
        services.TryAddSingleton(sp => new EmbeddingIndexer(
            sp.GetRequiredService<IActiveEmbeddingSelector>(),
            sp.GetRequiredService<IEmbeddingIndexStore>(),
            sp.GetRequiredService<IPersonalDataBlockDetector>(),
            Math.Max(1, sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value.IndexerBatchSize)));
        services.AddHostedService<EmbeddingIndexerService>();
    }
}
