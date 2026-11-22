using Bazaar.Server.GitSolarSystem.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.GitSolarSystem;

/// <summary>Registration of the Git Solar System module. Every registration uses TryAdd, so the host can override any of them.</summary>
public static class GitSolarSystemServiceCollectionExtensions
{
    /// <summary>
    /// Registers the feed service and the default settings. The ports <see cref="IGitSolarHistorySource"/> and
    /// <see cref="IGitSolarLayout"/> have no default here: the host must register their adapters (a follow-up).
    /// </summary>
    public static IServiceCollection AddBazaarGitSolarSystem(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IGitSolarSettings>(new GitSolarSettings());
        services.TryAddSingleton<GitSolarFeedService>();
        return services;
    }
}
