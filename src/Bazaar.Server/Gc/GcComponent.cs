using Bazaar.Server.Components;
using Bazaar.Server.Gc.Wire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Gc;

/// <summary>
/// The GC log as a Bazaar Component (docs/design/BAZAAR-COMPONENTS.md, docs/design/01-gc-data-model.md). It registers the log writer
/// and reader, the viewer rules, and the wire glue (stream source, access policy, notification source, the subscribe, send and
/// history handlers, and the dispatcher). It needs the kernel's <c>NpgsqlDataSource</c> and the wire and sync services, and depends
/// on no other component. The order of the registrations is the order the host ran them before the conversion.
/// </summary>
/// <remarks>
/// TODO(component): the GC tables still live in the shared <c>common/</c> migration folder
/// (<c>src/Bazaar.Server/Data/Sql/common/</c>). Move them to namespace "gc" once agent/wp-migration-namespaces merges.
/// Until then this component owns no migration files.
/// </remarks>
public sealed class GcComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "gc",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:gc-log", "feature:gc-wire"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        // Same order as before: the log first (AddBazaarGcWire skips it when it is already present), then the viewer rules.
        services.AddGcLog();
        services.AddGcReadAccess();
        services.AddBazaarGcWire();
    }
}
