using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Community.GitHub.Bridge;

public static class CommunityGitHubBridgeServiceCollectionExtensions
{
    /// <summary>
    /// Binds the community GitHub seams to Bazaar.GitHub. The host must register <c>IGitHubTokenProvider</c>, <c>IGitHubAdapter</c>
    /// and an <see cref="ICommunityInstallationResolver"/>. Uses TryAdd, so a host can override either binding.
    /// </summary>
    public static IServiceCollection AddBazaarCommunityGitHubBridge(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICommunityGitHubTokenSource, BridgeCommunityGitHubTokenSource>();
        services.TryAddSingleton<ICommunityPullRequestOpener, BridgeCommunityPullRequestOpener>();
        return services;
    }
}
