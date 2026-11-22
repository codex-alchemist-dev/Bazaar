using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.ThinkingGc;

/// <summary>
/// The Thinking GC module (docs/design/01-gc-data-model.md, Thinking GCs [DECIDED, round 3]). It gives each agent a Thinking GC
/// nested under every GC it works in, and writes the agent's reasoning and turn steps there. Needs the GC component (the log
/// writer and body codec) and the host's data source. Nothing is ever swept: the log is append-only and thinking is kept forever.
/// </summary>
public sealed class ThinkingGcComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "thinking-gc",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["gc"],
        Provides: ["feature:thinking-gc"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(sp => new ThinkingGcStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IThinkingGcLookup>(sp => new ThinkingGcLookup(sp.GetRequiredService<NpgsqlDataSource>()));
        // Off by default. Operators turn the collapsed pointer event on with Bazaar:ThinkingGc:PointerEvent (doc 01, PROPOSED).
        var pointerOn = bool.TryParse(configuration["Bazaar:ThinkingGc:PointerEvent"], out var on) && on;
        services.TryAddSingleton<IThinkingPointerPolicy>(new StaticThinkingPointerPolicy(pointerOn));
        services.TryAddSingleton<IThinkingRecorder>(sp => new ThinkingGcRecorder(
            sp.GetRequiredService<ThinkingGcStore>(),
            sp.GetRequiredService<Bazaar.Server.Gc.Write.IGcLogWriter>(),
            sp.GetRequiredService<Bazaar.Server.Gc.Body.IGcBodyCodec>(),
            sp.GetService<Bazaar.Souls.Abstractions.ISoulBindings>(),
            sp.GetService<IThinkingPointerPolicy>()));
    }
}
