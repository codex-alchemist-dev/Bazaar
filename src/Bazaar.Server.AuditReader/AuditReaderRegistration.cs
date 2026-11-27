using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.AuditReader;

public static class AuditReaderRegistration
{
    /// <summary>Registers <see cref="IAdminAuditReader"/> over the shared <see cref="NpgsqlDataSource"/>.</summary>
    public static IServiceCollection AddBazaarAuditReader(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAuditRowSource>(sp => new PostgresAuditRowSource(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IAdminAuditReader>(sp => new AdminAuditReader(sp.GetRequiredService<IAuditRowSource>()));
        return services;
    }
}
