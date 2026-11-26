using System.Diagnostics.CodeAnalysis;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Gc.Members;

/// <summary>
/// Read and write rules for one GC (docs/design/01-gc-data-model.md: Membership and permissions, Thinking GCs,
/// Access loss). Pure: callers load the header, the membership rows and the approved access principals, then ask here.
/// </summary>
/// <remarks>
/// Approved access principals are the principals whose access request for this GC is approved and not
/// revoked. Only DM and Thinking GCs take requests, so the set is consulted only for those types. An approval
/// grants read of the whole log (<see cref="AllEvents"/>), not a window from the approval onward. An approval
/// never grants write.
/// </remarks>
public static class GcAccessPolicy
{
    /// <summary>Returned by <see cref="ReadableUpToSeq"/> when the viewer may read every event.</summary>
    public const long AllEvents = long.MaxValue;

    /// <summary>
    /// The highest event seq the viewer may read, or null when the viewer has no read access.
    /// A former member gets its <see cref="GcMemberRecord.LeftSeq"/>: the removal event is readable, later events are not.
    /// </summary>
    public static long? ReadableUpToSeq(
        GcRecord gc,
        IReadOnlyCollection<GcMemberRecord> members,
        GcViewer viewer,
        IReadOnlySet<Guid> approvedAccessPrincipals)
    {
        ArgumentNullException.ThrowIfNull(gc);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(approvedAccessPrincipals);

        if (viewer.Kind == PrincipalKind.Owner)
        {
            return AllEvents;
        }

        if (GcTypeKeys.IsThinking(gc))
        {
            // The owning agent reads its own Thinking GC by its member row; everyone else needs an approval.
            if (ThinkingGcRules.OwningAgentId(gc, members) == viewer.PrincipalId)
            {
                return MemberReadUpTo(gc, members, viewer.PrincipalId);
            }

            return approvedAccessPrincipals.Contains(viewer.PrincipalId) ? AllEvents : null;
        }

        if (GcTypeKeys.IsDm(gc) && approvedAccessPrincipals.Contains(viewer.PrincipalId))
        {
            return AllEvents;
        }

        return MemberReadUpTo(gc, members, viewer.PrincipalId);
    }

    /// <summary>
    /// True when the viewer may write new events to the GC. Archived GCs are read-only for everyone, the owner included.
    /// Thinking GCs accept writes only from their owning agent and the owner.
    /// </summary>
    public static bool CanWrite(GcRecord gc, IReadOnlyCollection<GcMemberRecord> members, GcViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(gc);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(viewer);

        if (gc.ArchivedAt is not null)
        {
            return false;
        }

        if (viewer.Kind == PrincipalKind.Owner)
        {
            return true;
        }

        if (GcTypeKeys.IsThinking(gc) && ThinkingGcRules.OwningAgentId(gc, members) != viewer.PrincipalId)
        {
            return false;
        }

        return IsActiveMemberWithRole(gc, members, viewer.PrincipalId, GcMemberRole.Member);
    }

    /// <summary>The latest membership row for this principal in this GC (highest <see cref="GcMemberRecord.JoinedSeq"/>), or null.</summary>
    public static GcMemberRecord? CurrentRow(GcRecord gc, IReadOnlyCollection<GcMemberRecord> members, Guid principalId)
    {
        return members
            .Where(m => m.GcId == gc.GcId && m.PrincipalId == principalId)
            .OrderByDescending(m => m.JoinedSeq)
            .FirstOrDefault();
    }

    /// <summary>True when the row exists and has not left.</summary>
    public static bool IsActive([NotNullWhen(true)] GcMemberRecord? row) => row is { LeftSeq: null };

    /// <summary>True when the principal has an active row with the given role.</summary>
    public static bool IsActiveMemberWithRole(GcRecord gc, IReadOnlyCollection<GcMemberRecord> members, Guid principalId, GcMemberRole role)
    {
        var row = CurrentRow(gc, members, principalId);
        return IsActive(row) && row.Role == role;
    }

    /// <summary>Number of distinct principals with an active row.</summary>
    public static int ActiveCount(GcRecord gc, IReadOnlyCollection<GcMemberRecord> members)
    {
        return members
            .Where(m => m.GcId == gc.GcId && m.LeftSeq is null)
            .Select(m => m.PrincipalId)
            .Distinct()
            .Count();
    }

    private static long? MemberReadUpTo(GcRecord gc, IReadOnlyCollection<GcMemberRecord> members, Guid principalId)
    {
        var row = CurrentRow(gc, members, principalId);
        if (row is null)
        {
            return null;
        }

        return row.LeftSeq ?? AllEvents;
    }
}
