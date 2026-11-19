using System.Collections.Immutable;

namespace Bazaar.Server.Agents.LoopGuard;

/// <summary>
/// The loop-guard facade. Thread-keyed counters (ping-pong, agent messages since the last human message) are kept per GC;
/// agent-keyed counters (wakes per hour, repeated tool calls) are kept per agent. In-memory and pure logic: no database,
/// no I/O. Time comes from the injected <see cref="TimeProvider"/>. Thread-safe through one lock.
/// </summary>
public sealed class LoopGuardFacade : ILoopGuard
{
    private readonly LoopGuardOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, ThreadStats> _threads = [];
    private readonly Dictionary<string, AgentCounters> _agents = new(StringComparer.Ordinal);

    /// <summary>Creates the facade. Options are validated once here.</summary>
    /// <param name="time">The clock for wake windows.</param>
    /// <param name="options">The limits; null uses the doc 06 and doc 13 defaults.</param>
    public LoopGuardFacade(TimeProvider time, LoopGuardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(time);

        _time = time;
        _options = options ?? new LoopGuardOptions();
        _options.Validate();
    }

    /// <inheritdoc />
    public GuardVerdict Check(LoopEvent loopEvent)
    {
        ArgumentNullException.ThrowIfNull(loopEvent);

        lock (_gate)
        {
            return loopEvent switch
            {
                HumanMessage human => ResetThread(human.GcId),
                ToolResult result => ClearPingPong(result.GcId),
                AgentMessage message => OnAgentMessage(message),
                ToolCall call => OnToolCall(call),
                AgentWake wake => OnWake(wake),
                _ => throw new ArgumentOutOfRangeException(nameof(loopEvent), loopEvent, "Unknown loop event."),
            };
        }
    }

    /// <inheritdoc />
    public bool ScoredWakeWithinCap(string agentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        lock (_gate)
        {
            return AgentOf(agentId).ScoredWakesInWindow(_time.GetUtcNow()) < _options.ScoredWakesPerHour;
        }
    }

    private GuardVerdict ResetThread(Guid gcId)
    {
        _threads.Remove(gcId);
        return GuardVerdict.Ok;
    }

    private GuardVerdict ClearPingPong(Guid gcId)
    {
        _threads[gcId] = ThreadOf(gcId) with { PingPong = PingPongRun.None };
        return GuardVerdict.Ok;
    }

    private GuardVerdict OnAgentMessage(AgentMessage message)
    {
        ArgumentException.ThrowIfNullOrEmpty(message.AgentId);
        ArgumentOutOfRangeException.ThrowIfNegative(message.MentionDepth);

        var before = ThreadOf(message.GcId);
        var pingPong = before.PingPong.Next(message.AgentId);
        var agents = before.AgentsSinceHuman.Add(message.AgentId);
        var count = before.AgentMessagesSinceHuman + 1;
        _threads[message.GcId] = new ThreadStats(count, agents, pingPong);

        if (message.MentionDepth > _options.MentionDepth)
        {
            return GuardVerdict.Trip(
                GuardKind.MentionDepth,
                $"Mention chain depth {message.MentionDepth} exceeds the limit of {_options.MentionDepth}.",
                [message.AgentId]);
        }

        if (pingPong.Length > _options.PingPongLimit && pingPong.Other is { } other)
        {
            return GuardVerdict.Trip(
                GuardKind.PingPong,
                $"'{pingPong.Last}' and '{other}' alternated {pingPong.Length} messages with no human message or tool result (limit {_options.PingPongLimit}).",
                [pingPong.Last!, other]);
        }

        if (count > _options.ThreadAgentMessages)
        {
            return GuardVerdict.Trip(
                GuardKind.ThreadAgentMessages,
                $"{count} agent messages in this thread without a human message (limit {_options.ThreadAgentMessages}).",
                agents);
        }

        return GuardVerdict.Ok;
    }

    private GuardVerdict OnWake(AgentWake wake)
    {
        ArgumentException.ThrowIfNullOrEmpty(wake.AgentId);

        var now = _time.GetUtcNow();
        var counters = AgentOf(wake.AgentId);
        counters.RecordWake(now, wake.Kind);

        var wakes = counters.WakesInWindow(now);
        if (wakes > _options.WakesPerHour)
        {
            return GuardVerdict.Trip(
                GuardKind.WakesPerHour,
                $"{wakes} wakes in the last hour (limit {_options.WakesPerHour}).",
                [wake.AgentId]);
        }

        return GuardVerdict.Ok;
    }

    private GuardVerdict OnToolCall(ToolCall call)
    {
        ArgumentException.ThrowIfNullOrEmpty(call.AgentId);
        ArgumentException.ThrowIfNullOrEmpty(call.Fingerprint);

        var repeats = AgentOf(call.AgentId).RecordToolCall(call.Fingerprint);
        if (repeats > _options.RepeatToolCall)
        {
            return GuardVerdict.Trip(
                GuardKind.RepeatToolCall,
                $"The same tool call repeated {repeats} times in a row (limit {_options.RepeatToolCall}).",
                [call.AgentId]);
        }

        return GuardVerdict.Ok;
    }

    private ThreadStats ThreadOf(Guid gcId) =>
        _threads.TryGetValue(gcId, out var stats) ? stats : ThreadStats.Empty;

    private AgentCounters AgentOf(string agentId)
    {
        if (!_agents.TryGetValue(agentId, out var counters))
        {
            counters = new AgentCounters();
            _agents[agentId] = counters;
        }

        return counters;
    }
}
