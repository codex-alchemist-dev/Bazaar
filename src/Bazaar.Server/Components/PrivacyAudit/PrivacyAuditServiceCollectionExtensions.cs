using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Components.PrivacyAudit;

/// <summary>Registration for the privacy audit recorder. The caller must register <see cref="Bazaar.Server.Security.Audit.IAuditLog"/>.</summary>
public static class PrivacyAuditServiceCollectionExtensions
{
    // TODO(component): register from host composition when the component model (PR #125) merges.
    /// <summary>Adds <see cref="PrivacyAuditRecorder"/> as a singleton. Uses <see cref="TimeProvider.System"/> unless one is registered.</summary>
    public static IServiceCollection AddBazaarPrivacyAudit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PrivacyAuditRecorder>();
        return services;
    }
}
