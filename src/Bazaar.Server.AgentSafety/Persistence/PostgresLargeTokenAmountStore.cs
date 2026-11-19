using Bazaar.Contracts.Numerics;
using Npgsql;

namespace Bazaar.Server.AgentSafety.Persistence;

/// <summary>
/// The large tier for token amounts beyond the PostgreSQL <c>numeric</c> limits (doc 48, DECIDED 11.4, "the disk is the
/// limit"), over <c>token_amount_large</c> and <c>token_amount_large_chunk</c> (agent_safety migration 0006) in the main
/// database: no second store, no file and no external service, so backups cover it. Amounts are stored in clear (owner card,
/// 2026-11-19); role grants and disk encryption protect them. Works under the runtime role (<c>bazaar_app</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Writing.</b> <see cref="BeginStagingAsync"/> creates the header in state <c>writing</c>; the codec then writes the
/// chunks, which are inserted in their own transactions, <see cref="ChunksPerTransaction"/> chunks each, and completes the
/// header with the digit counts and the SHA-256. <see cref="SealAsync(NpgsqlConnection, NpgsqlTransaction, Guid, CancellationToken)"/>
/// seals it in the caller's transaction, the one that inserts the row that owns it. Only sealed amounts can be opened.
/// </para>
/// <para>
/// <b>Reading.</b> <see cref="OpenAsync(Guid, CancellationToken)"/> returns a chunk reader; the codec reads one chunk at a
/// time. A full read (<see cref="TryReadAsync"/>, <see cref="VerifyAsync"/>, or any addition) checks the SHA-256 and throws
/// <see cref="TokenAmountIntegrityException"/> on a mismatch.
/// </para>
/// <para>
/// <b>Deleting.</b> There are no foreign keys: the owner deletes an amount with the row that owns it
/// (<see cref="DeleteAsync(NpgsqlConnection, NpgsqlTransaction?, IReadOnlyCollection{Guid}, CancellationToken)"/>), and
/// <see cref="SweepStagedAsync"/> removes staged amounts that were never sealed. Every instant is the caller's
/// (<see cref="TimeProvider"/>), never the database clock.
/// </para>
/// </remarks>
public sealed class PostgresLargeTokenAmountStore
{
    /// <summary>Chunks inserted per staging transaction: 4 MiB per commit, so no single transaction grows with the amount.</summary>
    public const int DefaultChunksPerTransaction = 4;

