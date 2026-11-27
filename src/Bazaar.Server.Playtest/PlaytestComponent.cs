using Cdax.Core;
using Bazaar.Server.Playtest.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Playtest;

/// <summary>
/// Playtest Mode Tier A (docs/design/26-fun-systems.md section 7) as a Bazaar Component. Its SQL is the <c>playtest</c> migration
/// namespace, embedded in this assembly. The host supplies the headless host, project hasher and note writer (the real VM runner and
/// reader session are separate work packages); until then the run store defaults to memory.
/// </summary>
public sealed class PlaytestComponent : ICdaxModule
{
    /// <summary>Migration namespace and history key for this component's SQL. Equals <see cref="ComponentManifest.MigrationNamespace"/>.</summary>
    public const string MigrationProvider = "playtest";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "playtest",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:playtest"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) => AddBazaarPlaytest(services);

    /// <summary>Registers the runner, default settings and the in-memory store. A host that has a data source replaces the store with <see cref="PostgresPlaytestRunStore"/>.</summary>
    public static IServiceCollection AddBazaarPlaytest(IServiceCollection services)
    {
        services.TryAddSingleton(new PlaytestSettings());
        services.TryAddSingleton<IPlaytestRunStore, InMemoryPlaytestRunStore>();
        services.TryAddSingleton<IPlaytestRunner, PlaytestRunner>();
        return services;
    }
}

public static class PlaytestServiceCollectionExtensions
{
    public static IServiceCollection AddBazaarPlaytest(this IServiceCollection services) => PlaytestComponent.AddBazaarPlaytest(services);
}
