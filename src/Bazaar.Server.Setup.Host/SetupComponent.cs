using Bazaar.Core;
using Bazaar.RateLimits;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup.Http;
using Bazaar.Server.Setup.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Setup.Host;

/// <summary>
/// Server setup as a Bazaar Component (docs/design/45-onboarding.md section 1.9, docs/design/40-core-feature-modules-audit.md). It
/// registers the apply engine, the provisioning API (its three routes are mapped by <see cref="SetupEndpointRouting.MapBazaarSetup"/> only when this
/// component is enabled), the durable Postgres stores, and the host adapters. It depends on <c>audit</c> (the audit log) and
/// <c>settings</c> (the registry and the settings audit sink). The component lives in this composition-root project because the
/// adapters bind <c>Bazaar.Server</c> types to the setup seams; the setup libraries themselves never reference the server.
/// </summary>
/// <remarks>
/// No apply step is registered by this slice (<see cref="Steps"/> is empty), so an apply of a plan that changes anything ends in
/// exit 3 with one audit row. The routes are gated: before an owner exists a call needs loopback and the one-time bootstrap code,
/// and afterwards the owner role.
/// </remarks>
public sealed class SetupComponent(IEnumerable<ISetupStep>? steps = null) : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "setup",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["audit", "settings"],
        Provides: ["feature:setup"]);

    /// <summary>The apply steps this component registers. Empty until the database and custody steps land.</summary>
    public IReadOnlyList<ISetupStep> Steps { get; } = [.. steps ?? []];

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        // Presence of the marker is what MapBazaarSetup checks: no component, no routes (404).
        services.TryAddSingleton<SetupComponentMarker>();

        // Seams to the host. TryAdd, so a test or an operator's own implementation wins.
        services.TryAddSingleton<ISetupSettingCatalog>(sp => new RegistrySetupSettingCatalog(sp.GetRequiredService<SettingRegistry>()));
        services.TryAddSingleton<ISetupSettingAudit>(sp => new SettingAuditSinkSetupAudit(sp.GetRequiredService<ISettingAuditSink>()));
        services.TryAddSingleton<ISetupCallerAuthenticator, ConnectionSetupCallerAuthenticator>();
        services.TryAddSingleton<ISetupTransportPolicy, WireSetupTransportPolicy>();
        services.TryAddSingleton<ISetupProgressSource>(_ => new StepListProgressSource(Steps));

        // Durable owner and bootstrap-code stores on the one shared data source.
        services.TryAddSingleton<ISetupOwnerDirectory>(sp => new PostgresSetupOwnerDirectory(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton(sp => new PostgresBootstrapCodeStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IBootstrapCodeStore>(sp => new PostgresBootstrapCodeStoreAdapter(sp.GetRequiredService<PostgresBootstrapCodeStore>()));
        services.TryAddSingleton<ISetupAccessGate>(sp => new SetupAccessGate(
            sp.GetRequiredService<IBootstrapCodeStore>(), sp.GetRequiredService<ISetupOwnerDirectory>()));
        services.AddHostedService<SetupBootstrapCodeIssuer>();

        // The setup rate bucket needs a limiter; this registers the in-process one unless the host already has a limiter.
        services.AddBazaarRateLimits();

        services.AddBazaarSetupPostgres();
        services.AddSetupApply(Steps);
        services.AddSetupHttp();
    }
}

/// <summary>Maps the provisioning routes for a host that ships <see cref="SetupComponent"/>.</summary>
public static class SetupEndpointRouting
{
    /// <summary>
    /// Maps the provisioning routes when the setup component is enabled in this host, and nothing otherwise, so a host without the
    /// component answers 404 on <c>/setup/*</c>. Call once from the composition root, after the services are built.
    /// </summary>
    public static IEndpointRouteBuilder MapBazaarSetup(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.ServiceProvider.GetService<SetupComponentMarker>() is not null)
        {
            routes.MapSetupEndpoints();
        }

        return routes;
    }
}

/// <summary>Registered by <see cref="SetupComponent"/>; its presence in the container means the component is enabled.</summary>
internal sealed class SetupComponentMarker;
