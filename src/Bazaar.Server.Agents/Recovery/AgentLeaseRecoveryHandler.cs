using Bazaar.Server.Jobs;

namespace Bazaar.Server.Agents.Recovery;

/// <summary>Runs <see cref="AgentLeaseRecovery"/> as the <c>agent.lease_recovery</c> background job. Safe to rerun.</summary>
public sealed class AgentLeaseRecoveryHandler(AgentLeaseRecovery recovery) : IJobHandler
{
    /// <summary>The job kind served by this handler.</summary>
    public const string JobKind = "agent.lease_recovery";

    public string Kind => JobKind;

    public async Task HandleAsync(JobRecord job, CancellationToken ct) => await recovery.RecoverExpiredAsync(ct).ConfigureAwait(false);
}
