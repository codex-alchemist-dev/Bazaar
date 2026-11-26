using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.SignIn.Postgres;

/// <summary>Registers the shared handoff-code store. Call it before the sign-in module adds its in-memory default, so the shared store wins.</summary>
public static class SignInPostgresServiceCollectionExtensions
{
    /// <summary>Registers <see cref="PostgresHandoffCodeStore"/> as <see cref="IHandoffCodeStore"/> (TryAdd). Needs an <c>NpgsqlDataSource</c> in the container.</summary>
    public static IServiceCollection AddBazaarSignInPostgres(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IHandoffCodeStore, PostgresHandoffCodeStore>();
        return services;
    }
}
