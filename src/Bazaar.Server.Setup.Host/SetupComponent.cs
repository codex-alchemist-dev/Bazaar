using Cdax.Core;
using Bazaar.Server.Company;
using Bazaar.Server.RateLimits;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup.Constitution;
using Bazaar.Server.Setup.FirstRun;
using Bazaar.Server.Setup.FirstRun.Postgres;
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
/// The component registers the real apply steps: <see cref="InstallSettingsStep"/> (the plan's install-scope settings), <see cref="CompanyProfileStep"/> (the company profile), <see cref="OwnerStep"/> (the owner) and
/// <see cref="ConstitutionSeedStep"/> (the default Constitution in the Vault) and <see cref="FirstRunSeedStep"/> (the first GC and default agent, over the Postgres stores in <c>Bazaar.Server.Setup.FirstRun.Postgres</c>). A host or test
/// can pass its own steps to the constructor instead, and those replace the defaults. A step whose backing store is not in the tree
/// is not registered (see doc 45 section 1.3 status rows). With no step registered, an apply of a plan that changes anything ends in
/// exit 3 with one audit row. The routes are gated: before an owner exists a call needs loopback and the one-time bootstrap code,
/// and afterwards the owner role.
/// </remarks>
public sealed class SetupComponent(IEnumerable<ISetupStep>? steps = null) : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "setup",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["audit", "settings"],
        Provides: ["feature:setup"]);

    /// <summary>The apply steps passed to the constructor. Empty means the component registers its default steps.</summary>
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
        services.TryAddSingleton<ISetupProgressSource>(sp => new StepListProgressSource([.. sp.GetServices<ISetupStep>()]));

        // Durable owner and bootstrap-code stores on the one shared data source.
        services.TryAddSingleton<ISetupOwnerDirectory>(sp => new PostgresSetupOwnerDirectory(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton(sp => new PostgresBootstrapCodeStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IBootstrapCodeStore>(sp => new PostgresBootstrapCodeStoreAdapter(sp.GetRequiredService<PostgresBootstrapCodeStore>()));
        services.TryAddSingleton<ISetupAccessGate>(sp => new SetupAccessGate(
            sp.GetRequiredService<IBootstrapCodeStore>(), sp.GetRequiredService<ISetupOwnerDirectory>()));

        // The config-path door (doc 45 section 1.1): --setup-plan or BAZAAR_SETUP_PLAN applies a local plan file before serving. It starts
        // before the code issuer, so a plan that creates the owner retires the code and --setup-only never announces one.
        services.TryAddSingleton(SetupPlanStartOptions.FromConfiguration(configuration));
        services.TryAddSingleton<SetupPlanStartOutcome>();
        services.AddHostedService<SetupPlanOnStartService>();
        services.AddHostedService<SetupBootstrapCodeIssuer>();

        // The setup rate bucket needs a limiter; this registers the in-process one unless the host already has a limiter.
        services.AddBazaarRateLimits();

        // The verifier (doc 45 section 1.12). Probes answer infrastructure checks by id. Any ISetupVerificationCheck a module registers
        // replaces the placeholder with the same id, so a module owns its own check without Setup referencing it.
        services.TryAddSingleton<ISetupVerifier>(sp => new SetupVerifier(SetupVerificationChecks.Create(
            SetupVerificationProbeSet.Build(sp),
            replacements: sp.GetServices<ISetupVerificationCheck>())));

        services.AddBazaarSetupPostgres();
        RegisterSteps(services, configuration);
        services.AddSetupApply();
        services.AddSetupHttp();
    }

    private void RegisterSteps(IServiceCollection services, IConfiguration configuration)
    {
        if (Steps.Count > 0)
        {
            foreach (var step in Steps)
            {
                services.AddSingleton(step);
            }

            return;
        }

        services.AddSingleton<ISetupStep>(sp => new InstallSettingsStep(
            sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<ISetupSettingCatalog>()));
        services.AddSingleton<ISetupStep>(sp => new GatewaySourcesStep(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetService<IModelSourceCatalogue>()));

        // The company profile (install.identity) and the owner (owner, owner.recovery), applied before the seeds that need them: the
        // first-run seed finds the owner row this creates. Their stores and tables are in Bazaar.Server.Company.
        services.AddCompanySetup();

        // The default Constitution in the Vault (doc 24), drafted just before the first-run seed so there is a commit to accept. The Vault
        // repository and writer identity come from the same Memory:Vault keys as the memory module. Unset, the step fails with a message.
        services.AddConstitutionSeed(ConstitutionVaultOptions.FromSettings(key => configuration[key]));

        // The first-run seed (doc 45 section 1.15) with its Postgres stores. It needs the owner step's row, so it runs after it.
        services.AddFirstRunPostgres();
        services.AddFirstRunSeed();
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
