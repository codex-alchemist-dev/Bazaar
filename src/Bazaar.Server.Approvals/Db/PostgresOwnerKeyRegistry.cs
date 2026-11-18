using Bazaar.Server.Approvals;
using Npgsql;

namespace Bazaar.Server.Approvals.Db;

/// <summary>
/// The owner key registry over the <c>approval_key</c> table (doc 32 section 5a). A device id is the device's uuid in its
/// "D" form, which matches <c>^[A-Za-z0-9._-]{1,128}\z</c>. A device is revoked when either its key or the
/// device row is revoked. Works under the runtime role: it needs SELECT only.
/// </summary>
public sealed class PostgresOwnerKeyRegistry(NpgsqlDataSource dataSource) : IOwnerKeyRegistry
{
    /// <inheritdoc />
    public async Task<OwnerDeviceKey?> FindAsync(string deviceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        if (!Guid.TryParseExact(deviceId, "D", out var device))
        {
            return null;
        }

        await using var cmd = dataSource.CreateCommand(
            "SELECT ak.human_id, ak.public_key, (ak.revoked_at IS NOT NULL OR d.revoked_at IS NOT NULL) AS revoked " +
            "FROM approval_key ak JOIN device d ON d.id = ak.device_id " +
            "WHERE ak.device_id = $1 " +
            "ORDER BY (ak.revoked_at IS NULL) DESC, ak.created_at DESC " +
            "LIMIT 1");
        cmd.Parameters.AddWithValue(device);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var human = reader.GetGuid(0);
        var spki = (byte[])reader.GetValue(1);
        var revoked = reader.GetBoolean(2);
        return new OwnerDeviceKey(human.ToString("D"), device.ToString("D"), spki, revoked);
    }
}
