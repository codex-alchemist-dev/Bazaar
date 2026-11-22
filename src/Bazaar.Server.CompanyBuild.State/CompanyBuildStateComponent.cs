using Bazaar.Core;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.CompanyBuild.State;

/// <summary>
/// The company module selection and build history as a Bazaar Component (docs/design/51-web-admin-module.md section 5, PROPOSED
/// subsection on where the selection lives). Owns the <c>company_build_state</c> migration namespace. It implements the panel's
/// <see cref="IAdminModuleSelection"/> over the existing company-build composer and runner.
/// </summary>
public sealed class CompanyBuildStateComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "company-build-state",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["company-build", "audit"],
        Provides: ["feature:company-build-state", "webadmin:module-selection"],
        SoftDependsOn: ["approval-queue"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddBazaarCompanyBuildState(configuration["Bazaar:CompanyBuild:CompanyId"]);
    }
}

public static class CompanyBuildStateServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Postgres store (over the shared <see cref="NpgsqlDataSource"/>, resolved lazily) and the panel port. The host
    /// supplies <see cref="IModuleCatalogue"/> and, optionally, <see cref="ICompanyBuildExecutor"/> and
    /// <see cref="IModuleApprovalRequirement"/>; <c>IApprovalQueue</c> is picked up when the approval-queue component is enabled.
    /// </summary>
    public static IServiceCollection AddBazaarCompanyBuildState(this IServiceCollection services, string? companyId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new CompanyBuildStateOptions(string.IsNullOrWhiteSpace(companyId) ? "default" : companyId.Trim()));
        services.TryAddSingleton<ICompanyBuildStateStore>(sp => new PostgresCompanyBuildStateStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IAdminModuleSelection, ModuleSelectionService>();
        return services;
    }
}
