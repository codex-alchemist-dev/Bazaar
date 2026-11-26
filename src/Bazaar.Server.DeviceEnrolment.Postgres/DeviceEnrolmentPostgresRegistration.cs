using Bazaar.Server.Biometric;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.DeviceEnrolment.Postgres;

public static class DeviceEnrolmentPostgresRegistration
{
    /// <summary>
    /// Registers the Postgres stores. Needs an <c>NpgsqlDataSource</c> and an <c>ITransactionalAuditLog</c> in the container; both
    /// are supplied by the host, so a missing one fails at first resolve instead of falling back to a weaker store.
    /// </summary>
    public static IServiceCollection AddBazaarDeviceEnrolmentPostgres(this IServiceCollection services)
    {
        services.TryAddSingleton<IEnrolmentStore, PostgresEnrolmentStore>();
        services.TryAddSingleton<IBiometricCredentialStore, PostgresBiometricCredentialStore>();
        return services;
    }
}
