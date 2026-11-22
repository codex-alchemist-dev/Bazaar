namespace Bazaar.Client.Org;

/// <summary>A project the Org view can scope to.</summary>
public sealed record OrgProjectSummary(string Id, string Name);

/// <summary>An agent shown in the Org view.</summary>
public sealed record OrgAgentSummary(string Id, string Name);

/// <summary>An immutable read of the Org graph: projects, agents and edges. Edges may name agents missing from <see cref="Agents"/>.</summary>
public sealed record OrgSnapshot(
    IReadOnlyList<OrgProjectSummary> Projects,
    IReadOnlyList<OrgAgentSummary> Agents,
    IReadOnlyList<OrgEdgeSummary> Edges);
