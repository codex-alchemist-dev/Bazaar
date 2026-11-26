namespace Bazaar.Client.Athanor.Tests.Saves;

/// <summary>A clock that only moves when the test says so. Timers fire synchronously inside <see cref="Advance"/>.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_timers)
            {
                next = _timers.Where(t => t.Due is not null && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
            }

            if (next is null)
            {
                break;
            }

            _now = next.Due!.Value > _now ? next.Due.Value : _now;
            next.Fire();
        }

        _now = target;
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Fire()
        {
            Due = null; // one-shot: the coordinator re-arms it on the next change
            callback(state);
        }

        public void Dispose()
        {
            Due = null;
            lock (owner._timers)
            {
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A scriptable file source. Outcomes queue up; a gate can hold a save open to test overlap.</summary>
internal sealed class FakeFileSource : IEditorFileSource
{
    private readonly Queue<SaveOutcome> _outcomes = new();
    private int _inFlight;

    public List<(EditorDocumentId Id, string Content, string BaseRevision)> Saves { get; } = [];

    public int MaxConcurrent { get; private set; }

    /// <summary>When set, a save waits for it after recording itself.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Enqueue(SaveOutcome outcome) => _outcomes.Enqueue(outcome);

    public Task<EditorDocument?> OpenAsync(EditorDocumentId id, CancellationToken cancellationToken) =>
        Task.FromResult<EditorDocument?>(null);

    public async Task<SaveOutcome> SaveAsync(EditorDocumentId id, string content, string baseRevision, CancellationToken cancellationToken)
    {
        MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _inFlight));
        try
        {
            lock (Saves)
            {
                Saves.Add((id, content, baseRevision));
            }

            Started.TrySetResult();
            if (Hold is { } hold)
            {
                await hold.Task.ConfigureAwait(false);
            }

            return _outcomes.Count > 0 ? _outcomes.Dequeue() : new SaveOutcome.Saved("r" + (Saves.Count + 1));
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>An in-memory draft store that records every write and can hold writes back.</summary>
internal sealed class FakeDraftStore : IEditorDraftStore
{
    private readonly Dictionary<EditorDocumentId, EditorDraft> _drafts = [];

    public List<EditorDraft> Writes { get; } = [];

    public int Deletes { get; private set; }

    public Exception? FailWith { get; set; }

    /// <summary>When set, the next write waits for it (once).</summary>
    public TaskCompletionSource? HoldNextWrite { get; set; }

    public async Task WriteAsync(EditorDraft draft, CancellationToken cancellationToken)
    {
        var hold = HoldNextWrite;
        HoldNextWrite = null;
        if (hold is not null)
        {
            await hold.Task.ConfigureAwait(false);
        }

        if (FailWith is { } failure)
        {
            throw failure;
        }

        lock (_drafts)
        {
            _drafts[draft.Id] = draft;
            Writes.Add(draft);
        }
    }

    public Task<EditorDraft?> ReadAsync(EditorDocumentId id, CancellationToken cancellationToken)
    {
        lock (_drafts)
        {
            return Task.FromResult(_drafts.GetValueOrDefault(id));
        }
    }

    public Task DeleteAsync(EditorDocumentId id, CancellationToken cancellationToken)
    {
        lock (_drafts)
        {
            _drafts.Remove(id);
            Deletes++;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EditorDraft>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_drafts)
        {
            return Task.FromResult<IReadOnlyList<EditorDraft>>([.. _drafts.Values]);
        }
    }

    public void Seed(EditorDraft draft)
    {
        lock (_drafts)
        {
            _drafts[draft.Id] = draft;
        }
    }

    public bool Has(EditorDocumentId id)
    {
        lock (_drafts)
        {
            return _drafts.ContainsKey(id);
        }
    }
}
