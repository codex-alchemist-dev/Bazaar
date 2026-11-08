using Bazaar.Server.Components;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// RyuseiFuda schedule (doc 38, Layer 2) as a Bazaar Component. Registers <see cref="RyuseiTriggerService"/> with the caps
/// bound from <see cref="SectionName"/>. It needs the shared <c>NpgsqlDataSource</c> and <see cref="IAuditLog"/>. The
/// scheduler tick, the event bridge and the agent tools come in later work packages (doc 38, WP-R5 to WP-R9).
/// </summary>
public sealed class RyuseiScheduleComponent : IBazaarComponent
{
    /// <summary>Configuration section for the RyuseiFuda caps (doc 38, Settings).</summary>
    public const string SectionName = "Bazaar:Ryusei";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "ryusei-schedule",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["kanban"],
        Provides: ["feature:ryusei-schedule"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var limits = new RyuseiLimits();
        configuration.GetSection(SectionName).Bind(limits);
        services.TryAddSingleton(limits);
        services.TryAddSingleton(sp => new RyuseiTriggerService(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<IAuditLog>(),
            sp.GetRequiredService<RyuseiLimits>()));
    }
}
