using Bazaar.Contracts.Engines;

namespace Bazaar.Server.EngineDispatch;

/// <summary>
/// Reads the server-owned grant (doc 20, Data and interfaces). <c>PostgresEngineGrantReader</c> in <c>Bazaar.Server.Engines</c>
/// is the Postgres adapter over <c>EngineGrantStore</c>; tests supply their own.
/// </summary>
public interface IEngineGrantReader
{
    /// <summary>True only when the grant is valid, belongs to the agent and covers the tool at the risk class. Failures throw; the dispatcher fails closed.</summary>
    ValueTask<bool> CoversAsync(Guid agentId, string adapterId, string toolName, RiskClass riskClass, string grantId, CancellationToken ct);
}

/// <summary>Whether the requesting session is hard-tainted (doc 15).</summary>
public interface ISessionTaintReader
{
    /// <summary>True when the session has seen external content (hard taint). Failures throw; the dispatcher fails closed.</summary>
    ValueTask<bool> IsHardTaintedAsync(Guid agentId, Guid? sessionId, CancellationToken ct);
}

/// <summary>
/// Records a write or exec call as a message in the requesting agent's GC (doc 20, Audit; doc 01). Narrow seam: the GC writer
/// needs a GC id and a sealed body, which this module must not know about.
/// </summary>
public interface IEngineGcRecorder
{
    /// <summary>Records the call. Throws on failure; the dispatcher then denies the call.</summary>
    Task RecordAsync(EngineToolCallRecord record, CancellationToken ct);
}

/// <summary>What the GC message states. Never carries argument content, only its hash.</summary>
public sealed record EngineToolCallRecord(
    Guid AgentId,
    string AdapterId,
    string Tool,
    RiskClass RiskClass,
    string? GrantId,
    bool Allowed,
    EngineDispatchReason Reason,
    string ArgsSha256);
