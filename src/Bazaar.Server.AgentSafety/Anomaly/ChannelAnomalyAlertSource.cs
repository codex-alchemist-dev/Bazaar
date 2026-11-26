using System.Runtime.CompilerServices;

namespace Bazaar.Server.AgentSafety.Anomaly;

/// <summary>
/// An in-process alert queue: the rule engine publishes into it and the containment service reads from it. The queue is bounded
/// and never blocks the engine.
/// </summary>
/// <remarks>
/// A flood of alerts must not be able to push out the one alert that matters, so the queue does two things. An alert for an agent
/// and rule that already wait in the queue is merged into the waiting one (containment acts once per agent, so nothing is lost);
/// the waiting alert keeps its place and takes the higher severity. When the queue is still full, the alert that goes is the
/// oldest one of the lowest severity, unless the incoming alert is below every waiting one, in which case it is the one dropped. <see cref="DroppedCount"/>
/// counts the alerts lost that way; merged alerts are not counted.
/// </remarks>
public sealed class ChannelAnomalyAlertSource : IAnomalyAlertSource, IAnomalyAlertSink, IDisposable
{
    /// <summary>The default queue size.</summary>
    public const int DefaultCapacity = 256;

    private readonly int _capacity;
    private readonly Lock _gate = new();
    private readonly LinkedList<AnomalyAlert> _queue = new();
    private readonly Dictionary<(string AgentId, string RuleId), LinkedListNode<AnomalyAlert>> _waiting = new();
    private readonly SemaphoreSlim _available = new(0);
    private long _droppedCount;

    public ChannelAnomalyAlertSource(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The capacity must be at least 1.");
        }

        _capacity = capacity;
    }

    /// <summary>How many alerts were dropped because the queue was full. The lowest severity goes first, oldest first within it.</summary>
    public long DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _droppedCount;
            }
        }
    }

    /// <summary>Queues one alert. Never waits: see the class remarks for what happens to a duplicate and to a full queue.</summary>
    public ValueTask PublishAsync(AnomalyAlert alert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var key = (alert.AgentId, alert.RuleId);
        lock (_gate)
        {
            if (_waiting.TryGetValue(key, out var same))
            {
                if (alert.Severity > same.Value.Severity)
                {
                    same.Value = alert;
                }

                return ValueTask.CompletedTask;
            }

            if (_queue.Count == _capacity)
            {
                _droppedCount++;
                var victim = LowestOldestLocked();
                if (alert.Severity < victim.Value.Severity)
                {
                    // Everything waiting is more serious than the newcomer, so the newcomer is the one that goes.
                    return ValueTask.CompletedTask;
                }

                _waiting.Remove((victim.Value.AgentId, victim.Value.RuleId));
                _queue.Remove(victim);
                _waiting[key] = _queue.AddLast(alert);

                // One alert left and one arrived: the number of waiting alerts, and so the semaphore count, is unchanged.
                return ValueTask.CompletedTask;
            }

            _waiting[key] = _queue.AddLast(alert);
        }

        _available.Release();
        return ValueTask.CompletedTask;
    }

    /// <summary>Yields each alert in publish order as it arrives, until the enumeration is cancelled.</summary>
    public async IAsyncEnumerable<AnomalyAlert> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            await _available.WaitAsync(ct).ConfigureAwait(false);
            AnomalyAlert alert;
            lock (_gate)
            {
                var first = _queue.First!;
                alert = first.Value;
                _queue.RemoveFirst();
                _waiting.Remove((alert.AgentId, alert.RuleId));
            }

            yield return alert;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _available.Dispose();

    private LinkedListNode<AnomalyAlert> LowestOldestLocked()
    {
        var lowest = _queue.First!;
        for (var node = lowest.Next; node is not null; node = node.Next)
        {
            if (node.Value.Severity < lowest.Value.Severity)
            {
                lowest = node;
            }
        }

        return lowest;
    }
}
