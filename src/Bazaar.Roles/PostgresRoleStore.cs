using Bazaar.Roles.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Roles;

/// <summary>Role storage on the shared data source (migration <c>roles/0001</c>). Floors that need atomicity use row locks.</summary>
public sealed class PostgresRoleStore(NpgsqlDataSource dataSource) : IRoleStore
{
    /// <summary>Serialises first-owner bootstraps (transaction-level advisory lock key).</summary>
    private const long _bootstrapLockKey = 0x726F6C6573L;

    public async Task<Role?> GetAsync(Guid roleId, CancellationToken ct = default)
    {
        var roles = await QueryRolesAsync("WHERE role_id = $1", roleId, ct);
        return roles.Count == 0 ? null : roles[0];
    }

    public async Task<Role?> GetByTemplateAsync(string templateKey, CancellationToken ct = default)
    {
        var roles = await QueryRolesAsync("WHERE template_key = $1", templateKey, ct);
        return roles.Count == 0 ? null : roles[0];
    }

    public Task<IReadOnlyList<Role>> ListAsync(CancellationToken ct = default) => QueryRolesAsync("", null, ct);

    public async Task<bool> CreateAsync(Role role, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var insert = new NpgsqlCommand(
                "INSERT INTO role (role_id, name, description, template_key, protected, created_at, updated_at) VALUES ($1,$2,$3,$4,$5,$6,$7)", conn, tx))
            {
                insert.Parameters.Add(Param(role.Id));
                insert.Parameters.Add(Param(role.Name));
                insert.Parameters.Add(Param(role.Description));
                insert.Parameters.Add(new NpgsqlParameter { Value = (object?)role.TemplateKey ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
                insert.Parameters.Add(Param(role.Protected));
                insert.Parameters.Add(Param(role.CreatedAt));
                insert.Parameters.Add(Param(role.UpdatedAt));
                await insert.ExecuteNonQueryAsync(ct);
            }

            await InsertGrantsAsync(conn, tx, role.Id, role.Grants, ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    public async Task<bool> ReplaceGrantsAsync(Guid roleId, IReadOnlyList<Grant> grants, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int touched;
        await using (var update = new NpgsqlCommand("UPDATE role SET updated_at = $2 WHERE role_id = $1", conn, tx))
        {
            update.Parameters.Add(Param(roleId));
            update.Parameters.Add(Param(updatedAt));
            touched = await update.ExecuteNonQueryAsync(ct);
        }

        if (touched == 0)
            return false;

        await using (var delete = new NpgsqlCommand("DELETE FROM role_grant WHERE role_id = $1", conn, tx))
        {
            delete.Parameters.Add(Param(roleId));
            await delete.ExecuteNonQueryAsync(ct);
        }

        await InsertGrantsAsync(conn, tx, roleId, grants, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid roleId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM role WHERE role_id = $1");
        cmd.Parameters.Add(Param(roleId));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> AssignAsync(RoleAssignment assignment, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_insertAssignmentSql + " ON CONFLICT DO NOTHING");
        AddAssignment(cmd, assignment);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<UnassignResult> UnassignAsync(Guid principalId, Guid roleId, bool keepOneHuman, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (keepOneHuman)
        {
            // Lock every human row of the role in a fixed order, so a concurrent removal waits and then re-reads.
            var humans = new List<Guid>();
            await using (var lockHumans = new NpgsqlCommand(
                "SELECT principal_id FROM role_assignment WHERE role_id = $1 AND principal_kind = 'human' ORDER BY principal_id FOR UPDATE", conn, tx))
            {
                lockHumans.Parameters.Add(Param(roleId));
                await using var reader = await lockHumans.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    humans.Add(reader.GetGuid(0));
            }

            if (humans.Count == 1 && humans[0] == principalId)
                return UnassignResult.WouldRemoveLastHuman;
        }

        await using var delete = new NpgsqlCommand("DELETE FROM role_assignment WHERE principal_id = $1 AND role_id = $2", conn, tx);
        delete.Parameters.Add(Param(principalId));
        delete.Parameters.Add(Param(roleId));
        var removed = await delete.ExecuteNonQueryAsync(ct) > 0;
        await tx.CommitAsync(ct);
        return removed ? UnassignResult.Removed : UnassignResult.NotAssigned;
    }

    public Task<IReadOnlyList<RoleAssignment>> AssignmentsForPrincipalAsync(Guid principalId, CancellationToken ct = default) =>
        QueryAssignmentsAsync("WHERE principal_id = $1", principalId, ct);

    public Task<IReadOnlyList<RoleAssignment>> AssignmentsForRoleAsync(Guid roleId, CancellationToken ct = default) =>
        QueryAssignmentsAsync("WHERE role_id = $1", roleId, ct);

    public async Task<bool> AssignIfNoHumanHoldsAsync(RoleAssignment assignment, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", conn, tx))
        {
            lockCmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = _bootstrapLockKey });
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var exists = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM role_assignment WHERE role_id = $1 AND principal_kind = 'human')", conn, tx))
        {
            exists.Parameters.Add(Param(assignment.RoleId));
            if ((bool)(await exists.ExecuteScalarAsync(ct))!)
                return false;
        }

        await using (var insert = new NpgsqlCommand(_insertAssignmentSql, conn, tx))
        {
            AddAssignment(insert, assignment);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    private const string _insertAssignmentSql =
        "INSERT INTO role_assignment (principal_id, principal_kind, role_id, assigned_by, assigned_at) VALUES ($1,$2,$3,$4,$5)";

    private static void AddAssignment(NpgsqlCommand cmd, RoleAssignment a)
    {
        cmd.Parameters.Add(Param(a.PrincipalId));
        cmd.Parameters.Add(Param(KindToText(a.PrincipalKind)));
        cmd.Parameters.Add(Param(a.RoleId));
        cmd.Parameters.Add(Param(a.AssignedBy));
        cmd.Parameters.Add(Param(a.AssignedAt));
    }

    private async Task<IReadOnlyList<RoleAssignment>> QueryAssignmentsAsync(string where, Guid id, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT principal_id, principal_kind, role_id, assigned_by, assigned_at FROM role_assignment " + where + " ORDER BY assigned_at, principal_id");
        cmd.Parameters.Add(Param(id));
        var list = new List<RoleAssignment>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new RoleAssignment(
                reader.GetGuid(0), KindFromText(reader.GetString(1)), reader.GetGuid(2), reader.GetGuid(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return list;
    }

    private async Task<IReadOnlyList<Role>> QueryRolesAsync(string where, object? arg, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var heads = new List<(Guid Id, string Name, string Description, string? Key, bool Protected, DateTimeOffset Created, DateTimeOffset Updated)>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT role_id, name, description, template_key, protected, created_at, updated_at FROM role " + where + " ORDER BY created_at, role_id", conn, tx))
        {
            AddArg(cmd, arg);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                heads.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetBoolean(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6)));
            }
        }

        var grants = new Dictionary<Guid, List<Grant>>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT g.role_id, g.capability, g.scope_kind, g.scope_id FROM role_grant g WHERE g.role_id IN (SELECT role_id FROM role " + where +
            ") ORDER BY g.capability, g.scope_kind, g.scope_id", conn, tx))
        {
            AddArg(cmd, arg);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var roleId = reader.GetGuid(0);
                if (!grants.TryGetValue(roleId, out var list))
                    grants[roleId] = list = [];
                list.Add(new Grant(new Capability(reader.GetString(1)), new Scope(ScopeFromText(reader.GetString(2)), reader.GetString(3))));
            }
        }

        await tx.CommitAsync(ct);
        return heads
            .Select(h => new Role(h.Id, h.Name, h.Description, h.Key, h.Protected,
                grants.TryGetValue(h.Id, out var g) ? g : [], h.Created, h.Updated))
            .ToList();
    }

    private static void AddArg(NpgsqlCommand cmd, object? arg)
    {
        switch (arg)
        {
            case Guid g:
                cmd.Parameters.Add(Param(g));
                break;
            case string t:
                cmd.Parameters.Add(Param(t));
                break;
        }
    }

    private static async Task InsertGrantsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid roleId, IReadOnlyList<Grant> grants, CancellationToken ct)
    {
        foreach (var grant in grants.Distinct())
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO role_grant (role_id, capability, scope_kind, scope_id) VALUES ($1,$2,$3,$4)", conn, tx);
            cmd.Parameters.Add(Param(roleId));
            cmd.Parameters.Add(Param(grant.Capability.Value));
            cmd.Parameters.Add(Param(ScopeToText(grant.Scope.Kind)));
            cmd.Parameters.Add(Param(grant.Scope.Id));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static NpgsqlParameter<Guid> Param(Guid v) => new NpgsqlParameter<Guid> { TypedValue = v };

    private static NpgsqlParameter<string> Param(string v) => new NpgsqlParameter<string> { TypedValue = v };

    private static NpgsqlParameter<bool> Param(bool v) => new NpgsqlParameter<bool> { TypedValue = v };

    private static NpgsqlParameter<DateTimeOffset> Param(DateTimeOffset v) => new NpgsqlParameter<DateTimeOffset> { TypedValue = v };

    private static string KindToText(PrincipalKind kind) => kind switch
    {
        PrincipalKind.Human => "human",
        PrincipalKind.Agent => "agent",
        PrincipalKind.Service => "service",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static PrincipalKind KindFromText(string text) => text switch
    {
        "human" => PrincipalKind.Human,
        "agent" => PrincipalKind.Agent,
        "service" => PrincipalKind.Service,
        _ => throw new InvalidOperationException($"Unknown principal kind '{text}'."),
    };

    private static string ScopeToText(ScopeKind kind) => kind switch
    {
        ScopeKind.Instance => "instance",
        ScopeKind.Company => "company",
        ScopeKind.Gc => "gc",
        ScopeKind.GcTag => "gc-tag",
        ScopeKind.Module => "module",
        ScopeKind.Repository => "repository",
        ScopeKind.AgentGroup => "agent-group",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ScopeKind ScopeFromText(string text) => text switch
    {
        "instance" => ScopeKind.Instance,
        "company" => ScopeKind.Company,
        "gc" => ScopeKind.Gc,
        "gc-tag" => ScopeKind.GcTag,
        "module" => ScopeKind.Module,
        "repository" => ScopeKind.Repository,
        "agent-group" => ScopeKind.AgentGroup,
        _ => throw new InvalidOperationException($"Unknown scope kind '{text}'."),
    };
}
