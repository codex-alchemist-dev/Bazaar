using Npgsql;

namespace Bazaar.Server.Vms.Environments;

/// <summary>
/// SQL-first access to the <c>golden_image</c>, <c>environment_vm</c>, <c>environment_checkpoint</c>, <c>scoped_mount</c>
/// and <c>exec_log</c> tables (migration components/vms/0001, doc 16). Ids and timestamps come from the caller. The methods that change a
/// row return whether a row matched, so callers see a stale state or a missing row without a second read.
/// </summary>
public sealed class EnvironmentRepository(NpgsqlDataSource dataSource)
{
    private const string _imageColumns = "id, os, version, sha256, guest_agent_version, state, created_at";
    private const string _vmColumns = "id, agent_id, os, parent_image_id, backend_ref, state, vcpu, memory_mb, disk_cap_gb, created_at, updated_at";
    private const string _checkpointColumns = "id, vm_id, backend_ref, label, created_at";
    private const string _mountColumns = "id, agent_id, vm_id, kind, host_path, guest_path, mode, detached_at";
    private const string _execColumns = "id, vm_id, started_at, argv_hash, timeout_s, exit_code, bytes_out";

    // ---- golden_image ----

    public async Task InsertGoldenImageAsync(GoldenImageRecord image, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO golden_image (id, os, version, sha256, guest_agent_version, state, created_at)
            VALUES (@id, @os, @version, @sha256, @guest_agent_version, @state, @created_at)
            """);
        cmd.Parameters.AddWithValue("id", image.Id);
        cmd.Parameters.AddWithValue("os", EnvironmentText.ToDb(image.Os));
        cmd.Parameters.AddWithValue("version", image.Version);
        cmd.Parameters.AddWithValue("sha256", image.Sha256);
        cmd.Parameters.AddWithValue("guest_agent_version", image.GuestAgentVersion);
        cmd.Parameters.AddWithValue("state", EnvironmentText.ToDb(image.State));
        cmd.Parameters.AddWithValue("created_at", image.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<GoldenImageRecord?> GetGoldenImageAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_imageColumns} FROM golden_image WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadImage(reader) : null;
    }

    /// <summary>Sets a golden image's state. Returns false when no image has that id.</summary>
    public async Task<bool> SetGoldenImageStateAsync(Guid id, GoldenImageState state, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("UPDATE golden_image SET state = @state WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("state", EnvironmentText.ToDb(state));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    // ---- environment_vm ----

    public async Task InsertVmAsync(EnvironmentVmRecord vm, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO environment_vm (id, agent_id, os, parent_image_id, backend_ref, state, vcpu, memory_mb, disk_cap_gb,
                                        created_at, updated_at)
            VALUES (@id, @agent_id, @os, @parent_image_id, @backend_ref, @state, @vcpu, @memory_mb, @disk_cap_gb,
                    @created_at, @updated_at)
            """);
        cmd.Parameters.AddWithValue("id", vm.Id);
        cmd.Parameters.AddWithValue("agent_id", vm.AgentId);
        cmd.Parameters.AddWithValue("os", EnvironmentText.ToDb(vm.Os));
        cmd.Parameters.AddWithValue("parent_image_id", vm.ParentImageId);
        cmd.Parameters.AddWithValue("backend_ref", (object?)vm.BackendRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("state", EnvironmentText.ToDb(vm.State));
        cmd.Parameters.AddWithValue("vcpu", vm.Vcpu);
        cmd.Parameters.AddWithValue("memory_mb", vm.MemoryMb);
        cmd.Parameters.AddWithValue("disk_cap_gb", vm.DiskCapGb);
        cmd.Parameters.AddWithValue("created_at", vm.CreatedAt);
        cmd.Parameters.AddWithValue("updated_at", vm.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<EnvironmentVmRecord?> GetVmAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_vmColumns} FROM environment_vm WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadVm(reader) : null;
    }

