using Cdax.Core;
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

        if (!CdaxModuleRegistration.IsEnabled(configuration, EmbeddingsComponent.ManifestDefinition.Id)
            || !CdaxModuleRegistration.IsEnabled(configuration, "settings"))
        {
            return services;
        }

        // The module registered a default source (none) with TryAdd; the real one replaces it.
        services.Replace(ServiceDescriptor.Singleton<IEmbeddingSettingSource, SettingStoreEmbeddingSettingSource>());

        // The embedder is resolved once, at first use. With the setting at none (the default), no active profile, or a provider that
        // does not match the profile, nothing is offered and Vault search stays lexical-only, as before. A changed profile is a planned
        // re-embed and takes effect at the next start.
        services.TryAddSingleton(typeof(IVaultQueryEmbedder), ResolveQueryEmbedder);
        return services;
    }

    private static object ResolveQueryEmbedder(IServiceProvider services)
    {
        var selector = services.GetRequiredService<IActiveEmbeddingSelector>();
        try
        {
            var active = selector.ResolveAsync(CancellationToken.None).GetAwaiter().GetResult();
            // A null here means "not offered": the Vault search constructor takes the embedder as optional.
            return active is null ? null! : new VaultQueryEmbedder(active.Profile, active.Provider);
        }
        catch (EmbeddingException ex)
        {
            if (services.GetService<ILoggerFactory>()?.CreateLogger("Bazaar.Server.Embeddings") is { } logger)
            {
                _lexicalOnly(logger, ex);
            }

            return null!;
        }
    }

    private static readonly Action<ILogger, Exception> _lexicalOnly = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, "EmbeddingProfileMismatch"), "Embedding profile does not match its provider; Vault search stays lexical-only.");
}
