using Bazaar.Server.Mail.Quarantine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Mail;

/// <summary>Registers the inbound screening service and its protected-domain source.</summary>
public static class MailScreeningServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="MailScreeningService"/>, <see cref="MailScreeningOptions"/> (defaults) and a Postgres-backed
    /// <see cref="IProtectedMailDomains"/> over the shared <see cref="NpgsqlDataSource"/>. Every registration is skipped when the
    /// host already registered that service, so a host or test can substitute its own.
    /// </summary>
    public static IServiceCollection AddBazaarMailScreening(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(new MailScreeningOptions());
        services.TryAddSingleton<IProtectedMailDomains>(sp => new PostgresProtectedMailDomains(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<MailScreeningService>();
        return services;
    }
}
