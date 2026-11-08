namespace Bazaar.Server.Council.Service;

/// <summary>Kind of GC member, as far as the Council cares.</summary>
public enum GcMemberKind
{
    /// <summary>An agent member. Eligible to vote unless excluded.</summary>
    Agent,

    /// <summary>The owner. Votes only when council.owner_votes is set.</summary>
    Owner,
}

/// <summary>A GC member at the time of the query.</summary>
/// <param name="MemberId">The agent or owner id.</param>
/// <param name="Kind">Agent or owner.</param>
public sealed record GcMember(string MemberId, GcMemberKind Kind);

/// <summary>
/// Port to GC membership. The GC service (doc 01) provides it; the Council only reads the current members.
/// Work-life absence (doc 09) is not checked here; that belongs to the eligibility rules (WP5).
/// </summary>
public interface IGcMembership
{
    /// <summary>Lists the current members of a GC.</summary>
    Task<IReadOnlyList<GcMember>> ListMembersAsync(Guid gcId, CancellationToken ct);
}
