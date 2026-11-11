namespace Bazaar.Server.Agents.Turns.Posting;

/// <summary>A GC's active member with the handle an agent may type after "@" to mention them.</summary>
/// <param name="PrincipalId">The stable principal id the handle resolves to.</param>
/// <param name="Handle">The handle, without the "@". Matched ordinal and case-insensitive.</param>
public sealed record GcMentionMember(Guid PrincipalId, string Handle);

/// <summary>The active members of one GC with their handles, and the owner id, as the mention checks need them.</summary>
/// <param name="OwnerId">The owner. Always an available mention target, even when not in <see cref="ActiveMembers"/>.</param>
/// <param name="ActiveMembers">Principals with an active membership row now.</param>
public sealed record GcMentionRoster(Guid OwnerId, IReadOnlyList<GcMentionMember> ActiveMembers);

/// <summary>
/// The seam <see cref="GcAgentPostSink"/> uses to find mention targets for a GC. No implementation is registered in production:
/// the current schema has no principal handle column (see the report that added this seam). Without a directory the sink posts
/// with no mentions.
/// </summary>
public interface IGcMentionDirectory
{
    /// <summary>Loads the active members and their handles for a GC.</summary>
    Task<GcMentionRoster> LoadAsync(Guid gcId, CancellationToken ct);
}
