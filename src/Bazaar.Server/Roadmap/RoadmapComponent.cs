using Bazaar.Server.Components;
using Bazaar.Server.Vault;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Roadmap;

/// <summary>
/// The objective roadmap (doc 38, Layer 3) as a Bazaar Component. Registers <see cref="RoadmapSnapshotService"/>, which reads
/// the Vault index and the kanban task tables through the shared <c>NpgsqlDataSource</c>. It depends on <c>vault</c> (the
/// documents) and <c>kanban</c> (the task links and states).
/// </summary>
/// <remarks>
/// The component's SQL (migration <c>roadmap/0001</c>, views only) stays in <c>Data/Sql/roadmap/</c>. That file has been applied
/// on live databases, and the runner refuses a recorded migration whose file is gone, so it cannot move to another namespace.
/// The component therefore has no migration folder of its own: its id is <c>ryusei-roadmap</c> as doc 38 names it.
/// </remarks>
public sealed class RoadmapComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "ryusei-roadmap",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["vault", "kanban"],
        Provides: ["feature:ryusei-roadmap"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(sp => new RoadmapSnapshotService(sp.GetRequiredService<NpgsqlDataSource>()));
    }
}
