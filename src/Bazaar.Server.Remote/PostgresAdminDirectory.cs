using Bazaar.Server.WebAdmin;
using Npgsql;

namespace Bazaar.Server.Components.Remote;

/// <summary>
/// The admin panel's user and device read ports (doc 51 section 5) over the remote component's own tables
/// (<c>remote/0001_pairing_auth.sql</c>). Read-only; no secret column is selected. Only humans live in <c>human</c>, so
/// <see cref="AdminUser.IsAgent"/> is always false here. A device's last-seen time is its newest refresh credential issue
/// (each token refresh issues one), falling back to its pairing time.
/// </summary>
public sealed class PostgresAdminDirectory(NpgsqlDataSource dataSource) : IAdminUserDirectory, IAdminDeviceDirectory
{
    private const string _usersSql = "SELECT id, display_name, role, status FROM human ORDER BY created_at, id";

    private const string _devicesSql = """
        SELECT d.id, d.human_id, d.label, d.created_at, d.revoked_at,
               (SELECT max(rc.issued_at) FROM refresh_credential rc WHERE rc.device_id = d.id)
        FROM device d
        ORDER BY d.created_at, d.id
        """;

    /// <summary>A member is revoked unless the account is active (suspended and removed accounts both lose panel access).</summary>
    public static AdminUser MapUser(Guid id, string displayName, string role, string status) =>
        new(id, displayName, role, IsAgent: false, Revoked: !string.Equals(status, "active", StringComparison.Ordinal));

    public static AdminDevice MapDevice(Guid id, Guid humanId, string label, DateTimeOffset createdAt, DateTimeOffset? revokedAt, DateTimeOffset? lastIssued) =>
        new(id, humanId, label, lastIssued ?? createdAt, Revoked: revokedAt is not null);

    async Task<IReadOnlyList<AdminUser>> IAdminUserDirectory.ListAsync(CancellationToken ct)
    {
        var rows = new List<AdminUser>();
        await using var cmd = dataSource.CreateCommand(_usersSql);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(MapUser(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return rows;
    }

    async Task<IReadOnlyList<AdminDevice>> IAdminDeviceDirectory.ListAsync(CancellationToken ct)
    {
        var rows = new List<AdminDevice>();
        await using var cmd = dataSource.CreateCommand(_devicesSql);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(MapDevice(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
        }
        return rows;
    }
}
