using System.Buffers;
using System.Data;
using System.Runtime.InteropServices;
using Bazaar.Contracts.Numerics;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.AgentSafety.Persistence;

/// <summary>
/// Reads one large token amount's chunks from <c>token_amount_large_chunk</c> (agent_safety migration 0006), one chunk per
/// query, into buffers rented from the codec's <see cref="TokenAmountBufferPool"/>. Either over the data source (a pooled
/// connection per chunk) or inside a caller's open transaction, so a budget check reads what its own transaction sees.
/// </summary>
/// <remarks>
/// A missing chunk is a <see cref="TokenAmountIntegrityException"/>. The value is read with sequential access, so the driver
/// streams it into the rented buffer instead of buffering the row.
/// </remarks>
public sealed class PostgresTokenAmountChunkReader : ITokenAmountChunkReader
{
    private const string _readChunk = "SELECT digits FROM token_amount_large_chunk WHERE amount_id = $1 AND part = $2 AND seq = $3";

    private readonly NpgsqlDataSource? _dataSource;
    private readonly NpgsqlConnection? _connection;
    private readonly NpgsqlTransaction? _transaction;
    private readonly TokenAmountBufferPool _pool;
    private readonly byte[] _sha256;

    private PostgresTokenAmountChunkReader(
        Guid id, TokenAmountDigitCounts digits, byte[] sha256, TokenAmountBufferPool pool,
        NpgsqlDataSource? dataSource, NpgsqlConnection? connection, NpgsqlTransaction? transaction)
    {
        Id = id;
        Digits = digits;
        _sha256 = sha256;
        _pool = pool;
        _dataSource = dataSource;
        _connection = connection;
        _transaction = transaction;
    }

    /// <summary>A reader that opens a pooled connection for each chunk.</summary>
    public static PostgresTokenAmountChunkReader Over(
        NpgsqlDataSource dataSource, Guid id, TokenAmountDigitCounts digits, byte[] sha256, TokenAmountBufferPool pool)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentNullException.ThrowIfNull(pool);
        return new PostgresTokenAmountChunkReader(id, digits, sha256, pool, dataSource, null, null);
    }

    /// <summary>A reader that runs every chunk query on <paramref name="connection"/>, inside <paramref name="transaction"/> when given.</summary>
    public static PostgresTokenAmountChunkReader Within(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id, TokenAmountDigitCounts digits, byte[] sha256, TokenAmountBufferPool pool)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentNullException.ThrowIfNull(pool);
        return new PostgresTokenAmountChunkReader(id, digits, sha256, pool, null, connection, transaction);
    }

    /// <summary>The amount's header id.</summary>
    public Guid Id { get; }

    /// <inheritdoc />
    public TokenAmountDigitCounts Digits { get; }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Sha256 => _sha256;

    /// <inheritdoc />
    public async ValueTask<IMemoryOwner<byte>> ReadChunkAsync(TokenAmountPart part, long seq, CancellationToken ct)
    {
        await using var owned = _connection is null ? await _dataSource!.OpenConnectionAsync(ct).ConfigureAwait(false) : null;
        await using var cmd = new NpgsqlCommand(_readChunk, owned ?? _connection, owned is null ? _transaction : null);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Id });
        cmd.Parameters.Add(new NpgsqlParameter<short> { TypedValue = (short)part, NpgsqlDbType = NpgsqlDbType.Smallint });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = seq });
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleRow, ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new TokenAmountIntegrityException($"Large token amount {Id} has no chunk {seq} of the {part} part.");
        }

        // The CHECK on the column bounds a chunk at 1 MiB, so its length always fits an int.
        var length = checked((int)reader.GetBytes(0, 0, null, 0, 0));
        var owner = _pool.Rent(length);
        try
        {
            if (!MemoryMarshal.TryGetArray<byte>(owner.Memory, out var segment))
            {
                throw new InvalidOperationException("A pooled chunk buffer is not array-backed.");
            }

            var read = reader.GetBytes(0, 0, segment.Array, segment.Offset, length);
            if (read != length)
            {
                throw new TokenAmountIntegrityException($"Large token amount {Id}: chunk {seq} of the {part} part was cut short.");
            }

            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }
}