    private const string _insertHeader = "INSERT INTO token_amount_large (state, created_at) VALUES ('writing', $1) RETURNING id";
    private const string _readHeader =
        "SELECT integer_digits, fraction_digits, sha256, state FROM token_amount_large WHERE id = $1";
    private const string _seal =
        "UPDATE token_amount_large SET state = 'sealed' WHERE id = $1 AND state = 'writing' AND sha256 IS NOT NULL";
    private const string _deleteChunks = "DELETE FROM token_amount_large_chunk WHERE amount_id = ANY($1)";
    private const string _deleteHeaders = "DELETE FROM token_amount_large WHERE id = ANY($1)";
    private const string _deleteStagedHeader = "DELETE FROM token_amount_large WHERE id = $1 AND state = 'writing'";
    private const string _sweepHeaders =
        "DELETE FROM token_amount_large WHERE state = 'writing' AND created_at < $1 RETURNING id";

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _time;

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunksPerTransaction"/> is below 1.</exception>
    public PostgresLargeTokenAmountStore(
        NpgsqlDataSource dataSource, TimeProvider? time = null, TokenAmountChunkCodec? codec = null, int chunksPerTransaction = DefaultChunksPerTransaction)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunksPerTransaction, 1);
        _dataSource = dataSource;
        _time = time ?? TimeProvider.System;
        Codec = codec ?? TokenAmountChunkCodec.Standard;
        ChunksPerTransaction = chunksPerTransaction;
    }

    /// <summary>The codec for this store's layout; its pool holds every chunk buffer the store reads.</summary>
    public TokenAmountChunkCodec Codec { get; }

    /// <summary>Chunks inserted per staging transaction.</summary>
    public int ChunksPerTransaction { get; }

    /// <summary>Creates a staged header (committed, state <c>writing</c>, created now) and returns its writer.</summary>
    public async Task<PostgresStagedTokenAmount> BeginStagingAsync(CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(_insertHeader);
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(_time.GetUtcNow()) });
        var id = (Guid)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        return new PostgresStagedTokenAmount(_dataSource, this, id, ChunksPerTransaction);
    }

    /// <summary>Stages an in-memory amount in full. The caller seals it, or disposes the result to delete it.</summary>
    public async Task<PostgresStagedTokenAmount> StageAsync(TokenAmount amount, CancellationToken ct = default)
    {
        var staged = await BeginStagingAsync(ct).ConfigureAwait(false);
        try
        {
            await Codec.WriteAsync(amount, staged, ct).ConfigureAwait(false);
            return staged;
        }
        catch
        {
            await staged.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Seals a completed staged amount inside <paramref name="transaction"/>, the transaction that also inserts its owner.
    /// </summary>
    /// <exception cref="InvalidOperationException">No completed staged amount has that id (it was swept, or sealed already).</exception>
    public static async Task SealAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        await using var cmd = new NpgsqlCommand(_seal, connection, transaction);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException($"Large token amount {id} is not a completed staged amount; it cannot be sealed.");
        }
    }

    /// <summary>Seals a completed staged amount in its own transaction (tooling; owners seal in their own transaction).</summary>
    /// <exception cref="InvalidOperationException">No completed staged amount has that id.</exception>
    public async Task SealAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await SealAsync(conn, tx, id, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A reader over a sealed amount, each chunk on a pooled connection. Null when no sealed amount has that id.</summary>
    public async Task<PostgresTokenAmountChunkReader?> OpenAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var header = await ReadSealedHeaderAsync(conn, null, id, ct).ConfigureAwait(false);
        return header is { } h ? PostgresTokenAmountChunkReader.Over(_dataSource, id, h.Digits, h.Sha256, Codec.Pool) : null;
    }

    /// <summary>A reader over a sealed amount inside the caller's transaction. Null when no sealed amount has that id.</summary>
    public async Task<PostgresTokenAmountChunkReader?> OpenAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var header = await ReadSealedHeaderAsync(connection, transaction, id, ct).ConfigureAwait(false);
        return header is { } h ? PostgresTokenAmountChunkReader.Within(connection, transaction, id, h.Digits, h.Sha256, Codec.Pool) : null;
    }

    /// <summary>
    /// Reads a sealed amount in full, verified, when it has at most <paramref name="maxTotalDigits"/> digits; null when it is
    /// larger (never materialised) or does not exist.
    /// </summary>
    /// <exception cref="TokenAmountIntegrityException">The amount fails its integrity check.</exception>
    public async Task<TokenAmount?> TryReadAsync(Guid id, long maxTotalDigits, CancellationToken ct = default)
    {
        var reader = await OpenAsync(id, ct).ConfigureAwait(false);
        return reader is null ? null : await Codec.TryReadAsync(reader, maxTotalDigits, ct).ConfigureAwait(false);
    }

    /// <summary>Reads a sealed amount in full and checks every chunk and the SHA-256 (the Kodo self-check's per-amount step).</summary>
    /// <exception cref="TokenAmountIntegrityException">The amount fails its integrity check.</exception>
    /// <exception cref="InvalidOperationException">No sealed amount has that id.</exception>
    public async Task VerifyAsync(Guid id, CancellationToken ct = default)
    {
        var reader = await OpenAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No sealed large token amount has id {id}.");
        await Codec.VerifyAsync(reader, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes one amount, sealed or staged, with its chunks. False when it did not exist.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var deleted = await DeleteAsync(conn, tx, [id], ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return deleted == 1;
    }

    /// <summary>Deletes amounts and their chunks inside the caller's transaction. Returns the headers deleted.</summary>
    public static async Task<int> DeleteAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return 0;
        }

        var array = ids.ToArray();

        // Headers first: a header delete waits for any staging batch that holds it FOR SHARE, so the chunk delete after it
        // sees every chunk that batch committed.
        await using var headers = new NpgsqlCommand(_deleteHeaders, connection, transaction);
        headers.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = array });
        var deleted = await headers.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var chunks = new NpgsqlCommand(_deleteChunks, connection, transaction);
        chunks.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = array });
        await chunks.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// Deletes one amount only while it is still staged, with its chunks. A sealed amount is never touched, so an owner whose
    /// commit outcome is unknown cannot lose the amount its row refers to. False when no staged amount has that id.
    /// </summary>
    public async Task<bool> DeleteStagedAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var header = new NpgsqlCommand(_deleteStagedHeader, conn, tx))
        {
            header.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
            if (await header.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                return false;
            }
        }

        await using (var chunks = new NpgsqlCommand(_deleteChunks, conn, tx))
        {
            chunks.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [id] });
            await chunks.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Removes every staged (never sealed) amount created before <paramref name="olderThan"/>, with its chunks (doc 48,
    /// DECIDED 11.4: after <c>agent.budget.large_amount_staging_hours</c>). Sealed amounts are never touched. Returns the
    /// number of amounts removed.
    /// </summary>
    public async Task<int> SweepStagedAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var ids = new List<Guid>();
        await using (var cmd = new NpgsqlCommand(_sweepHeaders, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(olderThan) });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        if (ids.Count > 0)
        {
            await using var chunks = new NpgsqlCommand(_deleteChunks, conn, tx);
            chunks.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. ids] });
            await chunks.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return ids.Count;
    }

    private static async Task<(TokenAmountDigitCounts Digits, byte[] Sha256)?> ReadSealedHeaderAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(_readHeader, connection, transaction);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetString(3) != "sealed")
        {
            return null;
        }

        return (new TokenAmountDigitCounts(reader.GetInt64(0), reader.GetInt64(1)), reader.GetFieldValue<byte[]>(2));
    }

    /// <summary>Cuts an instant to whole microseconds, the precision Postgres stores, so a write and its read-back agree.</summary>
    internal static DateTimeOffset TruncateToMicroseconds(DateTimeOffset instant)
    {
        var ticks = instant.Ticks - instant.Ticks % 10;
        return new DateTimeOffset(ticks, instant.Offset);
    }
}
