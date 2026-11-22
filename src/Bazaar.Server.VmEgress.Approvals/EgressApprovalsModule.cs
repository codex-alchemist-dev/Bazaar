using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.VmEgress.Approvals;

/// <summary>
/// Composition for the egress approval gate. The host registers <see cref="IAuditLog"/> and real <see cref="IEgressApprovalPort"/> and
/// <see cref="IEgressTaintReader"/> adapters first. Without them the port and taint defaults throw, and the gate turns that into a denial.
/// </summary>
public static class EgressApprovalsModule
{
    /// <summary>Replaces the egress module's deny-all gate with the approval-backed one. Call after <c>AddBazaarVmEgress</c>.</summary>
    public static IServiceCollection AddBazaarVmEgressApprovals(this IServiceCollection services, TimeSpan? portTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = portTimeout is { } t ? new EgressApprovalGateOptions { PortTimeout = t } : new EgressApprovalGateOptions();
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEgressApprovalPort, UnavailableEgressApprovalPort>();
        services.TryAddSingleton<IEgressTaintReader, UnavailableEgressTaintReader>();
        services.RemoveAll<IEgressApprovalGate>();
        services.AddSingleton<IEgressApprovalGate>(sp => new EgressApprovalGate(
            sp.GetRequiredService<IEgressApprovalPort>(),
            sp.GetRequiredService<IEgressTaintReader>(),
            sp.GetRequiredService<IAuditLog>(),
            sp.GetRequiredService<EgressApprovalGateOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}

internal sealed class UnavailableEgressApprovalPort : IEgressApprovalPort
{
    public Task<EgressApprovalTicket> RequestOrGetAsync(EgressApprovalRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("No approvals service is wired for egress growth.");

    public Task<bool> ConsumeAsync(Guid ticketId, CancellationToken ct) =>
        throw new InvalidOperationException("No approvals service is wired for egress growth.");
}

internal sealed class UnavailableEgressTaintReader : IEgressTaintReader
{
    public ValueTask<bool> IsHardTaintedAsync(string agentId, CancellationToken ct) =>
        throw new InvalidOperationException("No taint reader is wired for egress growth.");
}
