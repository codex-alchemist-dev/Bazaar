using System.Text.Json;
using Bazaar.Contracts.Engines;

namespace Bazaar.Server.EngineDispatch;

/// <summary>One agent tool call to authorise (doc 20, WP4).</summary>
/// <param name="AgentId">The requesting agent.</param>
/// <param name="AdapterId">The engine adapter, for example <c>roblox</c>.</param>
/// <param name="GrantId">The grant reference the call carries, or null.</param>
/// <param name="StudioId">The target project or Studio.</param>
/// <param name="Tool">The tool name.</param>
/// <param name="Args">The tool arguments (JSON). Only their hash is audited.</param>
public sealed record EngineDispatchRequest(Guid AgentId, string AdapterId, string? GrantId, string StudioId, string Tool, JsonElement Args)
{
    /// <summary>The calling session, for the taint check.</summary>
    public Guid? SessionId { get; init; }

    /// <summary>
    /// True only when the caller verified an owner-signed approval bound to this tool and args hash (doc 15 approval flow).
    /// The dispatcher never validates signatures itself.
    /// </summary>
    public bool OwnerApprovalVerified { get; init; }
}

/// <summary>Why a call was allowed or denied.</summary>
public enum EngineDispatchReason
{
    Allowed,
    UnknownTool,
    GrantMissing,
    GrantNotCovering,
    HardTaintNeedsApproval,
    OwnerApprovalRequired,
    HostNotAllowlisted,
    ReaderFailure,
    AuditFailure,
    RecorderFailure,
}

/// <summary>The outcome of <c>AuthorizeAsync</c>.</summary>
public sealed record EngineDispatchDecision(bool Allowed, EngineDispatchReason Reason, RiskClass? RiskClass = null)
{
    public static EngineDispatchDecision Allow(RiskClass riskClass) => new(true, EngineDispatchReason.Allowed, riskClass);

    public static EngineDispatchDecision Deny(EngineDispatchReason reason, RiskClass? riskClass = null) => new(false, reason, riskClass);
}

/// <summary>Dispatch settings (doc 20, settings table). Settings may tighten the floors, never loosen them.</summary>
public sealed class EngineDispatchOptions
{
    /// <summary><c>engine.exec.http_allowlist</c>: exact host names. Empty denies every host.</summary>
    public IReadOnlyList<string> HttpAllowlist { get; init; } = [];

    /// <summary><c>engine.roblox.exec_owner_approval</c>: exec calls on the roblox adapter need a verified owner approval. Ignored (treated as true) while the session is hard-tainted.</summary>
    public bool RobloxExecOwnerApproval { get; init; } = true;

    /// <summary>Longest wait on a grant or taint read before the call is denied.</summary>
    public TimeSpan ReaderTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
