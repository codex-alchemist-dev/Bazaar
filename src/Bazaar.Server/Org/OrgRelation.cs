namespace Bazaar.Server.Org;

/// <summary>The two stored org relation kinds (doc 24 section 2). The text values are the 0018 CHECK constraint's.</summary>
public enum OrgRelationKind
{
    /// <summary>The subject reports to the object. Routes approvals and escalation only; grants no authority.</summary>
    ReportsTo,

    /// <summary>The subject is lent to the object's project. Adds the subject to that project's member list while live.</summary>
    LentTo,
}

/// <summary>A stored org relation (table <c>org_relation</c>, migration 0018). A null <see cref="ProjectId"/> means company-wide.</summary>
public sealed record OrgRelation(
    Guid Id,
    OrgRelationKind Kind,
    Guid FromAgentId,
    Guid ToAgentId,
    Guid? ProjectId,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    Guid CreatedBy,
    long Version);

/// <summary>The values a new relation is written with. Ids are supplied by the caller, as for interrupt frames.</summary>
public sealed record NewOrgRelation(
    Guid Id,
    OrgRelationKind Kind,
    Guid FromAgentId,
    Guid ToAgentId,
    Guid? ProjectId,
    DateTimeOffset StartsAt,
    Guid CreatedBy);
