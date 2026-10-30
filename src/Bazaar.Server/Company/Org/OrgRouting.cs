// TODO(component): move to the Company component project when PR #125 merges.
using Bazaar.Server.Org;

namespace Bazaar.Server.Company.Org;

/// <summary>
/// What to route: the agent raising the item, the item's scope (null project means a company item), the relations to
/// read and the moment that decides which of them are live.
/// </summary>
public sealed record OrgRouteRequest(
    IReadOnlyList<OrgRelation> Relations,
    Guid AgentId,
    Guid? ProjectId,
    DateTimeOffset At,
    OrgSettings Settings);

/// <summary>Where an item goes: an agent, or the owner (<see cref="ToOwner"/>, with a null <see cref="AgentId"/>).</summary>
public sealed record OrgRouteResult(Guid? AgentId, bool ToOwner, string Reason)
{
    internal static OrgRouteResult Agent(Guid id, string reason) => new(id, false, reason);

    internal static OrgRouteResult Owner(string reason) => new(null, true, reason);
}

/// <summary>
/// The org routing function (doc 24 section 2). Pure: no database, no clock. It only names an approval route; it grants no
/// authority to the agent it returns.
/// </summary>
public static class OrgRouting
{
    /// <summary>Routes one item. A project item goes to the agent's live <c>reports_to</c> in that scope, else to the owner.</summary>
    public static OrgRouteResult Route(OrgRouteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Relations);
        ArgumentNullException.ThrowIfNull(request.Settings);

        if (request.ProjectId is not { } project)
            return OrgRouteResult.Owner("Company items go to the owner.");

        if (request.Settings.ProjectItemRoute == OrgProjectItemRoute.Owner)
            return OrgRouteResult.Owner("org.project_item_route is owner.");

        var edge = request.Relations.FirstOrDefault(r =>
            r.Kind == OrgRelationKind.ReportsTo
            && r.FromAgentId == request.AgentId
            && r.ProjectId == project
            && IsLive(r, request.At));

        return edge is null
            ? OrgRouteResult.Owner("The agent has no live reports_to in this project, so the item goes to the owner.")
            : OrgRouteResult.Agent(edge.ToAgentId, "The agent's reports_to in this project.");
    }

    /// <summary>True when the relation has started and not yet ended at <paramref name="at"/>.</summary>
    internal static bool IsLive(OrgRelation relation, DateTimeOffset at) =>
        relation.StartsAt <= at && (relation.EndsAt is null || relation.EndsAt > at);
}
