using Bazaar.Server.VmMounts.Abstractions;
using Npgsql;

namespace Bazaar.Server.VmMounts.Stores;

/// <summary>SQL-first access to <c>vm_mount_canary</c> (this module's migration vm_mounts/0001). Rows are retired, never deleted.</summary>
public sealed class PostgresCanaryStore(NpgsqlDataSource dataSource) : ICanaryStore
{
    private const string _columns = "id, mount_id, agent_id, vm_id, relative_path, fingerprint, planted_at, retired_at";

    public async Task InsertAsync(IReadOnlyList<CanaryRecord> canaries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(canaries);
        if (canaries.Count == 0)
            return;
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var c in canaries)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO vm_mount_canary (id, mount_id, agent_id, vm_id, relative_path, fingerprint, planted_at, retired_at)
                VALUES (@id, @mount_id, @agent_id, @vm_id, @relative_path, @fingerprint, @planted_at, @retired_at)
                """, conn, tx);
            cmd.Parameters.AddWithValue("id", c.Id);
            cmd.Parameters.AddWithValue("mount_id", c.MountId);
            cmd.Parameters.AddWithValue("agent_id", c.AgentId);
            cmd.Parameters.AddWithValue("vm_id", c.VmId);
            cmd.Parameters.AddWithValue("relative_path", c.RelativePath);
            cmd.Parameters.AddWithValue("fingerprint", c.Fingerprint);
            cmd.Parameters.AddWithValue("planted_at", c.PlantedAt);
            cmd.Parameters.AddWithValue("retired_at", (object?)c.RetiredAt ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<CanaryRecord?> FindByFingerprintAsync(string fingerprint, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM vm_mount_canary WHERE fingerprint = @fingerprint");
        cmd.Parameters.AddWithValue("fingerprint", fingerprint);
        var rows = await ReadAsync(cmd, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<IReadOnlyList<CanaryRecord>> ListActiveForMountAsync(Guid mountId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {_columns} FROM vm_mount_canary WHERE mount_id = @mount_id AND retired_at IS NULL ORDER BY id");
        cmd.Parameters.AddWithValue("mount_id", mountId);
        return await ReadAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task RetireForMountAsync(Guid mountId, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE vm_mount_canary SET retired_at = @at WHERE mount_id = @mount_id AND retired_at IS NULL");
        cmd.Parameters.AddWithValue("mount_id", mountId);
        cmd.Parameters.AddWithValue("at", at);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<CanaryRecord>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<CanaryRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new CanaryRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
        return rows;
    }
}
