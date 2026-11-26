using Bazaar.Erasure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Erasure;

public static class LegalHoldRegistration
{
    /// <summary>
    /// Registers the Postgres legal hold reader for <see cref="ILegalHoldReader"/> and <see cref="ILegalHoldScopeReader"/>. The host composes this; the erasure
    /// coordinator in Bazaar.Server depends only on the abstraction. The host must register an <c>NpgsqlDataSource</c> first.
    /// </summary>
    public static IServiceCollection AddBazaarLegalHoldReader(this IServiceCollection services)
    {
        services.AddSingleton<PostgresLegalHoldReader>();
        services.AddSingleton<ILegalHoldReader>(sp => sp.GetRequiredService<PostgresLegalHoldReader>());
        services.AddSingleton<ILegalHoldScopeReader>(sp => sp.GetRequiredService<PostgresLegalHoldReader>());
        return services;
    }

    /// <summary>
    /// Registers the owner-signed hold admin (<see cref="ILegalHoldAdmin"/>) over the Postgres store. The host must also register an
    /// <c>NpgsqlDataSource</c>, an <see cref="Bazaar.Server.Security.Audit.ITransactionalAuditLog"/> and the owner-signature verifier
    /// (<c>AddBazaarApprovals</c>).
    /// </summary>
    public static IServiceCollection AddBazaarLegalHoldAdmin(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ILegalHoldStore, PostgresLegalHoldStore>();
        services.TryAddSingleton<ILegalHoldAdmin, LegalHoldService>();
        return services;
    }
}
