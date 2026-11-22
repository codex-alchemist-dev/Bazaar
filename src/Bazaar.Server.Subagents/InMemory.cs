using System.Collections.Concurrent;

namespace Bazaar.Server.Subagents;

/// <summary>One scripted run for <see cref="FakeRuntimeSubagentPort"/>.</summary>
/// <param name="Result">The result to return (default: completed, empty summary).</param>
/// <param name="Delay">A delay before the result.</param>
/// <param name="Throws">An exception to throw instead of returning.</param>
/// <param name="Gate">When set, the run waits for this task before returning (cancellable).</param>
public sealed record FakeRun(SubagentResult? Result = null, TimeSpan? Delay = null, Exception? Throws = null, Task? Gate = null);

/// <summary>In-memory <see cref="IRuntimeSubagentPort"/> with scripted results and delays. Replaces the runtime in tests.</summary>
public sealed class FakeRuntimeSubagentPort : IRuntimeSubagentPort
{
    private readonly ConcurrentQueue<FakeRun> _script = new();
    private readonly ConcurrentQueue<RuntimeSubagentCall> _calls = new();
    private int _running;
    private int _maxRunning;

    /// <summary>Queues the next run; runs beyond the script complete at once.</summary>
    public void Enqueue(FakeRun run) => _script.Enqueue(run);

    /// <summary>The calls received, in order.</summary>
    public IReadOnlyCollection<RuntimeSubagentCall> Calls => _calls.ToArray();

    /// <summary>The most runs seen at once.</summary>
    public int MaxRunning => Volatile.Read(ref _maxRunning);

    /// <summary>Runs in progress now.</summary>
    public int Running => Volatile.Read(ref _running);

    /// <inheritdoc />
    public async Task<SubagentResult> RunAsync(RuntimeSubagentCall invocation, CancellationToken cancellationToken)
    {
        _calls.Enqueue(invocation);
        var now = Interlocked.Increment(ref _running);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxRunning)))
        {
            Interlocked.CompareExchange(ref _maxRunning, now, seen);
        }

        try
        {
            _script.TryDequeue(out var run);
            run ??= new FakeRun();
            if (run.Delay is { } delay)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            if (run.Gate is { } gate)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (run.Throws is { } ex)
            {
                throw ex;
            }

            return run.Result ?? new SubagentResult(SubagentStatus.Completed, UntrustedText.Empty, SubagentUsage.None);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

/// <summary>In-memory <see cref="ISubagentTrace"/>. <see cref="FailOn"/> makes appends of one kind throw, to test fail-closed.</summary>
public sealed class InMemorySubagentTrace : ISubagentTrace
{
    private readonly ConcurrentQueue<SubagentTraceEntry> _entries = new();

    /// <summary>When set, appends of this kind throw.</summary>
    public SubagentTraceKind? FailOn { get; set; }

    /// <summary>The rows written, in order.</summary>
    public IReadOnlyCollection<SubagentTraceEntry> Entries => _entries.ToArray();

    /// <inheritdoc />
    public Task AppendAsync(SubagentTraceEntry entry, CancellationToken cancellationToken)
    {
        if (FailOn == entry.Kind)
        {
            throw new InvalidOperationException("Trace store unavailable.");
        }

        _entries.Enqueue(entry);
        return Task.CompletedTask;
    }
}

/// <summary>In-memory <see cref="ISubagentWorktreeAllocator"/> that hands out one distinct path per lease.</summary>
public sealed class InMemoryWorktreeAllocator : ISubagentWorktreeAllocator
{
    private readonly ConcurrentDictionary<string, WorktreeLease> _live = new();
    private int _next;
    private int _released;

    /// <summary>When set, every allocation returns this path (to test shared-worktree refusal).</summary>
    public string? ForcedPath { get; set; }

    /// <summary>When set, allocation throws.</summary>
    public bool Fail { get; set; }

    /// <summary>Leases not yet released.</summary>
    public int LiveCount => _live.Count;

    /// <summary>Leases released so far.</summary>
    public int ReleasedCount => Volatile.Read(ref _released);

    /// <inheritdoc />
    public Task<WorktreeLease> AllocateAsync(WorktreeAllocation request, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new InvalidOperationException("No worktree available.");
        }

        var n = Interlocked.Increment(ref _next);
        var lease = new WorktreeLease($"lease-{n}", ForcedPath ?? $"/worktrees/wt-{n}", request.ReadOnly);
        _live[lease.Id] = lease;
        return Task.FromResult(lease);
    }

    /// <inheritdoc />
    public Task ReleaseAsync(WorktreeLease lease, CancellationToken cancellationToken)
    {
        if (_live.TryRemove(lease.Id, out _))
        {
            Interlocked.Increment(ref _released);
        }

        return Task.CompletedTask;
    }
}
