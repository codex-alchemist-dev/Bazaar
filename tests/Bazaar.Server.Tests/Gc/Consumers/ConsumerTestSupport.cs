using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Consumers;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Npgsql;

namespace Bazaar.Server.Tests.Gc.Consumers;

/// <summary>Seeding and inspection helpers shared by the consumer tests.</summary>
internal static class ConsumerTestSupport
{
    /// <summary>Appends <paramref name="count"/> system events written by <paramref name="author"/>, keyed by <paramref name="tag"/>.</summary>
    public static async Task AppendAsync(GcDb db, Guid gc, Guid author, int count, string tag, CancellationToken ct)
    {
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());
        var requests = Enumerable.Range(0, count)
            .Select(i => new AppendRequest(gc, GcEventKinds.System, AuthorKind.System, author, SourceTrust.System, $"{tag}-{i}"))
            .ToList();
        await writer.AppendManyAsync(requests, ct);
    }

    public static async Task SetReadStateAsync(GcDb db, Guid principal, Guid gc, long lastReadSeq)
    {
        await using var cmd = db.DataSource.CreateCommand("INSERT INTO read_state (principal_id, gc_id, last_read_seq) VALUES ($1, $2, $3)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principal });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = lastReadSeq });
        await cmd.ExecuteNonQueryAsync();
    }

    public static GcPrincipal Principal(Guid id, PrincipalKind kind = PrincipalKind.Human) => new(id, kind, "test");
}

/// <summary>Records every batch it receives. Optionally throws on a given call (1-based) to simulate a failed batch.</summary>
internal sealed class RecordingConsumer(string name, int? failOnCall = null) : IGcConsumer
{
    private readonly List<long> _seqs = [];
    private int _calls;

    public string Name { get; } = name;

    /// <summary>Seqs of every event handled, in the order they arrived.</summary>
    public IReadOnlyList<long> Seqs => _seqs;

    /// <summary>Number of batches the consumer was handed, including a failed one.</summary>
    public int Calls => _calls;

    public Task HandleAsync(Guid gcId, IReadOnlyList<GcEvent> events, CancellationToken ct)
    {
        _calls++;
        if (_calls == failOnCall)
            throw new InvalidOperationException($"Simulated failure on batch {_calls}.");
        _seqs.AddRange(events.Select(e => e.Seq));
        return Task.CompletedTask;
    }
}

/// <summary>Denies every read, to show that the consumers read through the viewer rules.</summary>
internal sealed class DenyAllAccess : IGcReadAccess
{
    public ValueTask<GcReadGrant> GetGrantAsync(Guid gcId, GcPrincipal viewer, CancellationToken ct) =>
        ValueTask.FromResult(GcReadGrant.Denied);
}
