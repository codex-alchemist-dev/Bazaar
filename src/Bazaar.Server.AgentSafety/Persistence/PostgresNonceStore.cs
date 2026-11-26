using Bazaar.Server.Security.KillSwitch;
using Npgsql;

namespace Bazaar.Server.AgentSafety.Persistence;

/// <summary>
/// Postgres <see cref="INonceStore"/> over the <c>release_nonces</c> table (agent_safety migration 0004). A restart keeps every
/// pending nonce, so a used nonce cannot be replayed after a restart. It lives in the host because the server project is ratcheted
/// against new files; the host registers it through <c>AddBazaarKillSwitch</c>.
/// </summary>
/// <remarks>
/// <see cref="TryConsumeAsync"/> is a single <c>DELETE ... RETURNING</c>, so at most one caller gets the record. Expired rows are
/// pruned on each add, using the store's clock. One device holds at most <see cref="MaxPendingPerDevice"/> unexpired nonces. Runs under the runtime role (<c>bazaar_app</c>): SELECT, INSERT and DELETE.
/// </remarks>
public sealed class PostgresNonceStore(NpgsqlDataSource dataSource, TimeProvider? timeProvider = null) : INonceStore
{
    /// <summary>
    /// The most unexpired challenges one device may hold. A challenge only needs the device id to be requested, so without a bound
    /// anyone who knows an owner device id could fill the table. An owner uses one at a time.
    /// </summary>
    public const int MaxPendingPerDevice = 32;

    // One conditional insert: it writes nothing when the device already holds the cap. Expired rows were pruned just before, so the
    // count is of unexpired ones. Concurrent adds can overshoot the cap by the number of racing callers, which is harmless for a growth bound.
    private const string _add =
        "INSERT INTO release_nonces (nonce, agent_id, device_id, expires_at) " +
        "SELECT $1::bytea, $2::text, $3::text, $4::timestamptz WHERE (SELECT count(*) FROM release_nonces WHERE device_id = $3::text) < $5::int";
    private const string _prune = "DELETE FROM release_nonces WHERE expires_at <= $1";
    private const string _consume =
        "DELETE FROM release_nonces WHERE nonce = $1 RETURNING agent_id, device_id, expires_at";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task AddAsync(PendingNonce nonce, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(nonce.Nonce);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce.DeviceId);
        ct.ThrowIfCancellationRequested();

        await using var prune = dataSource.CreateCommand(_prune);
        prune.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(_time.GetUtcNow()) });
        await prune.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var cmd = dataSource.CreateCommand(_add);
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = nonce.Nonce });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = nonce.AgentId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = nonce.DeviceId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(nonce.ExpiresAt) });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = MaxPendingPerDevice });
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            throw new InvalidOperationException("Too many release challenges are pending for this device; wait for them to expire.");
        }
    }

    /// <inheritdoc />
    public async Task<PendingNonce?> TryConsumeAsync(byte[] nonce, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        ct.ThrowIfCancellationRequested();

        await using var cmd = dataSource.CreateCommand(_consume);
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = nonce });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new PendingNonce(nonce, reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2));
    }

    /// <summary>Cuts an instant to whole microseconds, the precision Postgres stores, so a write and its read-back agree.</summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset instant)
    {
        var ticks = instant.Ticks - instant.Ticks % 10;
        return new DateTimeOffset(ticks, instant.Offset);
    }
}
