using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Community.GitHub;

/// <summary>
/// The GitHub community adapter as a Bazaar Component (docs/design/36, work package 7). The host must also register an
/// <see cref="ICommunityGitHubTokenSource"/> (bridge to IGitHubTokenProvider) and an <see cref="ICommunityPullRequestOpener"/> (bridge to
/// IGitHubAdapter.OpenDraftPrAsync); without them the adapter cannot be built.
/// </summary>
public sealed class CommunityGitHubComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "community-github",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:community-github"],
        // The dispatcher lives in the community component; this adapter is only useful beside it, but loads without it.
        SoftDependsOn: ["community"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddBazaarCommunityGitHub();
}

/// <summary>Registration for the GitHub community adapter.</summary>
public static class CommunityGitHubServiceCollectionExtensions
{
    /// <summary>Registers the adapter, the default footer check and the HTTP API. Everything uses TryAdd, so a host can override any piece.</summary>
    public static IServiceCollection AddBazaarCommunityGitHub(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICommunityFooterCheck, DefaultCommunityFooterCheck>();
        services.TryAddSingleton<ICommunityGitHubApi>(sp =>
        {
            // One long-lived client; the handler recycles connections so DNS changes are picked up.
            var client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            {
                BaseAddress = HttpCommunityGitHubApi.DefaultBaseAddress,
                Timeout = TimeSpan.FromSeconds(30),
            };
            return new HttpCommunityGitHubApi(client, sp.GetRequiredService<ICommunityGitHubTokenSource>());
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICommunityAdapter, GitHubCommunityAdapter>());
        return services;
    }
}
