namespace Bazaar.Server.Subagents;

/// <summary>How a subagent run ended.</summary>
public enum SubagentStatus
{
    /// <summary>The child finished its task.</summary>
    Completed,

    /// <summary>The child or its runner failed.</summary>
    Failed,

    /// <summary>The run was cancelled.</summary>
    Cancelled,
}

/// <summary>Resources a child used.</summary>
/// <param name="InputTokens">Prompt tokens.</param>
/// <param name="OutputTokens">Completion tokens.</param>
/// <param name="Steps">Turn-loop steps.</param>
public sealed record SubagentUsage(long InputTokens, long OutputTokens, int Steps)
{
    /// <summary>No usage.</summary>
    public static SubagentUsage None { get; } = new(0, 0, 0);
}

/// <summary>What a child returns. The summary is data, never instructions, and carries no merge or approval authority.</summary>
/// <param name="Status">How the run ended.</param>
/// <param name="Summary">The child's summary, as untrusted text.</param>
/// <param name="Usage">Resources used.</param>
/// <param name="TraceId">Id of the full trace stored for the parent and reviewers (doc 06).</param>
public sealed record SubagentResult(SubagentStatus Status, UntrustedText Summary, SubagentUsage Usage, string? TraceId = null);
