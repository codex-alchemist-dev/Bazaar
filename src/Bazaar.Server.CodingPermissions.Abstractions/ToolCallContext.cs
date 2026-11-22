using System.Text.Json;

namespace Bazaar.Server.CodingPermissions;

/// <summary>Everything the permission gate needs to judge one tool call. Arguments are traced as a hash only; the raw <paramref name="Args"/> exist so the generic policy can hash them itself and are never written to the trace.</summary>
/// <param name="AgentId">The agent that owns the session. Rate caps and freezes are per agent.</param>
/// <param name="SessionId">The coding session.</param>
/// <param name="Tool">The tool name.</param>
/// <param name="CapabilityClass">The class the tool is registered with.</param>
/// <param name="ArgsSha256">Lowercase hex SHA-256 of the canonical arguments. Per-action approvals bind to it.</param>
/// <param name="SourceTrust">The doc 15 source trust label of the content that caused the call.</param>
/// <param name="Taint">The session taint as the caller sees it; the gate also asks <see cref="ISessionTaintReader"/> and uses the stricter value.</param>
/// <param name="TargetPath">The path the call touches, when it has one.</param>
/// <param name="TargetBranch">The branch a push or merge targets, when it has one.</param>
/// <param name="Args">The call's arguments. Server's generic policy hashes them in canonical form; the default (undefined) value is refused there, so a caller that omits it fails closed.</param>
/// <param name="Role">The session role. Defaults to <see cref="ToolSessionRole.Reader"/>, the narrower role, so an omitted value fails closed.</param>
/// <param name="Grant">A verified per-action approval for this exact call, when one exists.</param>
public sealed record ToolCallContext(
    string AgentId,
    string SessionId,
    string Tool,
    ToolCapabilityClass CapabilityClass,
    string ArgsSha256,
    string SourceTrust,
    SessionTaint Taint,
    string? TargetPath = null,
    string? TargetBranch = null,
    JsonElement Args = default,
    ToolSessionRole Role = ToolSessionRole.Reader,
    ToolApprovalGrant? Grant = null);

/// <summary>
/// The session role of the caller (doc 15, "Reader and actor sessions"). Mirrors Server's <c>SessionRoles</c> single-role
/// members; a test in the host tests project checks the match.
/// </summary>
public enum ToolSessionRole
{
    /// <summary>Reads untrusted input and holds no outward tools.</summary>
    Reader,

    /// <summary>Holds credentials and acts.</summary>
    Actor,
}

/// <summary>An owner approval already verified against its stored request by the approval service (doc 15).</summary>
/// <param name="ApprovalId">Id of the approval request.</param>
/// <param name="ArgsHash">The dispatch hash the owner saw. The call must hash to the same value.</param>
/// <param name="ExpiresAt">The approval is void at or after this time.</param>
public sealed record ToolApprovalGrant(Guid ApprovalId, byte[] ArgsHash, DateTimeOffset ExpiresAt);
