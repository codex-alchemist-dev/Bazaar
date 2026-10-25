using System.Collections.Immutable;

namespace Bazaar.Server.Agents.LoopGuard;

/// <summary>
/// Thread-keyed counters for one GC. Immutable: the facade replaces the value on each event. Ping-pong is tracked
/// per alternating pair; a run holds at most one pair at a time, so the pair is the run's last two speakers.
/// </summary>
/// <param name="AgentMessagesSinceHuman">Agent messages since the last human message in this GC.</param>
/// <param name="AgentsSinceHuman">Agents that posted since the last human message (for the trip's agent list).</param>
/// <param name="PingPong">The current alternation run.</param>
public sealed record ThreadStats(int AgentMessagesSinceHuman, ImmutableHashSet<string> AgentsSinceHuman, PingPongRun PingPong)
{
    /// <summary>A thread with no messages since the last human message.</summary>
    public static ThreadStats Empty { get; } = new(0, ImmutableHashSet<string>.Empty, PingPongRun.None);
}

/// <summary>
/// The current two-agent alternation in a thread. <see cref="Length"/> counts agent messages in the run; zero means no run.
/// </summary>
/// <param name="Last">The agent who posted most recently in the run.</param>
/// <param name="Other">The agent who posted before <paramref name="Last"/>; null until the second speaker arrives.</param>
/// <param name="Length">Messages in the run.</param>
public sealed record PingPongRun(string? Last, string? Other, int Length)
{
    /// <summary>No run.</summary>
    public static PingPongRun None { get; } = new(null, null, 0);

    /// <summary>
    /// Advances the run for one agent message. The same agent twice in a row, or a third agent, starts a new run
    /// of length 1 (the alternation is broken).
    /// </summary>
    public PingPongRun Next(string agentId)
    {
        if (Length == 0 || agentId == Last || (Other is not null && agentId != Other))
        {
            return new(agentId, null, 1);
        }

        return new(agentId, Last, Length + 1);
    }
}
