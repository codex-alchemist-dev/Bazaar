using Cdax.Core;
using Bazaar.Server.EngineDispatch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.EngineGcRecorder;

/// <summary>Registers <see cref="GcEngineCallRecorder"/> as the engine call recorder (doc 20 Audit). Needs the GC log writer from the host.</summary>
public sealed class EngineGcRecorderComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "engine-gc-recorder",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:engine-gc-recorder"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IEngineGcRecorder, GcEngineCallRecorder>();
    }
}
