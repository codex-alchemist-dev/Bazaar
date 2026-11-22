namespace Bazaar.Server.VmEgress.Enforcers;

/// <summary>Where an agent's guests attach to the host network, as the host knows it.</summary>
/// <param name="VmNames">Hyper-V VM names whose network adapters carry the agent's extended ACLs. Each must be a plain name (letters, digits, dot, dash, underscore).</param>
/// <param name="GuestInterface">Linux: the bridge port (tap or veth) name of the agent's guest. One interface per agent; null on Windows.</param>
public sealed record EgressTarget(IReadOnlyList<string> VmNames, string? GuestInterface = null);

/// <summary>
/// Port supplied by the host: maps an agent to its VM names and bridge port. The VM lifecycle owns that mapping, so this module
/// does not guess it. Without a registered resolver the enforcer stays the fail-closed <c>UnavailableEgressEnforcer</c>.
/// </summary>
public interface IEgressTargetResolver
{
    /// <summary>Resolves the agent's target. Throws when the agent has no guest, so egress is never reported as enforced.</summary>
    Task<EgressTarget> ResolveAsync(string agentId, CancellationToken ct = default);
}

/// <summary>A host command could not be run, failed, produced too much output, or its result did not match the rules.</summary>
public sealed class EgressEnforcementException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public EgressEnforcementException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the cause.</summary>
    public EgressEnforcementException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
