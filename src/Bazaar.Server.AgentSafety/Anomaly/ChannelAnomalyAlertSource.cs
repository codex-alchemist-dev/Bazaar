using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Bazaar.Server.AgentSafety.Anomaly;

/// <summary>
/// An in-process alert queue: the rule engine publishes into it and the containment service reads from it. The queue is bounded and
/// drops the oldest alert when full, so a slow reader never blocks the engine. <see cref="DroppedCount"/> counts those drops.
/// </summary>
public sealed class ChannelAnomalyAlertSource : IAnomalyAlertSource, IAnomalyAlertSink
{
    /// <summary>The default queue size.</summary>
    public const int DefaultCapacity = 256;

    private readonly Channel<AnomalyAlert> _channel;
    private readonly int _capacity;
    private readonly object _gate = new();

    // Alerts in the channel and the drop count. Both change only under _gate, so the count is exact.
    private int _queued;
    private long _droppedCount;

    public ChannelAnomalyAlertSource(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The capacity must be at least 1.");
        }

        _capacity = capacity;
        _channel = Channel.CreateBounded<AnomalyAlert>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>How many alerts were dropped because the queue was full. The oldest alert goes first.</summary>
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

    /// <summary>Queues one alert. Never waits: when the queue is full, the oldest alert is dropped and counted.</summary>
    public ValueTask PublishAsync(AnomalyAlert alert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);
        lock (_gate)
        {
            if (_queued == _capacity)
            {
                _droppedCount++;
            }
            else
            {
                _queued++;
            }

            _ = _channel.Writer.TryWrite(alert);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Yields each alert in publish order as it arrives, until the enumeration is cancelled.</summary>
    public async IAsyncEnumerable<AnomalyAlert> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (true)
            {
                AnomalyAlert? alert;
                lock (_gate)
                {
                    if (!reader.TryRead(out alert))
                    {
                        break;
                    }

                    _queued--;
                }

                yield return alert;
            }
        }
    }
}
