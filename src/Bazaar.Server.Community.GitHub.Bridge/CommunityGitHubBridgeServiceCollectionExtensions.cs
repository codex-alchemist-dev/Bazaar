using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Community.GitHub.Bridge;

public static class CommunityGitHubBridgeServiceCollectionExtensions
{
    /// <summary>
    /// Binds the community GitHub seams to Bazaar.GitHub. The host must register <c>IGitHubTokenProvider</c>, <c>IGitHubAdapter</c>
    /// and optionally an <see cref="ICommunityInstallationResolver"/>; the default is <see cref="ConfiguredCommunityInstallationResolver"/>. Uses TryAdd, so a host can override any binding.
    /// </summary>
    public static IServiceCollection AddBazaarCommunityGitHubBridge(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Default resolver: the operator allowlist, bound from the host IConfiguration when one is registered. A host-supplied resolver wins.
        services.AddOptions<CommunityInstallationOptions>()
            .Configure<IServiceProvider>((options, sp) =>
            {
                var entries = sp.GetService<IConfiguration>()?.GetSection(CommunityInstallationOptions.SectionName).Get<List<CommunityInstallationEntry>>();
                if (entries is not null)
                {
                    options.Entries = entries;
                }
            })
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CommunityInstallationOptions>, CommunityInstallationOptionsValidator>());
        services.TryAddSingleton<ICommunityInstallationResolver, ConfiguredCommunityInstallationResolver>();
        services.TryAddSingleton<ICommunityGitHubTokenSource, BridgeCommunityGitHubTokenSource>();
        services.TryAddSingleton<ICommunityPullRequestOpener, BridgeCommunityPullRequestOpener>();
        return services;
    }
}
