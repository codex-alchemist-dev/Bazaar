using Npgsql;

namespace Bazaar.Server.Components.Remote;

/// <summary>
/// <see cref="IPairingOfferStore"/> over the shared <see cref="NpgsqlDataSource"/>, SQL-first. Tables come from
/// <c>remote/0001_pairing_auth.sql</c> and <c>0002_pairing_device_request.sql</c>. Each transition is one guarded UPDATE.
/// </summary>
public sealed class PostgresPairingOfferStore(NpgsqlDataSource dataSource) : IPairingOfferStore
{
    private const string _columns = """
        id, created_by, role, user_code_hash, device_code_hash, expires_at, state, device_label, device_class, last_polled_at
        """;

    private const string _insertSql = """
        INSERT INTO pairing_offer (id, created_by, role, user_code_hash, device_code_hash, expires_at, state, attempts)
        VALUES ($1, $2, $3, $4, $5, $6, $7, 0)
        """;

    private const string _attachSql = """
        UPDATE pairing_offer SET device_code_hash = $2, device_label = $3, device_class = $4
        WHERE id = $1 AND state = 'pending' AND device_code_hash IS NULL
        """;

    private const string _transitionSql = "UPDATE pairing_offer SET state = $3 WHERE id = $1 AND state = $2";

    private const string _touchSql = "UPDATE pairing_offer SET last_polled_at = $2 WHERE id = $1";

    public async Task InsertAsync(PairingOfferRecord offer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(offer);
        await using var cmd = dataSource.CreateCommand(_insertSql);
        cmd.Parameters.AddWithValue(offer.Id);
        cmd.Parameters.AddWithValue(offer.CreatedBy);
        cmd.Parameters.AddWithValue(offer.Role);
        cmd.Parameters.AddWithValue(offer.UserCodeHash);
        cmd.Parameters.AddWithValue((object?)offer.DeviceCodeHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue(offer.ExpiresAt);
        cmd.Parameters.AddWithValue(offer.State);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<PairingOfferRecord?> FindByUserCodeHashAsync(byte[] hash, CancellationToken ct = default) =>
        FindOneAsync("user_code_hash = $1", hash, ct);

    public Task<PairingOfferRecord?> FindByDeviceCodeHashAsync(byte[] hash, CancellationToken ct = default) =>
        FindOneAsync("device_code_hash = $1", hash, ct);

    public Task<PairingOfferRecord?> FindAsync(Guid id, CancellationToken ct = default) =>
        FindOneAsync("id = $1", id, ct);

    public async Task<bool> TryAttachDeviceAsync(Guid id, byte[] deviceCodeHash, string label, string deviceClass, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_attachSql);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(deviceCodeHash);
        cmd.Parameters.AddWithValue(label);
        cmd.Parameters.AddWithValue(deviceClass);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> TryTransitionAsync(Guid id, string fromState, string toState, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_transitionSql);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(fromState);
        cmd.Parameters.AddWithValue(toState);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task TouchPollAsync(Guid id, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_touchSql);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<PairingOfferRecord?> FindOneAsync(string where, object key, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM pairing_offer WHERE {where}");
        cmd.Parameters.AddWithValue(key);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PairingOfferRecord(
            Id: reader.GetGuid(0),
            CreatedBy: reader.GetGuid(1),
            Role: reader.GetString(2),
            UserCodeHash: (byte[])reader.GetValue(3),
            DeviceCodeHash: reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4),
            ExpiresAt: reader.GetFieldValue<DateTimeOffset>(5),
            State: reader.GetString(6),
            DeviceLabel: reader.IsDBNull(7) ? null : reader.GetString(7),
            DeviceClass: reader.IsDBNull(8) ? null : reader.GetString(8),
            LastPolledAt: reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9));
    }
}
