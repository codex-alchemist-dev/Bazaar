namespace Bazaar.Server.Agents.Turns.Subagents;

/// <summary>Stable reason codes for a rejected subagent request. The codes appear in logs and must not be renamed.</summary>
public static class SubagentRejectionCodes
{
    /// <summary>The child's depth would exceed <see cref="SubagentOptions.MaxDepth"/>.</summary>
    public const string DepthLimit = "subagent_depth_limit";

    /// <summary>The child asks for a tool the parent does not hold.</summary>
    public const string ToolNotInParent = "subagent_tool_not_in_parent";

    /// <summary>The parent turn already has <see cref="SubagentOptions.MaxConcurrent"/> children running.</summary>
    public const string ConcurrencyLimit = "subagent_concurrency_limit";
}

/// <summary>A subagent request broke a limit. Nothing ran; the child turn was never started.</summary>
public sealed class SubagentRejectedException(string reasonCode, string message) : Exception(message)
{
    /// <summary>The stable code from <see cref="SubagentRejectionCodes"/>.</summary>
    public string ReasonCode { get; } = reasonCode;
}
