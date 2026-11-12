using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Server.Agents.Scheduling;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Start;

namespace Bazaar.Server.Tests.Agents.Scheduling;

/// <summary>Returns a fixed list of due agents and records the limits it was asked for.</summary>
internal sealed class FakeDueAgentReader(params Guid[] agents) : IDueAgentReader
{
    public List<int> Limits { get; } = [];

    public Task<IReadOnlyList<DueAgent>> PeekDueAgentsAsync(int limit, CancellationToken ct)
    {
        Limits.Add(limit);
        IReadOnlyList<DueAgent> due = [.. agents.Take(limit).Select(id => new DueAgent(id, 1, DateTimeOffset.UnixEpoch))];
        return Task.FromResult(due);
    }
}

/// <summary>Gives every agent one due item, except the agents listed in <see cref="NothingDue"/>.</summary>
internal sealed class FakeDueInboxReader : IDueInboxReader
{
    public HashSet<Guid> NothingDue { get; } = [];

    public Task<IReadOnlyList<InboxRow>> PeekDueAsync(Guid agentId, int limit, CancellationToken ct)
    {
        IReadOnlyList<InboxRow> rows = NothingDue.Contains(agentId)
            ? []
            : [new InboxRow(Guid.CreateVersion7(), "mention", 1, null, null, null)];
        return Task.FromResult(rows);
    }
}

/// <summary>Records every request, tracks how many runs are in flight, and answers from <see cref="OnRun"/>.</summary>
internal sealed class FakeTurnRunner : ITurnRunner
{
    private readonly Lock _gate = new();
    private readonly List<TurnRunRequest> _requests = [];
    private int _inFlight;
    private int _maxInFlight;

    /// <summary>What a run does. The default ends with a final message at once.</summary>
    public Func<TurnRunRequest, CancellationToken, Task<TurnEndReason>> OnRun { get; set; } =
        (_, _) => Task.FromResult(TurnEndReason.FinalMessage);

    public IReadOnlyList<TurnRunRequest> Requests
    {
        get
        {
            lock (_gate)
                return [.. _requests];
        }
    }

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public async Task<TurnRunOutcome> RunAsync(TurnRunRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request);
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);
        }

        try
        {
            var reason = await OnRun(request, cancellationToken);
            return new TurnRunOutcome(Guid.CreateVersion7(), reason, null, 1);
        }
        finally
        {
            lock (_gate)
                _inFlight--;
        }
    }
}

/// <summary>Trips for the agents listed in <see cref="Tripped"/> and records every wake it is asked about.</summary>
internal sealed class FakeLoopGuard : ILoopGuard
{
    private readonly Lock _gate = new();
    private readonly List<AgentWake> _wakes = [];

    public HashSet<Guid> Tripped { get; } = [];

    public IReadOnlyList<AgentWake> Wakes
    {
        get
        {
            lock (_gate)
                return [.. _wakes];
        }
    }

    public GuardVerdict Check(LoopEvent loopEvent)
    {
        var wake = Assert.IsType<AgentWake>(loopEvent);
        lock (_gate)
            _wakes.Add(wake);

        return Tripped.Contains(Guid.Parse(wake.AgentId))
            ? GuardVerdict.Trip(GuardKind.WakesPerHour, "test trip", [wake.AgentId])
            : GuardVerdict.Ok;
    }

    public bool ScoredWakeWithinCap(string agentId) => true;
}
