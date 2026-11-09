using Bazaar.Server.Community.Actions;
using Bazaar.Server.Community.Blocklist;
using Bazaar.Server.Community.Policy;
using Bazaar.Server.Community.Quotas;
using Bazaar.Server.Components;
using Bazaar.Server.Security.Policy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Community;

/// <summary>
/// Community and external platforms (docs/design/36-community-and-external-platforms.md) as a Bazaar Component. Host composition is
/// the only caller of <see cref="AddServices"/>. Its SQL is the <c>community</c> migration namespace, which the migration runner reads
/// from the Bazaar.Server assembly; the files live under <c>Data/Sql/community</c> there because the host's migration source is that
/// assembly only (the same rule the GC tables follow, see GcComponent).
/// </summary>
public sealed class CommunityComponent : IBazaarComponent
{
    /// <summary>Migration namespace and history key for this component's SQL. Equals <see cref="ComponentManifest.MigrationNamespace"/>.</summary>
    public const string MigrationProvider = "community";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "community",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:community"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<PlatformPolicyLoader>();
        services.TryAddSingleton<NpgsqlTargetBlocklist>();
        services.TryAddSingleton<ITargetBlocklist>(sp => sp.GetRequiredService<NpgsqlTargetBlocklist>());
        services.TryAddSingleton<ICommunityActionSql, NpgsqlCommunityActionSql>();
        services.TryAddSingleton<CommunityActionStore>();
        services.TryAddSingleton<KillSwitch>();

        services.TryAddSingleton<IRateBucketStore, InMemoryRateBucketStore>();
        services.TryAddSingleton<ICommunityFreezeStore, InMemoryCommunityFreezeStore>();
        services.TryAddSingleton<CommunityQuotaGuard>();
    }
}
