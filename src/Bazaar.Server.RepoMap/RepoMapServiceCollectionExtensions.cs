using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.RepoMap;

/// <summary>Registers the repo map and search tool.</summary>
public static class RepoMapServiceCollectionExtensions
{
    /// <summary>
    /// Registers the search tool, the repo map builder, the C# extractor and the default token counter.
    /// The host must also register an <see cref="IRepoWorkspace"/>. Registrations use TryAdd, so the host can replace any of them.
    /// </summary>
    public static IServiceCollection AddBazaarRepoMap(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ITokenCounter, CharQuarterTokenCounter>();
        services.TryAddSingleton<ISymbolExtractor, RoslynCSharpSymbolExtractor>();
        services.TryAddSingleton<ISearchTool, RipgrepSearchTool>();
        services.TryAddSingleton<IRepoMapBuilder, RepoMapBuilder>();
        return services;
    }
}
