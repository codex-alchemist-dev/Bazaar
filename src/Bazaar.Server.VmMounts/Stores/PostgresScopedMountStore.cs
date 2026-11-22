using Bazaar.Server.VmMounts.Abstractions;
using Npgsql;

namespace Bazaar.Server.VmMounts.Stores;

/// <summary>
/// SQL-first access to <c>scoped_mount</c>, a table of the <c>vms</c> component (migration vms/0001). This module adds no migration.
/// The partial unique index on (vm_id, guest_path) where detached_at is null is what makes a double attach fail, even across
/// server processes; <see cref="TryInsertAsync"/> turns that violation into a false result.
/// </summary>
public sealed class PostgresScopedMountStore(NpgsqlDataSource dataSource) : IScopedMountStore
{
    private const string _columns = "id, agent_id, vm_id, kind, host_path, guest_path, mode, detached_at";

    public async Task<MountInsertResult> TryInsertAsync(ScopedMountRow row, GuestOs guestOs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        // One statement: the VM must exist, belong to the agent, run the named OS and not be Retired. Zero rows inserted means it did not.
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO scoped_mount (id, agent_id, vm_id, kind, host_path, guest_path, mode, detached_at)
            SELECT @id, @agent_id, @vm_id, @kind, @host_path, @guest_path, @mode, @detached_at
            FROM environment_vm
            WHERE id = @vm_id AND agent_id = @agent_id AND os = @os AND state <> 'Retired'
            """);
        cmd.Parameters.AddWithValue("id", row.Id);
        cmd.Parameters.AddWithValue("agent_id", row.AgentId);
        cmd.Parameters.AddWithValue("vm_id", row.VmId);
        cmd.Parameters.AddWithValue("kind", KindText(row.Kind));
        cmd.Parameters.AddWithValue("host_path", row.HostPath);
        cmd.Parameters.AddWithValue("guest_path", row.GuestPath);
        cmd.Parameters.AddWithValue("mode", ModeText(row.Mode));
        cmd.Parameters.AddWithValue("detached_at", (object?)row.DetachedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("os", guestOs == GuestOs.Windows ? "windows" : "linux");
        try
        {
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1
                ? MountInsertResult.Inserted
                : MountInsertResult.VmNotEligible;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && row.DetachedAt is null
            && ex.ConstraintName == "scoped_mount_attached_guest_path_idx")
        {
            return MountInsertResult.GuestPathTaken;
        }
    }

    public async Task<GuestOs?> GetVmGuestOsAsync(Guid vmId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT os FROM environment_vm WHERE id = @id");
        cmd.Parameters.AddWithValue("id", vmId);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) switch
        {
            "windows" => GuestOs.Windows,
            "linux" => GuestOs.Linux,
            _ => null,
        };
    }

    public async Task<IReadOnlyList<ScopedMountRow>> ListAttachedAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM scoped_mount WHERE detached_at IS NULL ORDER BY id");
        return await ReadAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<ScopedMountRow?> GetAsync(Guid mountId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM scoped_mount WHERE id = @id");
        cmd.Parameters.AddWithValue("id", mountId);
        var rows = await ReadAsync(cmd, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<IReadOnlyList<ScopedMountRow>> ListAttachedForAgentAsync(Guid agentId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {_columns} FROM scoped_mount WHERE agent_id = @agent_id AND detached_at IS NULL ORDER BY id");
        cmd.Parameters.AddWithValue("agent_id", agentId);
        return await ReadAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<bool> MarkDetachedAsync(Guid mountId, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE scoped_mount SET detached_at = @at WHERE id = @id AND detached_at IS NULL");
        cmd.Parameters.AddWithValue("id", mountId);
        cmd.Parameters.AddWithValue("at", at);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static async Task<IReadOnlyList<ScopedMountRow>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<ScopedMountRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new ScopedMountRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                ParseKind(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                ParseMode(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
        return rows;
    }

    private static string KindText(MountKind kind) => kind switch
    {
        MountKind.Soul => "soul",
        MountKind.Workspace => "workspace",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string ModeText(MountMode mode) => mode switch
    {
        MountMode.Ro => "ro",
        MountMode.Rw => "rw",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static MountKind ParseKind(string text) => text switch
    {
        "soul" => MountKind.Soul,
        "workspace" => MountKind.Workspace,
        _ => throw new InvalidOperationException($"Unknown scoped_mount.kind '{text}'."),
    };

    private static MountMode ParseMode(string text) => text switch
    {
        "ro" => MountMode.Ro,
        "rw" => MountMode.Rw,
        _ => throw new InvalidOperationException($"Unknown scoped_mount.mode '{text}'."),
    };
}
