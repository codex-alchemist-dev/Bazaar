using Npgsql;

namespace Bazaar.Server.Diagnostics.Persistence;

/// <summary>
/// Install ownership over <c>crash_install</c> joined to the remote namespace's <c>device</c> table. The owner of an install is its
/// device's person; a person's installs are those of every device they ever enrolled, revoked ones included.
/// </summary>
public sealed class PostgresCrashInstallDirectory(NpgsqlDataSource dataSource, TimeProvider clock) : ICrashInstallDirectory, ICrashInstallRegistry
{
    /// <inheritdoc />
    public async Task<bool> TryBindAsync(Guid installId, Guid deviceId, bool isShared, CancellationToken ct = default)
    {
        if (installId == Guid.Empty || deviceId == Guid.Empty)
        {
            throw new ArgumentException("Install and device ids are required.");
        }

        var now = clock.GetUtcNow();
        await using var cmd = dataSource.CreateCommand(
            "WITH ins AS (INSERT INTO crash_install (install_id, device_id, shared, registered_at) VALUES ($1, $2, $3, $4) " +
            "ON CONFLICT (install_id) DO NOTHING RETURNING device_id) " +
            "SELECT device_id FROM ins UNION ALL SELECT device_id FROM crash_install WHERE install_id = $1 LIMIT 1");
        cmd.Parameters.AddWithValue(installId);
        cmd.Parameters.AddWithValue(deviceId);
        cmd.Parameters.AddWithValue(isShared);
        cmd.Parameters.AddWithValue(new DateTimeOffset(now.UtcTicks - (now.UtcTicks % 10), TimeSpan.Zero));
        return (Guid)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))! == deviceId;
    }

    /// <inheritdoc />
    public async Task<Guid?> GetOwnerAsync(Guid installId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT CASE WHEN i.shared THEN NULL ELSE d.human_id END FROM crash_install i JOIN device d ON d.id = i.device_id WHERE i.install_id = $1");
        cmd.Parameters.AddWithValue(installId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The install is not bound to any enrolled device.");
        }

        return r.IsDBNull(0) ? null : r.GetGuid(0);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetInstallsAsync(Guid personId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT i.install_id FROM crash_install i JOIN device d ON d.id = i.device_id WHERE d.human_id = $1 AND NOT i.shared ORDER BY i.registered_at, i.install_id");
        cmd.Parameters.AddWithValue(personId);
        var list = new List<Guid>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(r.GetGuid(0));
        }

        return list;
    }
}
