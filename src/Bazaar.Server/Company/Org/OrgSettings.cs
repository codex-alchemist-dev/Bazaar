// TODO(component): move to the Company component project when PR #125 merges.
namespace Bazaar.Server.Company.Org;

/// <summary>Who approves loans and <c>reports_to</c> edits (<c>org.relation_approver</c>, doc 24 section 2).</summary>
public enum OrgRelationApprover
{
    /// <summary>Only the owner. The shipped default.</summary>
    Owner,

    /// <summary>The owner, or the project's lead (an agent a live <c>reports_to</c> in that project points at).</summary>
    ProjectLead,
}

/// <summary>Where a project item goes (<c>org.project_item_route</c>, doc 24 section 2).</summary>
public enum OrgProjectItemRoute
{
    /// <summary>To the agent's live <c>reports_to</c> in the project scope. The shipped default.</summary>
    ProjectReportsTo,

    /// <summary>To the owner, whatever the graph says.</summary>
    Owner,
}

/// <summary>
/// The two org settings, passed in by the caller. Settings only choose an approval route; org edges grant no authority
/// (doc 24, No authority), so neither value widens what an agent may do.
/// </summary>
public sealed record OrgSettings(
    OrgRelationApprover RelationApprover = OrgRelationApprover.Owner,
    OrgProjectItemRoute ProjectItemRoute = OrgProjectItemRoute.ProjectReportsTo)
{
    /// <summary>The shipped defaults: owner approves, project items follow <c>reports_to</c>.</summary>
    public static OrgSettings Default { get; } = new();
}
