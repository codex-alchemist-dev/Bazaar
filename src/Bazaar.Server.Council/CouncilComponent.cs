using Bazaar.Core;
using Bazaar.Server.Council.Data;
using Bazaar.Server.Council.Service;
using Bazaar.Server.Council.Settings;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Council;

/// <summary>
/// Council (docs/design/10-council-and-voting.md) as a Bazaar Component. Host composition is the only caller of
/// <see cref="AddServices"/>. Its SQL is the <c>council</c> migration namespace, embedded in this assembly under
/// <c>Sql/components/council</c> and read by the host's component migration source.
/// </summary>
/// <remarks>
/// Registers <see cref="ICouncilStore"/> (over the shared <see cref="NpgsqlDataSource"/>) and <see cref="ICouncilService"/>. The
/// ports that need Server or GC types are supplied by the host: <see cref="ICouncilReasoningCipher"/>, <see cref="ICouncilGcEvents"/>
/// and <see cref="IGcMembership"/> (doc 40). The Vault decision writer and the dissent-share source are optional ports.
/// Hiring (<c>AddBazaarCouncilHiring</c>) is not registered here: it needs the Talent Pool's <c>IHireDecisionStore</c>, so the
/// Talent Pool component owns that call. The deadline sweeper is a host opt-in (<c>AddBazaarCouncilDeadlineSweeper</c>).
/// </remarks>
public sealed class CouncilComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "council",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["gc"],
        Provides: ["feature:council"],
        SoftDependsOn: ["vault"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<ICouncilStore>(sp => new CouncilStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<ICouncilReasoningCipher>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISettingContribution, CouncilSettingContribution>());
        services.AddBazaarCouncilService();
    }
}
