using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.ReadAudit.Npgsql;

/// <summary>Registration for the audited data source.</summary>
public static class ReadAuditNpgsqlServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="AuditedDbDataSource"/> as a singleton over the registered <see cref="NpgsqlDataSource"/>, and the read
    /// auditor it reports to. The raw <see cref="NpgsqlDataSource"/> registration is left as it is, so modules migrate one at a
    /// time; the caller must register <c>IAuditLog</c>.
    /// </summary>
    public static IServiceCollection AddBazaarAuditedDataSource(this IServiceCollection services, Action<AuditedDataSourceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddBazaarReadAudit();
        var options = new AuditedDataSourceOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<AuditedDbDataSource>(sp => new AuditedDbDataSource(
            sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<ReadAuditor>(), sp.GetRequiredService<AuditedDataSourceOptions>()));
        return services;
    }
}
