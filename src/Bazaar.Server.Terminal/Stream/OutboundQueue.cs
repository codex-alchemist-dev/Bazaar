using System.Threading.Channels;

namespace Bazaar.Server.Terminal.Stream;

/// <summary>Result of <see cref="OutboundQueue.Enqueue"/>.</summary>
internal enum EnqueueOutcome
{
    /// <summary>Queued.</summary>
    Accepted,

    /// <summary>Queued after the oldest output frame was dropped to make room.</summary>
    DroppedOldestOutput,

    /// <summary>An output frame was refused because the queue holds only undroppable frames.</summary>
    DroppedThisOutput,

    /// <summary>The queue is completed; nothing was queued.</summary>
    Closed,

    /// <summary>Undroppable frames exceed the hard limit; the connection must close.</summary>
    Overflow,
}

/// <summary>
/// Bounded outbound queue with one rule: when full, the oldest output frame is dropped and control and error frames never are.
/// Undroppable frames may exceed the soft bound up to <c>4 * capacity</c>; beyond that the owner must close the connection.
/// Thread safe; one reader.
/// </summary>
internal sealed class OutboundQueue : ChannelReader<ReadOnlyMemory<byte>>
{
    private readonly object _gate = new();
    private readonly LinkedList<Entry> _items = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _capacity;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;
    private long _droppedOutput;

    public OutboundQueue(int capacity) => _capacity = capacity;

    public override Task Completion => _completion.Task;

    public override bool CanCount => true;

    public override int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public long DroppedOutputFrames => Interlocked.Read(ref _droppedOutput);

    public EnqueueOutcome Enqueue(byte[] frame, bool isOutput)
    {
        var outcome = EnqueueOutcome.Accepted;
        lock (_gate)
        {
            if (_completed)
            {
                return EnqueueOutcome.Closed;
            }

            if (_items.Count >= _capacity)
            {
                var node = _items.First;
                while (node is not null && !node.Value.IsOutput)
                {
                    node = node.Next;
                }

                if (node is not null)
                {
                    _items.Remove(node);
                    Interlocked.Increment(ref _droppedOutput);
                    outcome = EnqueueOutcome.DroppedOldestOutput;
                }
                else if (isOutput)
                {
                    Interlocked.Increment(ref _droppedOutput);
                    return EnqueueOutcome.DroppedThisOutput;
                }
                else if (_items.Count >= _capacity * 4)
                {
                    return EnqueueOutcome.Overflow;
                }
            }

            _items.AddLast(new Entry(frame, isOutput));
            _signal.TrySetResult();
        }

        return outcome;
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            _signal.TrySetResult();
            if (_items.Count == 0)
            {
                _completion.TrySetResult();
            }
        }
    }

    public override bool TryRead(out ReadOnlyMemory<byte> item)
    {
        lock (_gate)
        {
            if (_items.First is { } first)
            {
                _items.RemoveFirst();
                item = first.Value.Bytes;
                if (_completed && _items.Count == 0)
                {
                    _completion.TrySetResult();
                }

                return true;
            }
        }

        item = default;
        return false;
    }

    public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_items.Count > 0)
                {
                    return true;
                }

                if (_completed)
                {
                    return false;
                }

                if (_signal.Task.IsCompleted)
                {
                    _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                wait = _signal.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private readonly record struct Entry(byte[] Bytes, bool IsOutput);
}
