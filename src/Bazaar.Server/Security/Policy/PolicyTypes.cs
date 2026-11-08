using System.Text.Json;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Security.Policy;

/// <summary>What the dispatcher does with one tool call.</summary>
public enum PolicyOutcome
{
    /// <summary>Run the call now.</summary>
    Allow,

    /// <summary>Stop and ask the owner. The call runs only with a matching approval.</summary>
    RequireApproval,

    /// <summary>Refuse the call. Nothing runs.</summary>
    Deny,
}

/// <summary>Why the engine chose its outcome. Written to the audit trail and used by tests.</summary>
public enum PolicyReason
{
    /// <summary>The tool is not in the registry.</summary>
    UnknownTool,

    /// <summary>The session's role may not call this tool.</summary>
    RoleNotPermitted,

    /// <summary>The tool is at the Deny tier.</summary>
    TierDenied,

    /// <summary>The tool is at the Tier 3 level, which only a human may perform.</summary>
    HumanPerformsAction,

    /// <summary>Tier 0: runs without notice.</summary>
    Autonomous,

    /// <summary>Tier 1 in a clean or soft session: runs, and the caller posts the GC notice.</summary>
    NoticeRequired,

    /// <summary>A verified owner approval covers this exact call.</summary>
    ApprovedByGrant,

    /// <summary>Tier 2 in a session with hard taint. Only a per-action approval gets past this.</summary>
    HardTaintTier2,

    /// <summary>Tier 2 that the setting requires approval for, even in an untainted session.</summary>
    OwnerApprovalRequired,

    /// <summary>Tier 2 allowed without approval by the setting, in a session that is not hard-tainted.</summary>
    UntaintedTier2Allowed,

    /// <summary>The call's quota window is full.</summary>
    RateLimited,

    /// <summary>The arguments cannot be hashed in canonical form, for example because a property name repeats.</summary>
    MalformedArguments,
}

/// <summary>The engine's answer for one call.</summary>
/// <param name="Outcome">What to do.</param>
/// <param name="Reason">Why.</param>
public sealed record PolicyDecision(PolicyOutcome Outcome, PolicyReason Reason);

/// <summary>An owner approval that has already been checked against the stored request (see <c>ApprovalService</c>).</summary>
/// <param name="ApprovalId">Id of the approval request.</param>
/// <param name="AgentId">Agent the approval was issued to.</param>
/// <param name="Tool">Tool the approval covers.</param>
/// <param name="ArgsHash">Dispatch hash the owner saw; the call must hash to the same value.</param>
/// <param name="ExpiresAt">The approval is void at or after this time.</param>
public sealed record VerifiedApproval(Guid ApprovalId, string AgentId, string Tool, byte[] ArgsHash, DateTimeOffset ExpiresAt);

/// <summary>
/// Everything the engine needs to judge one call. The server builds it; the model never sets any of it.
/// </summary>
/// <param name="AgentId">Calling agent.</param>
/// <param name="SessionId">Session the call runs in (doc 06 session key).</param>
/// <param name="Role">Exactly one role: <see cref="SessionRoles.Reader"/> or <see cref="SessionRoles.Actor"/>.</param>
/// <param name="Tool">Tool name as the model called it.</param>
/// <param name="Platform">Platform for quota keys, such as a forge or mail service. Null for platform-free tools.</param>
/// <param name="Target">Target of the action, such as a repository or a recipient. Part of the dispatch hash.</param>
/// <param name="Args">The call's arguments. Hashed in canonical form.</param>
/// <param name="Taint">The session's taint now.</param>
/// <param name="Now">Time of the check. Passed in so decisions are deterministic.</param>
/// <param name="Grant">A verified approval for this call, if one exists.</param>
public sealed record DispatchContext(
    string AgentId,
    Guid SessionId,
    SessionRoles Role,
    string Tool,
    string? Platform,
    string? Target,
    JsonElement Args,
    SessionTaint Taint,
    DateTimeOffset Now,
    VerifiedApproval? Grant = null);

/// <summary>
/// The operator choices the engine reads (doc 15 settings). Defaults are the most protective documented values.
/// A setting may tighten a floor; this type has no way to switch off the hard-taint gate.
/// </summary>
/// <param name="Tier2WhenUntainted">
/// <c>Always</c> (default) asks for approval on every Tier 2 call. <c>HardTaintOnly</c> asks only when the session is
/// hard-tainted, as doc 15 allows.
/// </param>
public sealed record PolicyOptions(Tier2WhenUntainted Tier2WhenUntainted = Tier2WhenUntainted.Always)
{
    /// <summary>The shipped default.</summary>
    public static PolicyOptions Default { get; } = new();
}

/// <summary>The setting <c>security.approval.tier2_when_untainted</c>.</summary>
public enum Tier2WhenUntainted
{
    /// <summary>Every Tier 2 call needs approval, whatever the session's taint.</summary>
    Always,

    /// <summary>Tier 2 needs approval only when the session is hard-tainted.</summary>
    HardTaintOnly,
}
