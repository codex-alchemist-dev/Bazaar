using System.Globalization;
using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Npgsql;

namespace Bazaar.Server.Projects;

/// <summary>
/// Postgres write API of the Projects module (docs/design/93-projects-and-membership.md section 5). Every success changes state and appends its
/// audit entry (actor, diff, time) in one transaction, so a crash cannot leave a change with no entry. Refusals change nothing. A write locks the
/// project row, so two writers on one project run one after the other.
/// </summary>
public sealed class PgProjectStore(
    NpgsqlDataSource dataSource,
    ITransactionalAuditLog audit,
    ProjectsOptions options,
    TimeProvider? clock = null) : IProjectStore
{
    private const string _projectColumns = "id, slug, title, state, created_by, created_at, version";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<ProjectWriteResult> CreateAsync(ProjectActor actor, string slug, string title, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        // projects.creators is owner at the floor and may not be widened here.
        if (actor.Kind != "owner")
            return ProjectWriteResult.Refused(ProjectError.Forbidden);
        if (!ProjectSlug.IsValid(slug))
            return ProjectWriteResult.Refused(ProjectError.InvalidSlug);
        if (string.IsNullOrWhiteSpace(title))
            return ProjectWriteResult.Refused(ProjectError.InvalidTitle);

        var now = _clock.GetUtcNow();
        var id = Guid.CreateVersion7();
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        int inserted;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO project (id, slug, title, state, created_by, created_at, version)
            VALUES ($1, $2, $3, 'active', $4, $5, 1)
            ON CONFLICT (slug) DO NOTHING
            """, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = slug });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = title });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = actor.Label });
            cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now.UtcDateTime });
            inserted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (inserted == 0)
            return ProjectWriteResult.Refused(ProjectError.SlugTaken);

        var diff = new JsonObject { ["after"] = new JsonObject { ["slug"] = slug, ["title"] = title, ["state"] = "active" } };
        await AppendAuditAsync(conn, tx, now, actor, "project.created", id, slug, diff, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return ProjectWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<ProjectWriteResult> AddMemberAsync(
        ProjectActor actor, string slug, ProjectPrincipalKind kind, Guid principalId, ProjectRole role, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!ProjectSlug.IsValid(slug))
            return ProjectWriteResult.Refused(ProjectError.InvalidSlug);
        if (principalId == Guid.Empty || !Enum.IsDefined(kind) || !Enum.IsDefined(role))
            return ProjectWriteResult.Refused(ProjectError.InvalidPrincipal);

        var now = _clock.GetUtcNow();
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var project = await LockProjectAsync(conn, tx, slug, ct).ConfigureAwait(false);
        if (project is null)
            return ProjectWriteResult.Refused(ProjectError.NotFound);
        if (!await MayManageMembersAsync(conn, tx, actor, project.Id, now, ct).ConfigureAwait(false))
            return ProjectWriteResult.Refused(ProjectError.Forbidden);
        if (project.State == ProjectState.Archived)
            return ProjectWriteResult.Refused(ProjectError.Archived);

        var existing = await ReadMemberAsync(conn, tx, project.Id, kind, principalId, ct).ConfigureAwait(false);
        if (existing is not null && IsCurrent(existing, now))
            return ProjectWriteResult.Refused(ProjectError.AlreadyMember);

        if (existing is null)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO project_member (project_id, principal_kind, principal_id, role, starts_at, ends_at, added_by, version)
                VALUES ($1, $2, $3, $4, $5, NULL, $6, 1)
                """, conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = project.Id });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = KindName(kind) });
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principalId });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = RoleName(role) });
            cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now.UtcDateTime });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = actor.Label });
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        else
        {
            // Re-adding after an end (or before a future start) resets the one row; the audit log keeps the history.
            await using var cmd = new NpgsqlCommand("""
                UPDATE project_member
                SET role = $4, starts_at = $5, ends_at = NULL, added_by = $6, version = version + 1
                WHERE project_id = $1 AND principal_kind = $2 AND principal_id = $3
                """, conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = project.Id });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = KindName(kind) });
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principalId });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = RoleName(role) });
            cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now.UtcDateTime });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = actor.Label });
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var diff = new JsonObject
        {
            ["before"] = existing is null ? null : MemberJson(existing),
            ["after"] = new JsonObject
            {
                ["kind"] = KindName(kind),
                ["principal"] = principalId.ToString("D"),
                ["role"] = RoleName(role),
                ["starts_at"] = Iso(now),
                ["ends_at"] = null,
            },
        };
        await AppendAuditAsync(conn, tx, now, actor, "project.member_added", project.Id, slug, diff, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return ProjectWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<ProjectWriteResult> EndMembershipAsync(
        ProjectActor actor, string slug, ProjectPrincipalKind kind, Guid principalId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!ProjectSlug.IsValid(slug))
            return ProjectWriteResult.Refused(ProjectError.InvalidSlug);
        if (principalId == Guid.Empty || !Enum.IsDefined(kind))
            return ProjectWriteResult.Refused(ProjectError.InvalidPrincipal);

        var now = _clock.GetUtcNow();
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var project = await LockProjectAsync(conn, tx, slug, ct).ConfigureAwait(false);
        if (project is null)
            return ProjectWriteResult.Refused(ProjectError.NotFound);
        if (!await MayManageMembersAsync(conn, tx, actor, project.Id, now, ct).ConfigureAwait(false))
            return ProjectWriteResult.Refused(ProjectError.Forbidden);

        // Ending only removes access, so it is allowed on an archived project.
        var existing = await ReadMemberAsync(conn, tx, project.Id, kind, principalId, ct).ConfigureAwait(false);
        if (existing is null || !IsCurrent(existing, now))
            return ProjectWriteResult.Refused(ProjectError.NotMember);

        await using (var cmd = new NpgsqlCommand("""
            UPDATE project_member SET ends_at = $4, version = version + 1
            WHERE project_id = $1 AND principal_kind = $2 AND principal_id = $3
            """, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = project.Id });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = KindName(kind) });
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principalId });
            // An end never precedes the start, so a membership that starts later is ended at its own start.
            cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = (now > existing.StartsAt ? now : existing.StartsAt).UtcDateTime });
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var diff = new JsonObject
        {
            ["before"] = MemberJson(existing),
            ["after"] = MemberJson(existing with { EndsAt = now }),
        };
        await AppendAuditAsync(conn, tx, now, actor, "project.member_ended", project.Id, slug, diff, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return ProjectWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<ProjectWriteResult> ArchiveAsync(ProjectActor actor, string slug, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!ProjectSlug.IsValid(slug))
            return ProjectWriteResult.Refused(ProjectError.InvalidSlug);

        var now = _clock.GetUtcNow();
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var project = await LockProjectAsync(conn, tx, slug, ct).ConfigureAwait(false);
        if (project is null)
            return ProjectWriteResult.Refused(ProjectError.NotFound);
        if (!await MayManageMembersAsync(conn, tx, actor, project.Id, now, ct).ConfigureAwait(false))
            return ProjectWriteResult.Refused(ProjectError.Forbidden);
        if (project.State == ProjectState.Archived)
            return ProjectWriteResult.Refused(ProjectError.Archived);

        await using (var cmd = new NpgsqlCommand("UPDATE project SET state = 'archived', version = version + 1 WHERE id = $1", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = project.Id });
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var diff = new JsonObject { ["before"] = new JsonObject { ["state"] = "active" }, ["after"] = new JsonObject { ["state"] = "archived" } };
        await AppendAuditAsync(conn, tx, now, actor, "project.archived", project.Id, slug, diff, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return ProjectWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<ProjectRecord?> GetAsync(string slug, CancellationToken ct = default)
    {
        if (!ProjectSlug.IsValid(slug))
            return null;

        await using var cmd = dataSource.CreateCommand($"SELECT {_projectColumns} FROM project WHERE slug = $1");
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = slug });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadProject(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectMemberRecord>> ListMembersAsync(string slug, CancellationToken ct = default)
    {
        if (!ProjectSlug.IsValid(slug))
            return [];

        await using var cmd = dataSource.CreateCommand("""
            SELECT m.project_id, m.principal_kind, m.principal_id, m.role, m.starts_at, m.ends_at, m.added_by, m.version
            FROM project_member m JOIN project p ON p.id = m.project_id
            WHERE p.slug = $1
            ORDER BY m.starts_at, m.principal_kind, m.principal_id
            """);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = slug });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var members = new List<ProjectMemberRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            members.Add(ReadMember(reader));
        return members;
    }

    // Owner always; otherwise a current lead of this project, unless projects.member_adders is tightened to the owner.
    private async Task<bool> MayManageMembersAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, ProjectActor actor, Guid projectId, DateTimeOffset now, CancellationToken ct)
    {
        if (actor.Kind == "owner")
            return true;
        if (options.MemberAdders == MemberAdders.OwnerOnly || actor.Id is not { } id || id == Guid.Empty)
            return false;

        var kind = actor.Kind == "agent" ? ProjectPrincipalKind.Agent : ProjectPrincipalKind.User;
        var row = await ReadMemberAsync(conn, tx, projectId, kind, id, ct).ConfigureAwait(false);
        return row is { Role: ProjectRole.Lead } && IsCurrent(row, now);
    }

    private static async Task<ProjectRecord?> LockProjectAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string slug, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {_projectColumns} FROM project WHERE slug = $1 FOR UPDATE", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = slug });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadProject(reader) : null;
    }

    private static async Task<ProjectMemberRecord?> ReadMemberAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid projectId, ProjectPrincipalKind kind, Guid principalId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT project_id, principal_kind, principal_id, role, starts_at, ends_at, added_by, version
            FROM project_member WHERE project_id = $1 AND principal_kind = $2 AND principal_id = $3
            """, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = projectId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = KindName(kind) });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principalId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadMember(reader) : null;
    }

    private Task AppendAuditAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, DateTimeOffset now, ProjectActor actor, string kind, Guid projectId, string slug, JsonObject diff, CancellationToken ct)
    {
        var payload = new JsonObject { ["project_id"] = projectId.ToString("D"), ["slug"] = slug, ["actor"] = actor.Label, ["diff"] = diff };
        return audit.AppendInTransactionAsync(conn, tx, new AuditEntry(now, actor.Label, kind, payload.ToJsonString()), ct);
    }

    private static bool IsCurrent(ProjectMemberRecord row, DateTimeOffset now) =>
        row.StartsAt <= now && (row.EndsAt is null || row.EndsAt > now);

    private static JsonObject MemberJson(ProjectMemberRecord row) => new()
    {
        ["kind"] = KindName(row.PrincipalKind),
        ["principal"] = row.PrincipalId.ToString("D"),
        ["role"] = RoleName(row.Role),
        ["starts_at"] = Iso(row.StartsAt),
        ["ends_at"] = row.EndsAt is { } end ? Iso(end) : null,
    };

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static string KindName(ProjectPrincipalKind kind) => kind == ProjectPrincipalKind.Agent ? "agent" : "user";

    private static string RoleName(ProjectRole role) => role == ProjectRole.Lead ? "lead" : "member";

    private static ProjectRecord ReadProject(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3) == "archived" ? ProjectState.Archived : ProjectState.Active,
        r.GetString(4),
        r.GetFieldValue<DateTimeOffset>(5),
        r.GetInt64(6));

    private static ProjectMemberRecord ReadMember(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1) == "agent" ? ProjectPrincipalKind.Agent : ProjectPrincipalKind.User,
        r.GetGuid(2),
        r.GetString(3) == "lead" ? ProjectRole.Lead : ProjectRole.Member,
        r.GetFieldValue<DateTimeOffset>(4),
        r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
        r.GetString(6),
        r.GetInt64(7));
}
