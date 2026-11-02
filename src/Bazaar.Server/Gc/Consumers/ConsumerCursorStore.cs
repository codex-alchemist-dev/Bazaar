using Npgsql;

namespace Bazaar.Server.Gc.Consumers;

/// <summary>Postgres-backed <see cref="IConsumerCursorStore"/> over <c>consumer_cursor</c>, keyed by <c>(consumer, gc_id)</c>.</summary>
public sealed class ConsumerCursorStore(NpgsqlDataSource dataSource) : IConsumerCursorStore
{
    private const string _getSql = "SELECT last_seq FROM consumer_cursor WHERE consumer = $1 AND gc_id = $2";

    // One statement, so the monotonic rule is enforced by the row lock rather than by a read-then-write in the application.
    // GREATEST keeps the stored value when it is already ahead, and RETURNING gives the stored value in both cases.
    private const string _advanceSql = """
        INSERT INTO consumer_cursor (consumer, gc_id, last_seq)
        VALUES ($1, $2, $3)
        ON CONFLICT (consumer, gc_id) DO UPDATE SET last_seq = GREATEST(consumer_cursor.last_seq, EXCLUDED.last_seq)
        RETURNING last_seq
        """;

    /// <inheritdoc />
    public async Task<long> GetAsync(string consumer, Guid gcId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);

        await using var cmd = dataSource.CreateCommand(_getSql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = consumer });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        var stored = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return stored is long seq ? seq : 0;
    }

    /// <inheritdoc />
    public async Task<long> AdvanceAsync(string consumer, Guid gcId, long seq, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        ArgumentOutOfRangeException.ThrowIfNegative(seq);

        await using var cmd = dataSource.CreateCommand(_advanceSql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = consumer });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = seq });
        var stored = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return (long)stored!;
    }
}
