using Bazaar.Server.Security.KillSwitch;

namespace Bazaar.Server.AgentSafety.Drill;

/// <summary>
/// The synthetic agent a drill runs against, and the owner-device key the drill releases it with. The host implements it, and the
/// real target is wired later. <see cref="IsDrillAgent"/> is the only check that lets the runner stop an agent.
/// </summary>
public interface IDrillTarget
{
    /// <summary>The drill agent id the scheduler runs against. <see cref="IsDrillAgent"/> must return true for it.</summary>
    string DrillAgentId { get; }

    /// <summary>The drill device id, recorded as the release actor. The host registers it for drills only.</summary>
    string DrillDeviceId { get; }

    /// <summary>True only for drill agent ids. The runner refuses every other agent id.</summary>
    bool IsDrillAgent(string agentId);

    /// <summary>
    /// Signs a release challenge with the drill device key. Returns null when the drill device cannot sign, which fails the release
    /// step. The signature is checked by the host's containment, not here.
    /// </summary>
    Task<ContainmentReleaseRequest?> SignReleaseAsync(string agentId, string deviceId, ReleaseChallenge challenge, CancellationToken ct = default);
}
