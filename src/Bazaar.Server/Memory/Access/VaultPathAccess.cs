namespace Bazaar.Server.Memory.Access;

/// <summary>
/// The path rules of docs/design/08-memory-system.md, section 8 and docs/design/18-vault.md. Anything not listed is denied.
/// <list type="bullet">
/// <item><c>agents/&lt;agent&gt;/memory/**</c>: the agent itself, the owner (logged) and maintenance. Cover never, including its primary's.</item>
/// <item><c>agents/&lt;agent&gt;/**</c> (other files, such as the profile): the owner and that agent only.</item>
/// <item><c>projects/&lt;slug&gt;/**</c> and <c>twin/&lt;slug&gt;/**</c>: owner, maintenance, and members of the project (agents,
/// cover of a member primary, member humans). Which paths are project areas, and of which project, comes from
/// <see cref="Vault.Search.VaultAccess.ProjectAreaOf"/>, the same source Vault search, link expansion and moves use.</item>
/// <item><c>company/memory/**</c>: owner, maintenance and all agents, including cover.</item>
/// <item><c>hive/**</c>: owner and maintenance; an agent or cover only when it is a member of one of the copy's target
/// projects, as <see cref="IHiveCopyTargets"/> reports them (doc 08, section 6). Without that source every agent is
/// denied. A copy still in its review window is not in the Vault at all.</item>
/// </list>
/// The handover note has no path convention in the design docs, so cover does not read it through these rules.
/// Matching is ordinal and case-sensitive.
/// </summary>
public sealed class VaultPathAccess : IVaultAccess
{
    private readonly IProjectMembership _membership;
    private readonly IHiveCopyTargets? _hiveTargets;

    /// <param name="membership">Project membership, consulted for project areas and Hive target projects.</param>
    /// <param name="hiveTargets">
    /// The target projects of each Hive copy. Null means no grant data is available, and agents are denied all of <c>hive/**</c>.
    /// </param>
    public VaultPathAccess(IProjectMembership membership, IHiveCopyTargets? hiveTargets = null)
    {
        ArgumentNullException.ThrowIfNull(membership);
        _membership = membership;
        _hiveTargets = hiveTargets;
    }

    /// <inheritdoc />
    public bool CanRead(VaultPrincipal who, VaultPath path) => Explain(who, path).Allowed;

    /// <inheritdoc />
    public IReadOnlyList<VaultPath> FilterVisible(VaultPrincipal who, IEnumerable<VaultPath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var visible = new List<VaultPath>();
        foreach (var path in paths)
        {
            if (CanRead(who, path))
            {
                visible.Add(path);
            }
        }

        return visible;
    }

    /// <inheritdoc />
    public bool CanResolveLink(VaultPrincipal who, VaultPath fromPath, VaultPath toPath)
    {
        ArgumentNullException.ThrowIfNull(fromPath);
        return CanRead(who, toPath);
    }

    /// <inheritdoc />
    public AccessDecision Explain(VaultPrincipal who, VaultPath path)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(path);

        if (Vault.Search.VaultAccess.ProjectAreaOf(path.Value) is { } projectSlug)
        {
            return ExplainProjectArea(who, projectSlug);
        }

        var segments = path.Segments;
        return segments[0] switch
        {
            "agents" when segments.Count >= 3 => ExplainAgentArea(who, segments),
            "company" when segments.Count >= 3 && segments[1] == "memory" => ExplainSharedArea(who, "company/memory"),
            "hive" when segments.Count >= 2 => ExplainHiveArea(who, path),
            _ => AccessDecision.Deny("no rule allows this path; denied by default"),
        };
    }

    private static AccessDecision ExplainAgentArea(VaultPrincipal who, IReadOnlyList<string> segments)
    {
        var agentId = segments[1];
        var isPrivate = segments.Count >= 4 && segments[2] == "memory";

        switch (who.Kind)
        {
            case VaultPrincipalKind.Owner:
                return isPrivate
                    ? AccessDecision.Allow("owner reads all; private read of another agent is logged", logged: true)
                    : AccessDecision.Allow("owner reads all");

            case VaultPrincipalKind.Agent when who.AgentId == agentId:
            case VaultPrincipalKind.Cover when who.AgentId == agentId:
                return AccessDecision.Allow("agent reads its own area");

            case VaultPrincipalKind.Maintenance when isPrivate:
                return AccessDecision.Allow("maintenance (consolidation) may read private memory");

            case VaultPrincipalKind.Cover:
                return AccessDecision.Deny(isPrivate
                    ? "cover never reads its primary's private memory"
                    : "cover reads shared areas only; no handover path convention exists");

            default:
                return isPrivate
                    ? AccessDecision.Deny("private memory is readable by its agent, the owner and maintenance only")
                    : AccessDecision.Deny("agent area is readable by the owner and that agent only");
        }
    }

    private AccessDecision ExplainProjectArea(VaultPrincipal who, string projectSlug)
    {
        if (who.Kind is VaultPrincipalKind.Owner or VaultPrincipalKind.Maintenance)
        {
            return AccessDecision.Allow("owner and maintenance read project areas");
        }

        if (who.Kind is VaultPrincipalKind.Agent or VaultPrincipalKind.Cover or VaultPrincipalKind.MemberHuman
            && _membership.IsMember(who, projectSlug))
        {
            return AccessDecision.Allow("project member reads shared project areas");
        }

        return AccessDecision.Deny("not a member of this project, or the project is unknown");
    }

    private AccessDecision ExplainHiveArea(VaultPrincipal who, VaultPath path)
    {
        if (who.Kind is VaultPrincipalKind.Owner or VaultPrincipalKind.Maintenance)
        {
            return AccessDecision.Allow("owner and maintenance read hive copies");
        }

        if (who.Kind is not (VaultPrincipalKind.Agent or VaultPrincipalKind.Cover))
        {
            return AccessDecision.Deny("hive copies are readable by the owner, maintenance and agents in a target project only");
        }

        if (_hiveTargets is null)
        {
            return AccessDecision.Deny("no Hive grant data is available; hive copies are denied to agents by default");
        }

        foreach (var target in _hiveTargets.TargetProjectsOf(path))
        {
            if (_membership.IsMember(who, target))
            {
                return AccessDecision.Allow("member of a target project of this hive copy");
            }
        }

        return AccessDecision.Deny("not a member of any target project of this hive copy");
    }

    private static AccessDecision ExplainSharedArea(VaultPrincipal who, string area) =>
        who.Kind is VaultPrincipalKind.Owner or VaultPrincipalKind.Maintenance
            or VaultPrincipalKind.Agent or VaultPrincipalKind.Cover
            ? AccessDecision.Allow($"{area} is readable by the owner, maintenance and agents")
            : AccessDecision.Deny($"{area} is readable by the owner, maintenance and agents only");
}
