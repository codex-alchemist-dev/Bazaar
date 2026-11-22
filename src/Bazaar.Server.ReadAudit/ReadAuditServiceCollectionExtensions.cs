using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;


namespace Bazaar.Server.ReadAudit;

/// <summary>Registration for the read auditor. The caller must register <see cref="IAuditLog"/>.</summary>
public static class ReadAuditServiceCollectionExtensions
{
    /// <summary>Adds <see cref="ReadAuditor"/> as a singleton. Uses <see cref="TimeProvider.System"/> unless one is registered.</summary>
    public static IServiceCollection AddBazaarReadAudit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ReadAuditor>();
        return services;
    }
}
