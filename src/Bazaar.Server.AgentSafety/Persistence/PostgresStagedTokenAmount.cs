using System.Buffers;
using Bazaar.Contracts.Numerics;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.AgentSafety.Persistence;

/// <summary>
/// A large token amount being staged in Postgres (doc 48, DECIDED 11.4, "Writing"): the header row exists in state
/// <c>writing</c>, chunks are inserted in their own transactions, one per batch, and <see cref="CompleteAsync"/> records the
/// digit counts and the SHA-256, still unsealed. The amount is sealed by
/// <see cref="PostgresLargeTokenAmountStore.SealAsync(NpgsqlConnection, NpgsqlTransaction, Guid, CancellationToken)"/> in the
/// transaction that inserts the row that owns it.
/// </summary>
/// <remarks>
/// Disposing deletes the staged amount (best effort, and only while it is still unsealed) unless <see cref="Keep"/> was
/// called, so an amount that is not taken over by a sealed owner does not linger; a crash leaves it to the sweeper. Each chunk insert locks the header row
/// (<c>FOR SHARE</c>), so a sweep cannot remove the header while a batch is still adding chunks to it, and a batch that
/// starts after a sweep finds no header and fails instead of leaving orphan chunks. Not thread-safe.
/// </remarks>
public sealed class PostgresStagedTokenAmount : ITokenAmountChunkWriter, IAsyncDisposable
{
    private const string _insertChunk =
        "INSERT INTO token_amount_large_chunk (amount_id, part, seq, digits) " +
        "SELECT $1, $2, $3, $4 FROM token_amount_large WHERE id = $1 AND state = 'writing' FOR SHARE";
    private const string _complete =
        "UPDATE token_amount_large SET integer_digits = $2, fraction_digits = $3, sha256 = $4 " +
        "WHERE id = $1 AND state = 'writing' AND sha256 IS NULL";

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresLargeTokenAmountStore _store;
    private readonly int _chunksPerTransaction;
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;
    private int _inBatch;
    private TokenAmountDigitCounts _digits;
    private byte[]? _sha256;
    private bool _keep;
    private bool _disposed;

    internal PostgresStagedTokenAmount(NpgsqlDataSource dataSource, PostgresLargeTokenAmountStore store, Guid id, int chunksPerTransaction)
    {
        _dataSource = dataSource;
        _store = store;
        _chunksPerTransaction = chunksPerTransaction;
        Id = id;
    }

    /// <summary>The header id.</summary>
    public Guid Id { get; }

    /// <summary>True once the counts and hash are recorded.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>The recorded digit counts.</summary>
    /// <exception cref="InvalidOperationException">The amount is not complete.</exception>
    public TokenAmountDigitCounts Digits => IsComplete ? _digits : throw new InvalidOperationException("The staged amount is not complete.");

    /// <summary>The recorded SHA-256.</summary>
    /// <exception cref="InvalidOperationException">The amount is not complete.</exception>
    public byte[] Sha256 => _sha256 ?? throw new InvalidOperationException("The staged amount is not complete.");

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The header is gone (swept) or no longer staged.</exception>
    public async ValueTask WriteChunkAsync(TokenAmountPart part, long seq, ReadOnlyMemory<byte> digits, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsComplete)
        {
            throw new InvalidOperationException("The staged amount is complete; no chunk can be added.");
        }

        var tx = await BatchAsync(ct).ConfigureAwait(false);
        await using (var cmd = new NpgsqlCommand(_insertChunk, _connection, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Id });
            cmd.Parameters.Add(new NpgsqlParameter<short> { TypedValue = (short)part, NpgsqlDbType = NpgsqlDbType.Smallint });
            cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = seq });
            cmd.Parameters.Add(new NpgsqlParameter<ReadOnlyMemory<byte>> { TypedValue = digits, NpgsqlDbType = NpgsqlDbType.Bytea });
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException($"Large token amount {Id} is no longer staged (swept, or sealed elsewhere).");
            }
        }

        if (++_inBatch >= _chunksPerTransaction)
        {
            await CommitBatchAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The header is gone (swept), no longer staged, or completed already.</exception>
    public async ValueTask CompleteAsync(TokenAmountDigitCounts digits, ReadOnlyMemory<byte> sha256, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsComplete)
        {
            throw new InvalidOperationException("The staged amount is complete already.");
        }

        var tx = await BatchAsync(ct).ConfigureAwait(false);
        var hash = sha256.ToArray();
        await using (var cmd = new NpgsqlCommand(_complete, _connection, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Id });
            cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = digits.IntegerDigits });
            cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = digits.FractionDigits });
            cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = hash });
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException($"Large token amount {Id} is no longer staged (swept, or sealed elsewhere).");
            }
        }

        await CommitBatchAsync(ct).ConfigureAwait(false);
        await CloseConnectionAsync().ConfigureAwait(false);
        SetComplete(digits, hash);
    }

    /// <summary>Commits the open batch, so every chunk written so far is visible to other connections.</summary>
    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await CommitBatchAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads back one chunk as it was written, on a pooled connection, before the amount is complete: for a staged amount
    /// used as scratch space (<see cref="ITokenAmountDigitScratch"/>), whose chunks are not in the stored layout. Call
    /// <see cref="FlushAsync"/> first. The buffer comes from the store's pool; the caller disposes it.
    /// </summary>
    /// <exception cref="TokenAmountIntegrityException">No such chunk was written.</exception>
    public ValueTask<IMemoryOwner<byte>> ReadWrittenChunkAsync(TokenAmountPart part, long seq, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return PostgresTokenAmountChunkReader.Over(_dataSource, Id, default, [], _store.Codec.Pool).ReadChunkAsync(part, seq, ct);
    }

    /// <summary>A reader over the completed (not necessarily sealed) chunks, for streamed arithmetic on a scratch result.</summary>
    /// <exception cref="InvalidOperationException">The amount is not complete.</exception>
    public PostgresTokenAmountChunkReader OpenReader(NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null) =>
        connection is null
            ? PostgresTokenAmountChunkReader.Over(_dataSource, Id, Digits, Sha256, _store.Codec.Pool)
            : PostgresTokenAmountChunkReader.Within(connection, transaction, Id, Digits, Sha256, _store.Codec.Pool);

    /// <summary>The amount is now owned by a sealed row; disposing no longer deletes it.</summary>
    public void Keep() => _keep = true;

    /// <summary>Rolls back an open batch and deletes the staged amount unless <see cref="Keep"/> was called.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CloseConnectionAsync().ConfigureAwait(false);
        if (!_keep)
        {
            try
            {
                await _store.DeleteStagedAsync(Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (NpgsqlException)
            {
                // Best effort: the sweeper removes an unsealed amount after agent.budget.large_amount_staging_hours.
            }
        }
    }

    private void SetComplete(TokenAmountDigitCounts digits, byte[] sha256)
    {
        _digits = digits;
        _sha256 = sha256;
        IsComplete = true;
    }

    private async ValueTask<NpgsqlTransaction> BatchAsync(CancellationToken ct)
    {
        _connection ??= await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        _transaction ??= await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        return _transaction;
    }

    private async ValueTask CommitBatchAsync(CancellationToken ct)
    {
        if (_transaction is not null)
        {
            await _transaction.CommitAsync(ct).ConfigureAwait(false);
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }

        _inBatch = 0;
    }

    private async ValueTask CloseConnectionAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }
}
