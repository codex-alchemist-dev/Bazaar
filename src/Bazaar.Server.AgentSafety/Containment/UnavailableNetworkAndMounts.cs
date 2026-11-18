using System.Net;
using Bazaar.Server.VmEgress;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.AgentSafety.Containment;

/// <summary>
/// <see cref="IEgressPolicy"/> for a host with no VmEgress module composed. Block and release throw, so containment never claims
/// egress was blocked when nothing was, and the stop stays in force.
/// </summary>
public sealed class UnavailableEgressPolicy : IEgressPolicy
{
    private const string Message = "No egress policy is composed, so the agent's egress cannot be blocked or released.";

    /// <inheritdoc />
    public Task<EgressApplyResult> ApplyAsync(string agentId, EgressRules rules, CancellationToken ct = default) => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task<EgressApplyResult> InitialiseAsync(string agentId, IPAddress proxyAddress, int proxyPort, CancellationToken ct = default) => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task BlockAllAsync(string agentId, CancellationToken ct = default) => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task<EgressApplyResult> ReleaseBlockAsync(string agentId, CancellationToken ct = default) => throw new InvalidOperationException(Message);
}

/// <summary>
/// <see cref="IMountManager"/> for a host with no VmMounts module composed. Every call throws, so containment never claims mounts
/// were detached when they were not.
/// </summary>
public sealed class UnavailableMountManager : IMountManager
{
    private const string Message = "No mount manager is composed, so the agent's mounts cannot be detached.";

    /// <inheritdoc />
    public Task<AttachedMount> AttachAsync(MountSpec spec, CancellationToken ct) => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task<DetachResult> DetachAsync(Guid mountId, DetachReason reason, CancellationToken ct) => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task<IReadOnlyList<DetachResult>> DetachAllForAgentAsync(Guid agentId, DetachReason reason, CancellationToken ct) => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task<WriteBackResult> WriteBackProfileAsync(Guid mountId, CancellationToken ct) => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task<bool> ReportCanaryTokenUseAsync(string tokenValue, string source, CancellationToken ct) => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task<int> RearmWatchesAsync(CancellationToken ct) => throw new InvalidOperationException(Message);
}

/// <summary><see cref="IGuestScrubber"/> for a host with no Vms lifecycle composed. It refuses, so a detach reports the guest as not scrubbed.</summary>
public sealed class UnavailableGuestScrubber : IGuestScrubber
{
    /// <inheritdoc />
    public Task ScrubAsync(GuestScrubRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("No VM lifecycle is composed, so the guest cannot be scrubbed.");
}
