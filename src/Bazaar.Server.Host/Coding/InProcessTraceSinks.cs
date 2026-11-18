using Bazaar.Contracts.Coding;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Subagents;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// A bounded, append-only, in-process list. Appending past the bound throws instead of dropping a row, so a full sink
/// fails closed (the gate denies the call, the spawner denies the spawn) and never loses audit rows silently.
/// </summary>
/// <typeparam name="T">The row type.</typeparam>
public sealed class InProcessAppendOnlyLog<T>
{
    /// <summary>The default capacity.</summary>
    public const int DefaultCapacity = 100_000;

    private readonly Lock _gate = new();
    private readonly List<T> _rows = [];
    private readonly int _capacity;

    /// <summary>Creates the log.</summary>
    /// <param name="capacity">The most rows it holds; must be positive.</param>
    public InProcessAppendOnlyLog(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        _capacity = capacity;
    }

    /// <summary>Appends a row, or throws <see cref="InvalidOperationException"/> when the log is full.</summary>
    public void Append(T row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_gate)
        {
            if (_rows.Count >= _capacity)
            {
                throw new InvalidOperationException("The in-process trace is full; the row was not written.");
            }

            _rows.Add(row);
        }
    }

    /// <summary>A copy of the rows in append order.</summary>
    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate)
        {
            return [.. _rows];
        }
    }
}

/// <summary>
/// The <c>tool_calls</c> trace as an in-process, append-only sink (doc 17, tables).
/// </summary>
/// <remarks>
/// The durable replacement is <see cref="ToolCallLogTrace"/>, used when a Postgres data source is registered; rows here live in
/// memory and are lost on restart, so this is the fallback for tests.
/// </remarks>
public sealed class InProcessToolCallTrace : IToolCallTrace
{
    private readonly InProcessAppendOnlyLog<ToolCallRecord> _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, (ToolCallStatus Status, int? ExitCode, DateTimeOffset At)> _outcomes = [];
    private readonly TimeProvider _time;

    /// <summary>Creates the sink.</summary>
    public InProcessToolCallTrace(int capacity = InProcessAppendOnlyLog<ToolCallRecord>.DefaultCapacity, TimeProvider? time = null)
    {
        _log = new InProcessAppendOnlyLog<ToolCallRecord>(capacity);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The rows written so far, with any outcome applied.</summary>
    public IReadOnlyList<ToolCallRecord> Rows
    {
        get
        {
            var rows = _log.Snapshot();
            lock (_gate)
            {
                return [.. rows.Select(r => _outcomes.TryGetValue(r.Id, out var o) ? r with { Status = o.Status, ExitCode = o.ExitCode, EndedAt = o.At } : r)];
            }
        }
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(ToolCallRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _log.Append(record);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> CompleteAsync(Guid id, ToolCallStatus status, int? exitCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (status == ToolCallStatus.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(status), "Unknown is not an outcome.");
        }

        var row = _log.Snapshot().FirstOrDefault(r => r.Id == id);
        lock (_gate)
        {
            if (row is null || row.EndedAt is not null || _outcomes.ContainsKey(id))
            {
                return ValueTask.FromResult(false);
            }

            _outcomes[id] = (status, exitCode, _time.GetUtcNow());
            return ValueTask.FromResult(true);
        }
    }
}

/// <summary>
/// The subagent spawn trace as an in-process, append-only sink (doc 17, WP12).
/// </summary>
/// <remarks>
/// The durable replacement is <see cref="ToolCallLogSubagentTrace"/>, used when a Postgres data source is registered; rows here
/// live in memory and are lost on restart, so this is the fallback for tests.
/// </remarks>
public sealed class InProcessSubagentTrace : ISubagentTrace
{
    private readonly InProcessAppendOnlyLog<SubagentTraceEntry> _log;

    /// <summary>Creates the sink.</summary>
    public InProcessSubagentTrace(int capacity = InProcessAppendOnlyLog<SubagentTraceEntry>.DefaultCapacity) =>
        _log = new InProcessAppendOnlyLog<SubagentTraceEntry>(capacity);

    /// <summary>The rows written so far.</summary>
    public IReadOnlyList<SubagentTraceEntry> Rows => _log.Snapshot();

    /// <inheritdoc />
    public Task AppendAsync(SubagentTraceEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _log.Append(entry);
        return Task.CompletedTask;
    }
}
