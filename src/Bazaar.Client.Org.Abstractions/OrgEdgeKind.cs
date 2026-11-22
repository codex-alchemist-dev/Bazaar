namespace Bazaar.Client.Org;

/// <summary>The v1 Org edge kinds (doc 24 section 2). Neither kind grants authority.</summary>
public enum OrgEdgeKind
{
    /// <summary>Approval routing and escalation only.</summary>
    ReportsTo,

    /// <summary>A project borrows an agent for a period.</summary>
    LentTo,
}
