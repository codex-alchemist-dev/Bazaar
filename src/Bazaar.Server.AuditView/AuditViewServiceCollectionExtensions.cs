using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.AuditView;

public static class AuditViewServiceCollectionExtensions
{
    /// <summary>
    /// Adds the scope resolver and the viewer. The host registers <c>IAuditViewSource</c>, <c>IAuditLog</c> and
    /// <c>IPermissionChecker</c>. Without a source the viewer cannot be built, so nothing is shown.
    /// </summary>
    public static IServiceCollection AddBazaarAuditView(this IServiceCollection services, AuditViewOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var o = options ?? new AuditViewOptions();
        o.Validate();
        services.TryAddSingleton(o);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAuditViewScopeResolver, AuditViewScopeResolver>();
        services.TryAddSingleton<IAuditViewService, AuditViewService>();
        return services;
    }
}
