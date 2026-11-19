using Bazaar.Server.Agents.LoopGuard;

namespace Bazaar.Server.Agents.Turns.Posting;

/// <summary>
/// Thrown by <see cref="GcAgentPostSink"/> when a loop guard trips on an agent post. Nothing was appended to the GC when it is
/// thrown, so the turn runner ends the turn as refused and releases the turn's inbox items.
/// </summary>
public sealed class AgentPostRefusedException(GuardKind guardKind, string reason) : Exception(reason)
{
    /// <summary>The guard that tripped.</summary>
    public GuardKind GuardKind { get; } = guardKind;

    /// <summary>The guard's reason, as the loop guard wrote it.</summary>
    public string Reason { get; } = reason;
}
