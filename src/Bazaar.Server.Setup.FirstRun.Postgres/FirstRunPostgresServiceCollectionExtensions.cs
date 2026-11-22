using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Setup.FirstRun.Postgres;

/// <summary>Registers the Postgres-backed ports of the first-run seed. The host supplies <c>NpgsqlDataSource</c>, <c>IAuditLog</c> and the Agents and gateway ports.</summary>
public static class FirstRunPostgresServiceCollectionExtensions
{
    /// <summary>Adds the owner, store, acceptance and audit adapters, and a no-commit default for <see cref="ICurrentConstitutionCommit"/>.</summary>
    public static IServiceCollection AddFirstRunPostgres(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICurrentConstitutionCommit, NoCurrentConstitutionCommit>();
        services.TryAddSingleton<IFirstRunOwner, PostgresFirstRunOwner>();
        services.TryAddSingleton<IFirstRunStore, PostgresFirstRunStore>();
        services.TryAddSingleton<IConstitutionAcceptance, PostgresConstitutionAcceptance>();
        services.TryAddSingleton<IFirstRunAudit, AuditLogFirstRunAudit>();
        return services;
    }
}
