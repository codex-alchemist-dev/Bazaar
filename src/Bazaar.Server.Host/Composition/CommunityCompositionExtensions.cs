using Bazaar.Approvals;
using Bazaar.Server.Community.Actions;
using Bazaar.Server.Community.Drafts;
using Bazaar.Server.Community.GitHub;
using Bazaar.Server.Community.GitHub.Bridge;
using Bazaar.Server.Community.Policy;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Binds the seams the Community component leaves open (doc 36 WP7 and WP8, doc 40). The component's draft decision handler and
/// creator are factories that throw until these exist, which the draft routes answer as 503; after this call they resolve.
/// Every registration is TryAdd, so a test or an operator build can substitute any part first.
/// </summary>
public static class CommunityCompositionExtensions
{
    /// <summary>
    /// Registers the policy source over <see cref="PlatformPolicyLoader"/>, the GitHub community adapter with its Bazaar.Server.GitHub bridge
    /// (the installation allowlist is read from <c>Bazaar:Community:GitHub:Installations</c>; an unlisted repository is refused, never
    /// posted to), the owner key registry (through <c>AddBazaarApprovals</c>) and the community action store. Needs the shared
    /// <see cref="NpgsqlDataSource"/> and <see cref="IAuditLog"/>, which other host modules supply. The GitHub token provider and
    /// adapter come from <c>GitHubComponent</c>; without an Identity Service address the token provider fails closed.
    /// </summary>
    public static IServiceCollection AddBazaarCommunityHost(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp => new PlatformPolicyLoader(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<ICommunityPolicySource>(sp => new PlatformPolicySource(sp.GetRequiredService<PlatformPolicyLoader>()));

        // The owner's device keys (PostgresOwnerKeyRegistry). TryAdd inside, so the agent-safety module's earlier call wins harmlessly.
        services.AddBazaarApprovals();

        services.TryAddSingleton<ICommunityActionSql>(sp => new NpgsqlCommunityActionSql(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton(sp => new CommunityActionStore(
            sp.GetRequiredService<ICommunityActionSql>(), sp.GetRequiredService<IAuditLog>(), sp.GetService<TimeProvider>()));

        // GitHubCommunityAdapter (an ICommunityAdapter), its HTTP API and footer check, then the bridge to Bazaar.Server.GitHub.
        services.AddBazaarCommunityGitHub();
        services.AddBazaarCommunityGitHubBridge();
        return services;
    }
}