    /// <summary>Lists every VM whose state is not Retired, oldest first.</summary>
    public async Task<IReadOnlyList<EnvironmentVmRecord>> ListNonRetiredVmsAsync(CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {_vmColumns} FROM environment_vm WHERE state <> 'Retired' ORDER BY created_at, id");
        return await ReadVmsAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>Lists every VM of one agent, including retired ones, oldest first.</summary>
    public async Task<IReadOnlyList<EnvironmentVmRecord>> ListVmsForAgentAsync(Guid agentId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {_vmColumns} FROM environment_vm WHERE agent_id = @agent_id ORDER BY created_at, id");
        cmd.Parameters.AddWithValue("agent_id", agentId);
        return await ReadVmsAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a VM from <paramref name="expectedState"/> to <paramref name="newState"/> only if it is still there
    /// (compare-and-set). Returns false when the row is missing or its state changed underneath the caller.
    /// </summary>
    public async Task<bool> TryUpdateVmStateAsync(
        Guid vmId, VmState expectedState, VmState newState, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE environment_vm SET state = @new_state, updated_at = @now
            WHERE id = @vm_id AND state = @expected_state
            """);
        cmd.Parameters.AddWithValue("vm_id", vmId);
        cmd.Parameters.AddWithValue("expected_state", EnvironmentText.ToDb(expectedState));
        cmd.Parameters.AddWithValue("new_state", EnvironmentText.ToDb(newState));
        cmd.Parameters.AddWithValue("now", now);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Records the backend's handle for a VM. Returns false when no VM has that id.</summary>
    public async Task<bool> SetBackendRefAsync(Guid vmId, string backendRef, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE environment_vm SET backend_ref = @backend_ref, updated_at = @now WHERE id = @vm_id
            """);
        cmd.Parameters.AddWithValue("vm_id", vmId);
        cmd.Parameters.AddWithValue("backend_ref", backendRef);
        cmd.Parameters.AddWithValue("now", now);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    // ---- environment_checkpoint ----

    public async Task InsertCheckpointAsync(EnvironmentCheckpointRecord checkpoint, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO environment_checkpoint (id, vm_id, backend_ref, label, created_at)
            VALUES (@id, @vm_id, @backend_ref, @label, @created_at)
            """);
        cmd.Parameters.AddWithValue("id", checkpoint.Id);
        cmd.Parameters.AddWithValue("vm_id", checkpoint.VmId);
        cmd.Parameters.AddWithValue("backend_ref", checkpoint.BackendRef);
        cmd.Parameters.AddWithValue("label", (object?)checkpoint.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("created_at", checkpoint.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Lists a VM's checkpoints, oldest first.</summary>
    public async Task<IReadOnlyList<EnvironmentCheckpointRecord>> ListCheckpointsAsync(Guid vmId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {_checkpointColumns} FROM environment_checkpoint WHERE vm_id = @vm_id ORDER BY created_at, id");
        cmd.Parameters.AddWithValue("vm_id", vmId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<EnvironmentCheckpointRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new EnvironmentCheckpointRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        return rows;
    }

    // ---- scoped_mount ----

    public async Task InsertMountAsync(ScopedMountRecord mount, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO scoped_mount (id, agent_id, vm_id, kind, host_path, guest_path, mode, detached_at)
            VALUES (@id, @agent_id, @vm_id, @kind, @host_path, @guest_path, @mode, @detached_at)
            """);
        cmd.Parameters.AddWithValue("id", mount.Id);
        cmd.Parameters.AddWithValue("agent_id", mount.AgentId);
        cmd.Parameters.AddWithValue("vm_id", mount.VmId);
        cmd.Parameters.AddWithValue("kind", EnvironmentText.ToDb(mount.Kind));
        cmd.Parameters.AddWithValue("host_path", mount.HostPath);
        cmd.Parameters.AddWithValue("guest_path", mount.GuestPath);
        cmd.Parameters.AddWithValue("mode", EnvironmentText.ToDb(mount.Mode));
        cmd.Parameters.AddWithValue("detached_at", (object?)mount.DetachedAt ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Marks an attached mount as detached. Returns false when the mount is missing or already detached.</summary>
    public async Task<bool> DetachMountAsync(Guid mountId, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE scoped_mount SET detached_at = @at WHERE id = @id AND detached_at IS NULL");
        cmd.Parameters.AddWithValue("id", mountId);
        cmd.Parameters.AddWithValue("at", at);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Lists a VM's mounts in id order, including detached ones; check <see cref="ScopedMountRecord.DetachedAt"/>.</summary>
    public async Task<IReadOnlyList<ScopedMountRecord>> ListMountsAsync(Guid vmId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {_mountColumns} FROM scoped_mount WHERE vm_id = @vm_id ORDER BY id");
        cmd.Parameters.AddWithValue("vm_id", vmId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<ScopedMountRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new ScopedMountRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                EnvironmentText.ParseMountKind(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                EnvironmentText.ParseMountMode(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
        return rows;
    }

    // ---- exec_log ----

    /// <summary>Opens an exec log row and returns its database-assigned id.</summary>
    public async Task<long> InsertExecLogAsync(NewExecLog exec, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO exec_log (vm_id, started_at, argv_hash, timeout_s)
            VALUES (@vm_id, @started_at, @argv_hash, @timeout_s)
            RETURNING id
            """);
        cmd.Parameters.AddWithValue("vm_id", exec.VmId);
        cmd.Parameters.AddWithValue("started_at", exec.StartedAt);
        cmd.Parameters.AddWithValue("argv_hash", exec.ArgvHash);
        cmd.Parameters.AddWithValue("timeout_s", exec.TimeoutSeconds);
        return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>Records the exit code and output size of an open exec. Returns false when the row is missing or already complete.</summary>
    public async Task<bool> CompleteExecLogAsync(long execId, int exitCode, long bytesOut, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE exec_log SET exit_code = @exit_code, bytes_out = @bytes_out
            WHERE id = @id AND exit_code IS NULL
            """);
        cmd.Parameters.AddWithValue("id", execId);
        cmd.Parameters.AddWithValue("exit_code", exitCode);
        cmd.Parameters.AddWithValue("bytes_out", bytesOut);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Reads one exec log row. Used to check a completed exec; returns null when the id is unknown.</summary>
    public async Task<ExecLogRecord?> GetExecLogAsync(long execId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_execColumns} FROM exec_log WHERE id = @id");
        cmd.Parameters.AddWithValue("id", execId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return new ExecLogRecord(
            reader.GetInt64(0),
            reader.GetGuid(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6));
    }

    // ---- helpers ----

    private static GoldenImageRecord ReadImage(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        EnvironmentText.ParseVmOs(reader.GetString(1)),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.GetString(4),
        EnvironmentText.ParseGoldenImageState(reader.GetString(5)),
        reader.GetFieldValue<DateTimeOffset>(6));

    private static EnvironmentVmRecord ReadVm(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        EnvironmentText.ParseVmOs(reader.GetString(2)),
        reader.GetGuid(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        EnvironmentText.ParseVmState(reader.GetString(5)),
        reader.GetInt32(6),
        reader.GetInt32(7),
        reader.GetInt32(8),
        reader.GetFieldValue<DateTimeOffset>(9),
        reader.GetFieldValue<DateTimeOffset>(10));

    private static async Task<IReadOnlyList<EnvironmentVmRecord>> ReadVmsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<EnvironmentVmRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(ReadVm(reader));
        return rows;
    }
}
