using Bazaar.Core;
using Bazaar.Server.Embeddings;
using Bazaar.Server.Host.Components.Embeddings;
using Bazaar.Server.Vault.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Host wiring for the Embeddings module (doc 08 WP6 detail; doc 40 "Embeddings module"). The module registers its own services, setting
/// and setup step; the host adds what needs <c>Bazaar.Server</c>: the setting-store adapter, and the query embedder Vault search receives.
/// </summary>
public static class EmbeddingsCompositionExtensions
{
    /// <summary>
    /// Does nothing unless the <c>embeddings</c> and <c>settings</c> components are both enabled; without them the module keeps its
    /// default of <c>none</c>. Call after the components are registered.
    /// </summary>
    public static IServiceCollection AddBazaarEmbeddingsHost(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!BazaarComponentRegistration.IsEnabled(configuration, EmbeddingsComponent.ManifestDefinition.Id)
            || !BazaarComponentRegistration.IsEnabled(configuration, "settings"))
        {
            return services;
        }

        // The module registered a default source (none) with TryAdd; the real one replaces it.
        services.Replace(ServiceDescriptor.Singleton<IEmbeddingSettingSource, SettingStoreEmbeddingSettingSource>());

        services.Replace(ServiceDescriptor.Singleton<IEmbeddingSwitchSettingSource, SettingStoreEmbeddingSwitchSettingSource>());

        // One holder follows the active profile: it re-resolves after an activation, so a planned switch needs no restart. With none
        // selected, or a provider that does not match the profile (logged), it reports itself unavailable and Vault search stays
        // lexical-only, as before.
        services.TryAddSingleton<IVaultQueryEmbedder>(sp => new ActiveVaultQueryEmbedder(
            sp.GetRequiredService<IActiveEmbeddingSelector>(),
            sp.GetRequiredService<IEmbeddingStatus>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            ex =>
            {
                if (sp.GetService<ILoggerFactory>()?.CreateLogger("Bazaar.Server.Embeddings") is { } logger)
                {
                    _lexicalOnly(logger, ex);
                }
            }));
        return services;
    }

    private static readonly Action<ILogger, Exception> _lexicalOnly = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, "EmbeddingProfileMismatch"), "Embedding profile does not match its provider; Vault search stays lexical-only.");
}
