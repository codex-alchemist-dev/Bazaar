using Bazaar.Server.Memory.Access;

namespace Bazaar.Server.Vault.Search;

/// <summary>
/// The access rule for Vault reads, search hits and link resolution (doc 18, "Access"). The SQL in
/// <see cref="Data.VaultSearchRepository"/> applies the same rule before ranking, and <see cref="VaultPathAccess"/> takes its
/// project areas from <see cref="ProjectAreaOf"/>. It fails closed: a document no rule names is readable by the owner only.
/// </summary>
public static class VaultAccess
{
    /// <summary>The path prefixes doc 18 makes readable by every agent: <c>company/</c> and the shared areas.</summary>
    public static IReadOnlyList<string> AgentReadablePrefixes { get; } = ["company/", "shared/"];

    /// <summary>
    /// The roots whose second segment names the project an area belongs to (doc 18, Repository table):
    /// <c>projects/&lt;slug&gt;/**</c> and the Digital Twin's <c>twin/&lt;project&gt;/**</c> (doc 25). No other area is
    /// project-scoped by path, so <c>decisions/</c>, <c>drafts/</c>, <c>archive/</c>, <c>hive/</c> and root files are not.
    /// </summary>
    public static IReadOnlyList<string> ProjectAreaRoots { get; } = ["projects/", "twin/"];

    private const string _agentOwnerPrefix = "agent:";

    /// <summary>
    /// The owner reads everything. A document with an <paramref name="documentOwner"/> (an agent's private memory) is
    /// readable by that agent only. A document with no owner is readable by an agent only through an explicit area rule:
    /// <see cref="AgentReadablePrefixes"/>, the agent's own <c>agents/&lt;id&gt;/</c> area, or a project area
    /// (<see cref="ProjectAreaOf"/>) of a project in <see cref="VaultCaller.Projects"/>. Everything else with no owner
    /// (<c>hive/</c>, <c>decisions/</c>, drafts, <c>archive/</c>, root files) is the owner's only. Callers that are neither
    /// the owner nor an agent read nothing, whatever projects they carry.
    /// </summary>
    public static bool CanRead(string path, string? documentOwner, VaultCaller caller)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.IsOwner)
        {
            return true;
        }

        if (caller.AgentOwner is null)
        {
            return false;
        }

        if (documentOwner is not null)
        {
            return string.Equals(documentOwner, caller.AgentOwner, StringComparison.Ordinal);
        }

        foreach (var prefix in AgentReadablePrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (ProjectAreaOf(path) is { } project && caller.Projects.Contains(project))
        {
            return true;
        }

        return caller.AgentOwner.StartsWith(_agentOwnerPrefix, StringComparison.Ordinal)
            && path.StartsWith("agents/" + caller.AgentOwner[_agentOwnerPrefix.Length..] + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// The project slug of a project-scoped path: the second segment of a path under one of <see cref="ProjectAreaRoots"/>,
    /// when the path has a non-empty slug and something after it. Null for every other path. Matching is ordinal.
    /// </summary>
    public static string? ProjectAreaOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        foreach (var root in ProjectAreaRoots)
        {
            if (!path.StartsWith(root, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = path.AsSpan(root.Length);
            var slash = rest.IndexOf('/');
            return slash > 0 && slash < rest.Length - 1 ? rest[..slash].ToString() : null;
        }

        return null;
    }

    /// <summary>
    /// The caller for an agent reading with its own identity. Its projects are resolved now from
    /// <paramref name="membership"/>; with no membership source the set is empty and no project area is readable.
    /// </summary>
    public static VaultCaller ForAgent(Guid agentId, IProjectMembership? membership) =>
        new(IsOwner: false, AgentOwner: VaultOwner.ForAgent(agentId))
        {
            Projects = ProjectsOf(VaultPrincipal.Agent(agentId.ToString("D")), membership),
        };

    /// <summary>
    /// The caller for a cover agent working for <paramref name="primaryAgentId"/>. It keeps its own identity (its own
    /// private memory, never the primary's) and carries the primary's projects, as <see cref="StaticProjectMembership"/>
    /// answers for a cover principal (doc 09, Cover).
    /// </summary>
    public static VaultCaller ForCover(Guid coverAgentId, Guid primaryAgentId, IProjectMembership? membership) =>
        new(IsOwner: false, AgentOwner: VaultOwner.ForAgent(coverAgentId))
        {
            Projects = ProjectsOf(VaultPrincipal.Cover(coverAgentId.ToString("D"), primaryAgentId.ToString("D")), membership),
        };

    // A slug that is not one path segment can never name a project area, so it is dropped rather than failing the request.
    private static HashSet<string> ProjectsOf(VaultPrincipal who, IProjectMembership? membership)
    {
        var projects = new HashSet<string>(StringComparer.Ordinal);
        if (membership is null)
        {
            return projects;
        }

        foreach (var slug in membership.ProjectsOf(who))
        {
            if (!string.IsNullOrEmpty(slug) && !slug.Contains('/', StringComparison.Ordinal))
            {
                projects.Add(slug);
            }
        }

        return projects;
    }
}
