using Bazaar.Server.Security.Approvals;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Security;

/// <summary>Registration for the approvals read port of the web admin.</summary>
public static class SecurityAdminServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAdminApprovalSource"/> over <see cref="IPendingApprovalLister"/>, which resolves to the registered
    /// <see cref="IApprovalStore"/> when that store can list. TryAdd. A host with a store that cannot list leaves the section "unavailable".
    /// </summary>
    public static IServiceCollection AddBazaarAdminApprovalSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IPendingApprovalLister>(sp =>
            sp.GetRequiredService<IApprovalStore>() as IPendingApprovalLister
            ?? throw new InvalidOperationException("The registered approval store cannot list pending approvals."));
        services.TryAddSingleton<IAdminApprovalSource>(sp => new SecurityAdminApprovalSource(
            sp.GetRequiredService<IPendingApprovalLister>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));
        return services;
    }
}
