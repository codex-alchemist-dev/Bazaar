using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Taint;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.VmEgress.Approvals.Security;

/// <summary>
/// Composition for the approvals-service adapters of the egress gate. The host registers <c>ApprovalService</c>, <c>IApprovalStore</c>
/// and <see cref="IAgentSessionTaintReader"/>; a missing one fails at call time and the gate denies.
/// </summary>
public static class EgressApprovalsSecurityModule
{
    /// <summary>
    /// Replaces the unavailable port and taint-reader defaults with the approvals-service adapters. Order-independent with
    /// <c>AddBazaarVmEgressApprovals</c>, which only adds its defaults when none is registered.
    /// </summary>
    public static IServiceCollection AddBazaarVmEgressApprovalsSecurity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.Replace(ServiceDescriptor.Singleton<IEgressApprovalPort>(sp => new ApprovalServiceEgressApprovalPort(
            () => sp.GetRequiredService<ApprovalService>(),
            () => sp.GetRequiredService<IApprovalStore>(),
            sp.GetRequiredService<TimeProvider>())));
        services.Replace(ServiceDescriptor.Singleton<IEgressTaintReader>(sp =>
            new AgentTaintReader(sp.GetService<IAgentSessionTaintReader>())));
        return services;
    }
}
