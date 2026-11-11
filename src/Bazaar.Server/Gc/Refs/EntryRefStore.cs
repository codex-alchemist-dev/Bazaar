// Required CodeQL Analyze check runs on this head.
using Npgsql;

namespace Bazaar.Server.Gc.Refs;

/// <summary>The reference kinds <c>entry_ref.ref_kind</c> accepts (docs/design/01, entry references).</summary>
public static class EntryRefKinds
{
    public const string Pr = "pr";
    public const string Task = "task";
    public const string VaultDoc = "vault_doc";
    public const string Gc = "gc";
    public const string Blob = "blob";
    public const string Decision = "decision";

    /// <summary>True when <paramref name="kind"/> is one of the kinds above.</summary>
    public static bool IsKnown(string kind) => kind is Pr or Task or VaultDoc or Gc or Blob or Decision;
}

/// <summary>
/// A pointer from a GC message to a PR, task, Vault entry, GC, blob or decision. <see cref="SnapshotJson"/> is the target's
/// state at reference time and is frozen at post; live state is resolved at view time under the viewer's permissions.
/// </summary>
/// <param name="GcId">The GC the message was posted in.</param>
/// <param name="Seq">The seq of the event that carries the reference (the message's posting seq).</param>
/// <param name="RefKind">One of <see cref="EntryRefKinds"/>.</param>
/// <param name="RefId">The target's identifier within its kind.</param>
/// <param name="SnapshotJson">The frozen target state as a JSON object; <c>{}</c> when there is nothing to freeze.</param>
public sealed record EntryRef(Guid GcId, long Seq, string RefKind, string RefId, string SnapshotJson);

/// <summary>Reads and writes entry references. Rows are append-only: a reference is never edited or removed.</summary>
public interface IEntryRefStore
{
    /// <summary>
    /// Records a reference. Idempotent on <c>(gc_id, seq, ref_kind, ref_id)</c>: a repeat returns <c>false</c> and keeps the
    /// first snapshot, because snapshots are frozen at post. Returns <c>true</c> when a new row was written.
    /// </summary>
    Task<bool> InsertAsync(EntryRef entryRef, CancellationToken ct);

    /// <summary>
    /// The references carried by the event at <paramref name="seq"/> in <paramref name="gcId"/>, ordered by
    /// <c>(ref_kind, ref_id)</c>. Returns an empty list when the message has none or does not exist.
    /// </summary>
    Task<IReadOnlyList<EntryRef>> GetForMessageAsync(Guid gcId, long seq, CancellationToken ct);
}

/// <summary>Postgres-backed <see cref="IEntryRefStore"/> over <c>entry_ref</c>.</summary>
public sealed class EntryRefStore(NpgsqlDataSource dataSource) : IEntryRefStore
{
    // The primary key is (gc_id, seq, ref_kind, ref_id), so ON CONFLICT names the key and DO NOTHING keeps the first snapshot.
    // Rows affected is 1 for a new row and 0 for a repeat.
    private const string _insertSql = """
        INSERT INTO entry_ref (gc_id, seq, ref_kind, ref_id, snapshot)
        VALUES ($1, $2, $3, $4, $5::jsonb)
        ON CONFLICT (gc_id, seq, ref_kind, ref_id) DO NOTHING
        """;

    // COLLATE "C" makes the order byte-wise, so it does not depend on the database's locale.
    private const string _readSql = """
        SELECT ref_kind, ref_id, snapshot::text
        FROM entry_ref
        WHERE gc_id = $1 AND seq = $2
        ORDER BY ref_kind COLLATE "C", ref_id COLLATE "C"
        """;

    /// <inheritdoc />
    public async Task<bool> InsertAsync(EntryRef entryRef, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entryRef);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entryRef.Seq);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryRef.RefId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryRef.SnapshotJson);
        if (!EntryRefKinds.IsKnown(entryRef.RefKind))
            throw new ArgumentException($"Unknown entry reference kind '{entryRef.RefKind}'.", nameof(entryRef));

        await using var cmd = dataSource.CreateCommand(_insertSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = entryRef.GcId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = entryRef.Seq });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = entryRef.RefKind });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = entryRef.RefId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = entryRef.SnapshotJson });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EntryRef>> GetForMessageAsync(Guid gcId, long seq, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seq);

        await using var cmd = dataSource.CreateCommand(_readSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = seq });

        var refs = new List<EntryRef>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            refs.Add(new EntryRef(gcId, seq, reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return refs;
    }
}
