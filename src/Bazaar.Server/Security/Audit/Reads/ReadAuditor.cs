namespace Bazaar.Server.Security.Audit.Reads;

/// <summary>
/// Records reads of server data as <c>data.read</c> rows on the audit log (doc 48 section 5.1). Inside a
/// <see cref="ReadAuditScope"/> the non-personal reads of one query shape are summed into one row, written when the scope
/// ends. A personal object read, or a read with no scope, is written at once. Rows go through <see cref="IAuditLog"/>, so
/// they join the same hash chain as every other audit row.
/// </summary>
public sealed class ReadAuditor(IAuditLog log, TimeProvider time)
{
    private readonly IAuditLog _log = log ?? throw new ArgumentNullException(nameof(log));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));

    /// <summary>Opens a scope for the reads that follow on this async flow. Dispose it (await the dispose) to write its batched rows.</summary>
    public ReadAuditScope BeginScope(ReadAccessContext context)
    {
        DataReadPayload.ValidateContext(context);
        return new ReadAuditScope(this, context);
    }

    /// <summary>
    /// Records one read. In a scope, a non-personal read is added to the scope's batch (written at dispose). Otherwise it is
    /// written now, under the scope's context or <see cref="ReadAccessContext.Unattributed"/>.
    /// </summary>
    public async Task RecordAsync(DataRead read, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        DataReadPayload.ValidateRead(read);

        var scope = ReadAuditScope.Current;
        if (scope is not null && read.ObjectId is null && scope.TryAdd(read))
            return;

        var context = scope?.Context ?? ReadAccessContext.Unattributed;
        var now = _time.GetUtcNow();
        await AppendRowAsync(context, new DataReadRow(read.ObjectType, read.ObjectId, read.QueryKind, read.QueryShapeHash, read.ResultCount, now, now), ct)
            .ConfigureAwait(false);
    }

    internal DateTimeOffset Now => _time.GetUtcNow();

    internal Task AppendRowAsync(ReadAccessContext context, DataReadRow row, CancellationToken ct)
    {
        var payload = DataReadPayload.Build(context, row).ToJsonString();
        return _log.AppendAsync(new AuditEntry(_time.GetUtcNow(), context.Principal, DataReadPayload.Kind, payload), ct);
    }
}

/// <summary>
/// The reads of one unit of work (a request, a tool call, a job) under one <see cref="ReadAccessContext"/>. The scope is
/// current on its async flow until it is disposed, and nested scopes restore the outer one. Batched rows are written by
/// <see cref="DisposeAsync"/>, so await it. A scope with no reads writes nothing.
/// </summary>
public sealed class ReadAuditScope : IAsyncDisposable
{
    private static readonly AsyncLocal<ReadAuditScope?> _current = new();

    private readonly ReadAuditor _auditor;
    private readonly ReadAuditScope? _previous;
    private readonly object _gate = new();
    private readonly Dictionary<BatchKey, Tally> _tallies = [];
    private bool _disposed;

    internal ReadAuditScope(ReadAuditor auditor, ReadAccessContext context)
    {
        _auditor = auditor;
        Context = context;
        _previous = _current.Value;
        _current.Value = this;
    }

    /// <summary>Who and why, for every read in this scope.</summary>
    public ReadAccessContext Context { get; }

    /// <summary>The innermost scope on this async flow, or null.</summary>
    internal static ReadAuditScope? Current => _current.Value;

    /// <summary>Adds a read to the batch. Returns false when the scope has already been disposed, so the caller writes the row itself.</summary>
    internal bool TryAdd(DataRead read)
    {
        var now = _auditor.Now;
        var key = new BatchKey(read.ObjectType, read.QueryKind, read.QueryShapeHash);
        lock (_gate)
        {
            if (_disposed)
                return false;
            _tallies[key] = _tallies.TryGetValue(key, out var tally)
                ? tally with { Count = tally.Count + read.ResultCount, Last = now }
                : new Tally(read.ResultCount, now, now);
            return true;
        }
    }

    /// <summary>Writes one row per query shape (sorted, so the log is deterministic), then ends the scope. Idempotent.</summary>
    public ValueTask DisposeAsync()
    {
        // The scope is closed and the outer one restored here, synchronously. An async method would change the AsyncLocal
        // on its own flow and lose the change for the caller, so only the flush is async.
        var rows = Close();
        return rows is null ? ValueTask.CompletedTask : FlushAsync(rows);
    }

    private List<DataReadRow>? Close()
    {
        List<DataReadRow> rows;
        lock (_gate)
        {
            if (_disposed)
                return null;
            _disposed = true;
            rows = _tallies
                .OrderBy(kv => kv.Key.ObjectType, StringComparer.Ordinal)
                .ThenBy(kv => kv.Key.QueryKind ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(kv => kv.Key.QueryShapeHash ?? string.Empty, StringComparer.Ordinal)
                .Select(kv => new DataReadRow(kv.Key.ObjectType, null, kv.Key.QueryKind, kv.Key.QueryShapeHash, kv.Value.Count, kv.Value.First, kv.Value.Last))
                .ToList();
            _tallies.Clear();
        }

        // Restore the outer scope only when this scope is the one current on this flow.
        if (ReferenceEquals(_current.Value, this))
            _current.Value = _previous;
        return rows;
    }

    private async ValueTask FlushAsync(List<DataReadRow> rows)
    {
        foreach (var row in rows)
            await _auditor.AppendRowAsync(Context, row, CancellationToken.None).ConfigureAwait(false);
    }

    private readonly record struct BatchKey(string ObjectType, string? QueryKind, string? QueryShapeHash);

    private sealed record Tally(long Count, DateTimeOffset First, DateTimeOffset Last);
}
