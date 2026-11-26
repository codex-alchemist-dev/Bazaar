using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.AuditSinks.Tests;

internal static class Make
{
    public static AuditSinkRow Row(long seq, DateTimeOffset? ts = null, string payload = "{\"name\":\"enc:abc\"}") =>
        new(seq, ts ?? new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), "owner", "auth.signin", payload,
            SHA256.HashData(Encoding.UTF8.GetBytes("p" + seq)), SHA256.HashData(Encoding.UTF8.GetBytes("h" + seq)));

    public static AuditSinkCheckpoint Checkpoint(long first, long last, DateTimeOffset? at = null) =>
        new(first, last, last - first + 1, SHA256.HashData([1, (byte)last]), SHA256.HashData([2, (byte)last]),
            at ?? new DateTimeOffset(2026, 11, 1, 23, 0, 0, TimeSpan.Zero), "key-1", [9, 9, (byte)last]);
}

internal sealed class FakeSource : IAuditSinkSource
{
    public List<AuditSinkRow> Rows { get; } = [];
    public List<AuditSinkCheckpoint> Checkpoints { get; } = [];

    public Task<IReadOnlyList<AuditSinkRow>> ReadRowsAfterAsync(long afterSeq, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AuditSinkRow>>(Rows.Where(r => r.Seq > afterSeq).OrderBy(r => r.Seq).Take(limit).ToList());

    public Task<IReadOnlyList<AuditSinkCheckpoint>> ReadCheckpointsAfterAsync(long afterLastSeq, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AuditSinkCheckpoint>>(Checkpoints.Where(c => c.LastSeq > afterLastSeq).OrderBy(c => c.LastSeq).Take(limit).ToList());
}

internal sealed class FakeCursorStore : IAuditSinkCursorStore
{
    public Dictionary<string, AuditSinkCursor> Cursors { get; } = [];

    public Task<AuditSinkCursor> GetAsync(string sinkName, CancellationToken ct) =>
        Task.FromResult(Cursors.TryGetValue(sinkName, out var c) ? c : AuditSinkCursor.Start);

    public Task SaveAsync(string sinkName, AuditSinkCursor cursor, CancellationToken ct)
    {
        Cursors[sinkName] = cursor;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingSink(string name) : IAuditSink
{
    public string Name { get; } = name;
    public List<long> RowSeqs { get; } = [];
    public List<long> CheckpointSeqs { get; } = [];
    public int Failures { get; set; }
    public int Calls { get; private set; }

    public Task DeliverRowsAsync(IReadOnlyList<AuditSinkRow> rows, CancellationToken ct)
    {
        Calls++;
        if (Failures > 0)
        {
            Failures--;
            throw new InvalidOperationException("secret-endpoint-token");
        }

        RowSeqs.AddRange(rows.Select(r => r.Seq));
        return Task.CompletedTask;
    }

    public Task DeliverCheckpointsAsync(IReadOnlyList<AuditSinkCheckpoint> checkpoints, CancellationToken ct)
    {
        CheckpointSeqs.AddRange(checkpoints.Select(c => c.LastSeq));
        return Task.CompletedTask;
    }
}

internal sealed class FakeAudit : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];
    public bool Throw { get; set; }

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        if (Throw)
        {
            throw new InvalidOperationException("audit down");
        }

        Entries.Add(e);
        return Task.CompletedTask;
    }
}
