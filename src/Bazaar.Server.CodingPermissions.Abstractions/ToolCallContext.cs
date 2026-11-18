namespace Bazaar.Server.CodingPermissions;

/// <summary>Everything the permission gate needs to judge one tool call. Arguments are carried as a hash only.</summary>
/// <param name="AgentId">The agent that owns the session. Rate caps and freezes are per agent.</param>
/// <param name="SessionId">The coding session.</param>
/// <param name="Tool">The tool name.</param>
/// <param name="CapabilityClass">The class the tool is registered with.</param>
/// <param name="ArgsSha256">Lowercase hex SHA-256 of the canonical arguments. Per-action approvals bind to it.</param>
/// <param name="SourceTrust">The doc 15 source trust label of the content that caused the call.</param>
/// <param name="Taint">The session taint as the caller sees it; the gate also asks <see cref="ISessionTaintReader"/> and uses the stricter value.</param>
/// <param name="TargetPath">The path the call touches, when it has one.</param>
/// <param name="TargetBranch">The branch a push or merge targets, when it has one.</param>
public sealed record ToolCallContext(
    string AgentId,
    string SessionId,
    string Tool,
    ToolCapabilityClass CapabilityClass,
    string ArgsSha256,
    string SourceTrust,
    SessionTaint Taint,
    string? TargetPath = null,
    string? TargetBranch = null);
