using System.Data.Common;
using System.Net.Sockets;
using Bazaar.Server.Memory.Access;
using Npgsql;

namespace Bazaar.Server.Projects;

/// <summary>
/// Postgres <see cref="IProjectMembership"/> (docs/design/93-projects-and-membership.md section 4). An agent is a member of a project through a
/// current <c>project_member</c> row, or, unless <see cref="ProjectsOptions.LoansGrantRead"/> is off, through a current <c>org_relation</c>
/// <c>lent_to</c> loan whose <c>to_agent_id</c> is the agent. A loan is never copied, so an ended loan stops granting at the next request. Humans
/// count only through <c>project_member</c>. A cover answers its primary's projects. The owner and maintenance read through
/// <see cref="VaultPathAccess"/> directly and answer empty here. Archived projects still count (read stays). Nothing is cached.
/// </summary>
/// <remarks>Fails closed: an unknown principal, a malformed slug or any database error answers no membership, never all.</remarks>
public sealed class PgProjectMembership(NpgsqlDataSource dataSource, ProjectsOptions options, TimeProvider? clock = null) : IProjectMembership
{
    // One query for both sources. $1 kind, $2 principal id, $3 now, $4 loans count.
    private const string _projectsSql = """
        SELECT p.slug
        FROM project p
        WHERE EXISTS (
                SELECT 1 FROM project_member m
                WHERE m.project_id = p.id AND m.principal_kind = $1 AND m.principal_id = $2
                  AND m.starts_at <= $3 AND (m.ends_at IS NULL OR m.ends_at > $3))
           OR ($4 AND $1 = 'agent' AND EXISTS (
                SELECT 1 FROM org_relation o
                WHERE o.kind = 'lent_to' AND o.project_id = p.id AND o.to_agent_id = $2
                  AND o.starts_at <= $3 AND (o.ends_at IS NULL OR o.ends_at > $3)))
        """;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    public bool IsMember(VaultPrincipal who, string projectSlug)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(projectSlug);

        return ProjectSlug.IsValid(projectSlug) && ProjectsOf(who).Contains(projectSlug);
    }

    /// <inheritdoc />
    public IReadOnlySet<string> ProjectsOf(VaultPrincipal who)
    {
        ArgumentNullException.ThrowIfNull(who);

        var (kind, idText) = who.Kind switch
        {
            VaultPrincipalKind.Agent => ("agent", who.AgentId),
            VaultPrincipalKind.Cover => ("agent", who.PrimaryAgentId),
            VaultPrincipalKind.MemberHuman => ("user", who.MemberId),
            _ => ((string?)null, (string?)null),
        };
        if (kind is null || !Guid.TryParse(idText, out var id) || id == Guid.Empty)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var slugs = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var cmd = dataSource.CreateCommand(_projectsSql);
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = kind });
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
            cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = _clock.GetUtcNow().UtcDateTime });
            cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = options.LoansGrantRead });
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                slugs.Add(reader.GetString(0));
            }
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or SocketException or TimeoutException or IOException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return slugs;
    }
}
