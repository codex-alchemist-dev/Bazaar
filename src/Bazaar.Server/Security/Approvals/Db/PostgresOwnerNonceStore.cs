using Npgsql;

namespace Bazaar.Server.Security.Approvals.Db;

/// <summary>
/// The durable owner nonce store over <c>owner_nonce</c> (doc 32 section 5a). Spending a nonce is one insert; a second
/// insert of the same nonce conflicts and is refused, so replay protection survives a restart. Expired rows are purged
/// first, because a payload past its expiry is refused by the verifier before the nonce is ever spent.
/// </summary>
public sealed class PostgresOwnerNonceStore(NpgsqlDataSource dataSource) : IOwnerNonceStore
{
    /// <inheritdoc />
    public async Task<bool> TryUseAsync(byte[] nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        if (nonce.Length != OwnerSignedPayload.NonceLength)
        {
            throw new ArgumentException("Nonce must be 16 bytes.", nameof(nonce));
        }

        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO owner_nonce (nonce, expires_at) VALUES ($1, $2) ON CONFLICT (nonce) DO NOTHING");
        cmd.Parameters.AddWithValue(nonce);
        cmd.Parameters.AddWithValue(expiresAt);

        var inserted = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return inserted == 1;
    }

    /// <summary>Removes nonces whose payloads have expired. Safe to run at any time; an expired nonce can never verify.</summary>
    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM owner_nonce WHERE expires_at <= $1");
        cmd.Parameters.AddWithValue(now);
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
