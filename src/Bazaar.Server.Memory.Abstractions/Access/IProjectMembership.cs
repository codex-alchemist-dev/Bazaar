namespace Bazaar.Server.Memory.Access;

/// <summary>Answers whether a principal belongs to a project. Unknown projects and principals are not members.</summary>
public interface IProjectMembership
{
    /// <summary>True when <paramref name="who"/> is a member of the project with slug <paramref name="projectSlug"/>.</summary>
    bool IsMember(VaultPrincipal who, string projectSlug);
}

/// <summary>The members of one project: agent ids and human member ids.</summary>
/// <param name="AgentIds">Agents on the project.</param>
/// <param name="MemberIds">Co-member humans on the project (not the owner).</param>
public sealed record ProjectMembers(IReadOnlySet<string> AgentIds, IReadOnlySet<string> MemberIds);

/// <summary>
/// Membership from a fixed map of project slug to members. A cover principal is a member when its primary agent is,
/// because the cover reads shared project areas for the primary (docs/design/09-work-life-simulation.md, Cover).
/// Owner and maintenance are not looked up here; <see cref="VaultPathAccess"/> allows them directly.
/// </summary>
public sealed class StaticProjectMembership : IProjectMembership
{
    private readonly IReadOnlyDictionary<string, ProjectMembers> _projects;

    /// <param name="projects">Project slug to members. Slugs are matched ordinally.</param>
    public StaticProjectMembership(IReadOnlyDictionary<string, ProjectMembers> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        _projects = projects;
    }

    /// <inheritdoc />
    public bool IsMember(VaultPrincipal who, string projectSlug)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(projectSlug);

        if (!_projects.TryGetValue(projectSlug, out var members))
        {
            return false;
        }

        return who.Kind switch
        {
            VaultPrincipalKind.Agent => members.AgentIds.Contains(who.AgentId!),
            VaultPrincipalKind.Cover => members.AgentIds.Contains(who.PrimaryAgentId!),
            VaultPrincipalKind.MemberHuman => members.MemberIds.Contains(who.MemberId!),
            _ => false,
        };
    }
}
