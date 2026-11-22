using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.VmEgress.Approvals.Security;

/// <summary>
/// Port to the agent's current session taint. The agent to session mapping lives in <c>Bazaar.Server</c>, which modules may not
/// reference, so the host supplies this. Failures throw; the egress gate turns a throw into a denial.
/// </summary>
public interface IAgentSessionTaintLookup
{
    /// <summary>The taint of the agent's current session. A session with no untrusted input is <see cref="SessionTaint.Clean"/>.</summary>
    ValueTask<SessionTaint> GetAgentTaintAsync(string agentId, CancellationToken ct);
}
