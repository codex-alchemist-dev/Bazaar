using Bazaar.Server.Security.Approvals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Security;

/// <summary>Registration for the owner approval enqueue port (doc 15, enqueue entry point).</summary>
public static class OwnerApprovalEnqueueServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IOwnerApprovalEnqueuer"/> over the host's <see cref="IApprovalStore"/>. This registers no store:
    /// resolving the enqueuer without one throws, so a host that has not wired approvals fails closed. Uses
    /// <see cref="ApprovalOptions"/> when registered, else the defaults. TryAdd, so a host registration wins.
    /// </summary>
    public static IServiceCollection AddOwnerApprovalEnqueue(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IOwnerApprovalEnqueuer>(sp => new OwnerApprovalEnqueuer(
            sp.GetRequiredService<IApprovalStore>(),
            sp.GetService<ApprovalOptions>(),
            sp.GetService<TimeProvider>()));
        return services;
    }
}
