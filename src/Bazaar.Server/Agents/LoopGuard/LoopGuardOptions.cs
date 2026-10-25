namespace Bazaar.Server.Agents.LoopGuard;

/// <summary>
/// The guard limits from doc 06 (settings <c>runtime.guard.*</c>) and the Skimmer scored cap from doc 13
/// (<c>skimmer.scored_wake_cap_per_hour</c>). Each limit is the largest count allowed: a counter that goes above it trips.
/// Loosening a limit needs owner approval (doc 06 settings table); this type only holds and validates the values.
/// </summary>
public sealed class LoopGuardOptions
{
    /// <summary><c>runtime.guard.pingpong_limit</c>: largest alternating run of messages between two agents.</summary>
    public int PingPongLimit { get; init; } = 6;

    /// <summary><c>runtime.guard.mention_depth</c>: deepest nested @mention chain.</summary>
    public int MentionDepth { get; init; } = 4;

    /// <summary><c>runtime.guard.wakes_per_hour</c>: wakes per agent in a sliding hour, all reasons included.</summary>
    public int WakesPerHour { get; init; } = 30;

    /// <summary><c>runtime.guard.thread_agent_messages</c>: agent messages in a GC since the last human message.</summary>
    public int ThreadAgentMessages { get; init; } = 20;

    /// <summary><c>runtime.guard.repeat_tool_call</c>: most identical tool calls in a row by one agent.</summary>
    public int RepeatToolCall { get; init; } = 3;

    /// <summary>
    /// <c>skimmer.scored_wake_cap_per_hour</c> (doc 13): scored wakes per agent per hour. Not a trip; a scored wake past
    /// the cap is not made. Must not exceed <see cref="WakesPerHour"/> (owner, doc 13).
    /// </summary>
    public int ScoredWakesPerHour { get; init; } = 20;

    /// <summary>Checks that every limit is positive and that the scored cap does not exceed the wake total.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit is below 1.</exception>
    /// <exception cref="ArgumentException">The scored cap is greater than the wake total.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(PingPongLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MentionDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(WakesPerHour, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ThreadAgentMessages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(RepeatToolCall, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ScoredWakesPerHour, 1);

        if (ScoredWakesPerHour > WakesPerHour)
        {
            throw new ArgumentException(
                $"The scored wake cap ({ScoredWakesPerHour}) must not exceed the wake total ({WakesPerHour}) (doc 13).",
                nameof(ScoredWakesPerHour));
        }
    }
}
