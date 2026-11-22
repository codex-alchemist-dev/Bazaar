using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Setup.FirstRun.Postgres;

/// <summary>Registers the Postgres stores behind the first-run seed ports. The caller registers the shared <c>NpgsqlDataSource</c> and an <c>IAuditLog</c> first.</summary>
public static class FirstRunPostgresServiceCollectionExtensions
{
    /// <summary>Adds the five port implementations (TryAdd, so a caller's own wins).</summary>
    public static IServiceCollection AddFirstRunPostgres(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IFirstRunOwner, PostgresFirstRunOwner>();
        services.TryAddSingleton<IFirstRunStore, PostgresFirstRunStore>();
        services.TryAddSingleton<IConstitutionAcceptance, PostgresConstitutionAcceptance>();
        services.TryAddSingleton<IDefaultAgentRoutes, PostgresDefaultAgentRoutes>();
        services.TryAddSingleton<IFirstRunAudit>(sp => new AuditLogFirstRunAudit(
            sp.GetRequiredService<Bazaar.Server.Security.Audit.IAuditLog>(), sp.GetService<TimeProvider>()));
        return services;
    }
}
