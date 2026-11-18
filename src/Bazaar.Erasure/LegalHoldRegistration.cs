using Bazaar.Erasure.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Erasure;

public static class LegalHoldRegistration
{
    /// <summary>
    /// Registers the Postgres legal hold reader for <see cref="ILegalHoldReader"/>. The host composes this; the erasure
    /// coordinator in Bazaar.Server depends only on the abstraction. The host must register an <c>NpgsqlDataSource</c> first.
    /// </summary>
    public static IServiceCollection AddBazaarLegalHoldReader(this IServiceCollection services)
    {
        services.AddSingleton<ILegalHoldReader, PostgresLegalHoldReader>();
        return services;
    }
}
