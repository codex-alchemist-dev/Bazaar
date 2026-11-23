using Bazaar.Server.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Company;

/// <summary>Registers the company profile and owner steps with their Postgres stores. The caller registers the shared <c>NpgsqlDataSource</c> and an <c>IAuditLog</c>.</summary>
public static class CompanyServiceCollectionExtensions
{
    /// <summary>Adds the two stores and reader (TryAdd, so a caller's own wins) and both steps as <see cref="ISetupStep"/>.</summary>
    public static IServiceCollection AddCompanySetup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICompanyProfileReader>(sp => new PostgresCompanyProfileReader(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<ICompanyProfileStore>(sp => new PostgresCompanyProfileStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IOwnerStore>(sp => new PostgresOwnerStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<ISetupStep>(sp => new CompanyProfileStep(
            sp.GetRequiredService<ICompanyProfileStore>(), sp.GetRequiredService<Bazaar.Server.Security.Audit.IAuditLog>(), sp.GetService<TimeProvider>()));
        services.AddSingleton<ISetupStep>(sp => new OwnerStep(
            sp.GetRequiredService<IOwnerStore>(), sp.GetRequiredService<Bazaar.Server.Security.Audit.IAuditLog>(), sp.GetService<TimeProvider>()));
        return services;
    }
}
