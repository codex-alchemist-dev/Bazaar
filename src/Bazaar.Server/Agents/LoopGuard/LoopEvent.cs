namespace Bazaar.Server.Agents.LoopGuard;

/// <summary>
/// What the runtime reports to the guard. Thread events carry a GC id (thread-keyed counters); agent events carry an
/// agent id (agent-keyed counters). The guard does no I/O: the caller maps GC messages into these events.
/// </summary>
public abstract record LoopEvent;

/// <summary>A human message (the owner or a co-member) in a GC. Resets every thread counter for that GC.</summary>
public sealed record HumanMessage(Guid GcId) : LoopEvent;

/// <summary>
/// An agent message in a GC. <see cref="MentionDepth"/> is the caller's count of nested @mentions behind this message,
/// since <c>GcMessage</c> has no parent field.
/// </summary>
public sealed record AgentMessage(Guid GcId, string AgentId, int MentionDepth) : LoopEvent;

/// <summary>A tool result in a GC. Resets the ping-pong run for that GC; it does not reset the agent-message count.</summary>
public sealed record ToolResult(Guid GcId) : LoopEvent;

/// <summary>An agent is about to make a tool call. <see cref="Fingerprint"/> comes from <see cref="ToolCallFingerprint"/>.</summary>
public sealed record ToolCall(string AgentId, string Fingerprint) : LoopEvent;

/// <summary>An agent is woken for any reason. Scored wakes (doc 13) count toward the same hourly total.</summary>
public sealed record AgentWake(string AgentId, WakeKind Kind) : LoopEvent;

/// <summary>Whether a wake came from the ordinary wake reasons or from a Skimmer score (doc 13).</summary>
public enum WakeKind
{
    /// <summary>Mention, DM, digest or any other non-scored wake reason.</summary>
    Standard,

    /// <summary>A wake the Skimmer Bouncer promoted from a score (doc 13; counts toward the scored cap too).</summary>
    SkimmerScored,
}
