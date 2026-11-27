using Bazaar.Approvals;
using Cdax.Core;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Community;
using Bazaar.Server.Community.Actions;
using Bazaar.Server.Community.Drafts;
using Bazaar.Server.Community.Observatory;
using Bazaar.Server.Community.Profile;
using Bazaar.Server.Community.Quotas;
using Bazaar.Server.Community.Tools;
using Bazaar.Server.Security.Policy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.Server.Community;

/// <summary>
/// Community and external platforms (docs/design/36-community-and-external-platforms.md) as a Bazaar Component. Host composition is
/// the only caller of <see cref="AddServices"/>. Its SQL is the <c>community</c> migration namespace, which the migration runner reads
/// from the Bazaar.Server assembly; the files live under <c>Data/Sql/community</c> there because the host's migration source is that
/// assembly only (the same rule the GC tables follow, see GcComponent).
/// </summary>
public sealed class CommunityComponent : ICdaxModule
{
    /// <summary>Migration namespace and history key for this component's SQL. Equals <see cref="ComponentManifest.MigrationNamespace"/>.</summary>
    public const string MigrationProvider = "community";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "community",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:community"]);

    /// <summary>
    /// Policy entry for <c>community.propose_post</c> (doc 36 WP8, PROPOSED). The tool only stores a draft; the post needs the sponsor's
    /// signed approval on a client. Tier 1 from an actor session, hard taint escalates it to owner approval, and the rate caps a runaway agent.
    /// </summary>
    public static ToolCapability ProposePostCapability { get; } = new(
        CommunityProposePostTool.ToolName,
        ToolTier.AutonomousWithNotice,
        CapabilityClass.Post,
        SessionRoles.Actor,
        new RateRule(10, TimeSpan.FromHours(1)));

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        // Only dependency-free services register here. The Postgres-backed stores (action log, blocklist, policy loader) need the
        // shared NpgsqlDataSource and IAuditLog from the kernel; the host wires them when it is composed against a database.
        // TODO(component): register the Npgsql stores once the host supplies their dependencies in the composition tests.
        services.TryAddSingleton<ICommunityFreezeStore, InMemoryCommunityFreezeStore>();

        // The Observatory log reader backs GET /community/log (CommunityLogEndpoint, mapped by host composition). It is lazy: it
        // resolves the shared NpgsqlDataSource only when the route first runs, and the route answers 503 when none is registered.
        services.TryAddSingleton<ICommunityLogReader>(sp => new NpgsqlCommunityLogReader(sp.GetRequiredService<NpgsqlDataSource>()));

        // Draft review (doc 36 WP8). The store is lazy over the shared NpgsqlDataSource, like the log reader. The decision handler and
        // the creator are factories: they resolve their dependencies on first use, so a host that has not bound them yet gets an
        // InvalidOperationException there, which the draft routes answer as 503 "unavailable". Supplied by host composition
        // (AddBazaarCommunityHost in Bazaar.Server.Host): ICommunityPolicySource (over PlatformPolicyLoader), the ICommunityAdapter
        // set, IOwnerKeyRegistry (the owner's device keys) and CommunityActionStore (ICommunityActionSql and IAuditLog).
        // DisclosureOptions and CommunityDraftOptions default to the shipped values unless the host registers its own.
        services.TryAddSingleton<ICommunityDraftStore>(sp => new NpgsqlCommunityDraftStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton(new DisclosureOptions());
        services.TryAddSingleton(new CommunityDraftOptions());
        services.TryAddSingleton(sp => new CommunityDraftDecisionHandler(
            sp.GetRequiredService<ICommunityDraftStore>(),
            sp.GetRequiredService<IOwnerKeyRegistry>(),
            sp.GetServices<ICommunityAdapter>(),
            sp.GetRequiredService<CommunityActionStore>(),
            sp.GetRequiredService<ICommunityPolicySource>(),
            sp.GetRequiredService<DisclosureOptions>(),
            sp.GetService<TimeProvider>(),
            sp.GetRequiredService<CommunityDraftOptions>()));
        services.TryAddSingleton<ICommunityDraftCreator>(sp => new CommunityDraftCreator(
            sp.GetRequiredService<ICommunityDraftStore>(),
            sp.GetRequiredService<ICommunityPolicySource>(),
            sp.GetRequiredService<CommunityDraftDecisionHandler>(),
            sp.GetRequiredService<DisclosureOptions>(),
            sp.GetService<TimeProvider>(),
            sp.GetRequiredService<CommunityDraftOptions>()));

        // [PROPOSED] Expiry sweeper (doc 36 WP8): marks pending drafts past their approval expiry as Expired on a timer. Lazy like the
        // handler: the hosted service resolves its sweeper when the host starts, so a host that has not bound the draft store's data
        // source or the action store fails there, as it would on first use of the handler.
        services.TryAddSingleton<ICommunityDraftExpirySweeper>(sp => new CommunityDraftExpirySweeper(
            sp.GetRequiredService<ICommunityDraftStore>(),
            sp.GetRequiredService<CommunityActionStore>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ILogger<CommunityDraftExpirySweeper>>()));
        services.AddHostedService(sp => new CommunityDraftExpiryService(
            () => sp.GetRequiredService<ICommunityDraftExpirySweeper>(),
            sp.GetRequiredService<CommunityDraftOptions>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ILogger<CommunityDraftExpiryService>>()));
        // Agent tool (doc 36 WP8). The agent's sponsor and profile come from ICommunityAgentIdentitySource: by default the operator list in
        // Bazaar:Community:Profiles, which a host can replace. An unlisted agent gets no_profile. Registered lazily so a host without the binding still starts.
        services.TryAddSingleton<ICommunityAgentIdentitySource>(_ => ConfiguredCommunityAgentIdentitySource.From(configuration));
        // Taint lineage for the action log comes from the session taint reader the host registers; without one the tool logs "[]" (the
        // dispatcher's policy engine still gates on the real taint).
        services.TryAddSingleton<ICommunityTaintSourceReader>(sp => sp.GetService<Bazaar.Server.Security.Taint.ISessionTaintReader>() is { } r
            ? new SessionCommunityTaintSourceReader(r)
            : new EmptyCommunityTaintSourceReader());
        services.TryAddSingleton(sp => new CommunityProposePostTool(
            sp.GetRequiredService<ICommunityDraftCreator>(),
            sp.GetRequiredService<ICommunityAgentIdentitySource>(),
            sp.GetService<ICommunityTaintSourceReader>()));
        services.AddSingleton(sp => new ToolRegistration(CommunityProposePostTool.ToolName, sp.GetRequiredService<CommunityProposePostTool>()));
        services.AddSingleton(ProposePostCapability);
        services.AddSingleton(CommunityProposePostTool.Definition);
    }
}
