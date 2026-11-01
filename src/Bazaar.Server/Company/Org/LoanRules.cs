// TODO(component): move to the Company component project when PR #125 merges.
using Bazaar.Contracts.Gc;
using Bazaar.Server.Org;

namespace Bazaar.Server.Company.Org;

/// <summary>Who is acting on an org relation. Only the owner and agents may.</summary>
public sealed record OrgActor(AuthorKind Kind, Guid PrincipalId);

/// <summary>The answer of a rule: allowed, or refused with a reason.</summary>
public readonly record struct OrgRuleResult(bool Allowed, string? Reason)
{
    public static OrgRuleResult Allow() => new(true, null);

    public static OrgRuleResult Refuse(string reason) => new(false, reason);
}

/// <summary>
/// Pure rules for starting and ending a loan (doc 24 section 2). A loan is a <c>lent_to</c> relation: the lent agent is the
/// subject and the lending project's lead agent is the object. The rules only decide who may approve; a loan grants the
/// lent agent no access.
/// </summary>
public static class LoanRules
{
    /// <summary>
    /// May <paramref name="actor"/> start a loan of <paramref name="lentAgentId"/> to <paramref name="projectId"/>, led by
    /// <paramref name="leadAgentId"/>? The owner always may. Under <see cref="OrgRelationApprover.ProjectLead"/> the lead may
    /// too, if a live <c>reports_to</c> in that project points at it. Self-loans are refused for everyone.
    /// </summary>
    public static OrgRuleResult MayStart(
        OrgActor actor,
        Guid lentAgentId,
        Guid leadAgentId,
        Guid projectId,
        IReadOnlyList<OrgRelation> projectRelations,
        DateTimeOffset at,
        OrgSettings settings)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(projectRelations);
        ArgumentNullException.ThrowIfNull(settings);

        if (lentAgentId == leadAgentId)
            return OrgRuleResult.Refuse("An agent cannot be lent to itself.");
        return MayApprove(actor, lentAgentId, leadAgentId, projectId, projectRelations, at, settings);
    }

    /// <summary>May <paramref name="actor"/> end <paramref name="loan"/>? Same approvers as starting one.</summary>
    public static OrgRuleResult MayEnd(
        OrgActor actor,
        OrgRelation loan,
        IReadOnlyList<OrgRelation> projectRelations,
        DateTimeOffset at,
        OrgSettings settings)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(loan);
        ArgumentNullException.ThrowIfNull(projectRelations);
        ArgumentNullException.ThrowIfNull(settings);

        if (loan.Kind != OrgRelationKind.LentTo || loan.ProjectId is not { } project)
            return OrgRuleResult.Refuse("Only a project loan can be ended here.");
        return MayApprove(actor, loan.FromAgentId, loan.ToAgentId, project, projectRelations, at, settings);
    }

    private static OrgRuleResult MayApprove(
        OrgActor actor,
        Guid lentAgentId,
        Guid leadAgentId,
        Guid projectId,
        IReadOnlyList<OrgRelation> projectRelations,
        DateTimeOffset at,
        OrgSettings settings)
    {
        switch (actor.Kind)
        {
            case AuthorKind.Owner:
                return OrgRuleResult.Allow();
            case AuthorKind.Agent:
                break;
            default:
                return OrgRuleResult.Refuse("Only the owner or an agent may act on a loan.");
        }

        if (settings.RelationApprover != OrgRelationApprover.ProjectLead)
            return OrgRuleResult.Refuse("org.relation_approver is owner: only the owner approves loans.");
        if (actor.PrincipalId == lentAgentId)
            return OrgRuleResult.Refuse("An agent cannot approve its own loan.");
        if (actor.PrincipalId != leadAgentId)
            return OrgRuleResult.Refuse("Only the project's lead may approve this loan.");

        var leads = projectRelations.Any(r =>
            r.Kind == OrgRelationKind.ReportsTo
            && r.ProjectId == projectId
            && r.ToAgentId == leadAgentId
            && OrgRouting.IsLive(r, at));
        return leads
            ? OrgRuleResult.Allow()
            : OrgRuleResult.Refuse("The agent is not the reports_to of this project.");
    }
}
