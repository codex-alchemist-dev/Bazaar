using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.DeviceEnrolment.Postgres;

/// <summary>Registers the durable enrolment store. The caller registers the shared <see cref="NpgsqlDataSource"/> first.</summary>
public static class DeviceEnrolmentPostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresEnrolmentStore"/> as <see cref="IEnrolmentStore"/> (TryAdd, so a caller's own store wins). The ceremony
    /// module registers no store of its own, so there is nothing to replace; call this before or after <c>AddBazaarDeviceEnrolment</c>.
    /// </summary>
    public static IServiceCollection AddBazaarDeviceEnrolmentPostgres(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IEnrolmentStore>(sp => new PostgresEnrolmentStore(sp.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }
}
