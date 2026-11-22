namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// Receives one record per kill-switch engage or resume (doc 15, "Audit log and kill switch"). The host adapts this to the
/// audit chain (<c>IAuditLog</c>); the engine does not write the chain itself.
/// </summary>
public interface IKillSwitchAuditSink
{
    /// <summary>Records one engage or resume. Completes when the record is durable.</summary>
    Task RecordAsync(KillSwitchAuditEntry entry, CancellationToken ct = default);
}

/// <summary>Whether a kill-switch record engages or resumes a stop.</summary>
public enum KillSwitchAction
{
    /// <summary>A stop was engaged.</summary>
    Engaged,

    /// <summary>A stop was resumed.</summary>
    Resumed,
}

/// <summary>One kill-switch audit record.</summary>
/// <param name="Ts">When the action happened, from the engine's <see cref="TimeProvider"/>.</param>
/// <param name="ActorId">The authorized actor, or the breach actor for an automatic stop.</param>
/// <param name="Action">Engaged or resumed.</param>
/// <param name="Scope">Global or agent.</param>
/// <param name="AgentId">The agent for an agent stop. Null for a global stop.</param>
/// <param name="Reason">Why the action was taken.</param>
public sealed record KillSwitchAuditEntry(
    DateTimeOffset Ts,
    string ActorId,
    KillSwitchAction Action,
    KillSwitchScope Scope,
    string? AgentId,
    string Reason);
