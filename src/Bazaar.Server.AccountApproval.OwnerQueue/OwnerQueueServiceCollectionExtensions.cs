using Bazaar.Server.AccountApproval.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.AccountApproval.OwnerQueue;

/// <summary>Registration for the account approval owner queue adapter.</summary>
public static class OwnerQueueServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="OwnerQueueApprovalHandOff"/> as <see cref="IOwnerApprovalHandOff"/>. It needs an
    /// <c>IOwnerApprovalEnqueuer</c> from the host (<c>AddOwnerApprovalEnqueue</c> in Bazaar.Server.Security, over an approval
    /// store); without one, resolving the hand-off throws and the request is not reported as handed off. TryAdd, so a host
    /// registration wins.
    /// </summary>
    public static IServiceCollection AddBazaarAccountApprovalOwnerQueue(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IOwnerApprovalHandOff, OwnerQueueApprovalHandOff>();
        return services;
    }
}
