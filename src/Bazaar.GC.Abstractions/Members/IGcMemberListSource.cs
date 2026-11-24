namespace Bazaar.Server.Gc.Members;

/// <summary>One GC a principal is an active member of (docs/design/02, GC list).</summary>
/// <param name="GcId">The group chat.</param>
/// <param name="Title">The stored title.</param>
/// <param name="TypeKey">The stored type key (<c>general</c>, <c>dm</c>, <c>thinking</c>, and so on).</param>
/// <param name="HeadSeq">The last assigned seq.</param>
/// <param name="Archived">True when the GC is archived.</param>
/// <param name="MemberCount">Active members.</param>
/// <param name="LastActivityTs">When the last log entry was written; null for a GC with no entries.</param>
public sealed record GcMemberListing(
    Guid GcId, string Title, string TypeKey, long HeadSeq, bool Archived, int MemberCount, DateTimeOffset? LastActivityTs);

/// <summary>
/// Lists the GCs a principal belongs to. Read only. It answers from membership rows alone (an active row, no <c>left_seq</c>); it
/// does not apply DM or Thinking GC access grants, which are not memberships, and it never lists a GC the principal is not in.
/// </summary>
public interface IGcMemberListSource
{
    /// <summary>
    /// The principal's active memberships, most recently active first (ties by GC id). Archived GCs are left out unless
    /// <paramref name="includeArchived"/> is true. Thinking GCs are returned; the caller filters them.
    /// </summary>
    Task<IReadOnlyList<GcMemberListing>> ListAsync(Guid principalId, bool includeArchived, CancellationToken ct);
}
