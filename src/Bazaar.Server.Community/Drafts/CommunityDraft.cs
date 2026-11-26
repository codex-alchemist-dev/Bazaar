using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Community;
using Bazaar.Server.Community.Gates;
using Bazaar.Server.Community.Profile;

namespace Bazaar.Server.Community.Drafts;

/// <summary>Where a draft stands. Only <see cref="Pending"/> can still be posted.</summary>
public enum CommunityDraftStatus
{
    /// <summary>Waiting for the sponsor's signed decision. Posts wait here while the sponsor is offline, until the approval expires.</summary>
    Pending,

    /// <summary>An approve was accepted and the adapter call is in flight. A second approve gets a conflict, not a second post.</summary>
    Posting,

    /// <summary>The adapter returned a receipt. Terminal.</summary>
    Posted,

    /// <summary>Denied by the owner, or by the server on a body-hash mismatch or failed gates. Terminal.</summary>
    Denied,

    /// <summary>The owner edited the body: this draft is denied and a new draft awaits its own approval. Terminal.</summary>
    Edited,

    /// <summary>The platform refused the post for good (not retryable). Terminal.</summary>
    Failed,

    /// <summary>The approval ran out before a decision arrived. Terminal.</summary>
    Expired,
}

/// <summary>
/// One outbound community draft awaiting, or past, the sponsor's decision (doc 36 "Data and interfaces", doc 15 approval flow).
/// The approval is bound to <see cref="ApprovalHash"/>, which covers every field of <see cref="Post"/>, so a changed body, target
/// or title voids it. Holds per-person content: never log it.
/// </summary>
/// <param name="DraftId">Draft id; the idempotency scope for the post.</param>
/// <param name="AgentId">Agent that drafted it.</param>
/// <param name="SponsorId">Human sponsor of that agent.</param>
/// <param name="Action">Outward action the post is, for the action log and quotas.</param>
/// <param name="Post">The exact post, as it will go to the adapter.</param>
/// <param name="GateInput">The draft as the quality gates read it. Its body is the agent's text without the disclosure footer, which carries the sponsor's contact and would trip the personal-data gate.</param>
/// <param name="Profile">Bot profile used to append the disclosure footer to an edited body.</param>
/// <param name="ApprovalId">The approval this draft is bound to.</param>
/// <param name="ApprovalHash">The hash the sponsor's device signs for this draft. Built by <see cref="DraftApprovalHash.Compute"/>.</param>
/// <param name="ApprovalExpiresAt">The approval is void at or after this time. Also part of the signed message.</param>
/// <param name="GateFailures">Failed quality gates. Any entry blocks approval.</param>
/// <param name="Status">Current status.</param>
/// <param name="TaintSourcesJson">JSON array of taint sources for the action log; "[]" when none.</param>
/// <param name="ExternalId">Platform id once posted.</param>
/// <param name="Url">Public URL once posted.</param>
/// <param name="Reused">True when the platform already had this draft's item.</param>
/// <param name="SupersededBy">The new draft that replaced this one when <see cref="Status"/> is <see cref="CommunityDraftStatus.Edited"/>.</param>
public sealed record CommunityDraft(
    Guid DraftId,
    string AgentId,
    string SponsorId,
    CommunityAction Action,
    OutboundPost Post,
    DraftPost GateInput,
    BotProfile Profile,
    Guid ApprovalId,
    byte[] ApprovalHash,
    DateTimeOffset ApprovalExpiresAt,
    IReadOnlyList<GateFailure> GateFailures,
    CommunityDraftStatus Status = CommunityDraftStatus.Pending,
    string TaintSourcesJson = "[]",
    string? ExternalId = null,
    string? Url = null,
    bool Reused = false,
    Guid? SupersededBy = null);

/// <summary>
/// Persistence seam for drafts. <see cref="NpgsqlCommunityDraftStore"/> is the production implementation over the
/// <c>community_draft</c> table (migration community/0005). Every state change is a compare-and-set on the status, so two
/// concurrent decisions cannot both win.
/// </summary>
public interface ICommunityDraftStore
{
    /// <summary>The draft, or null when none has this id.</summary>
    Task<CommunityDraft?> GetAsync(Guid draftId, CancellationToken ct = default);

    /// <summary>Replaces the draft only if its stored status is still <paramref name="expected"/>. False when another change got there first.</summary>
    Task<bool> TryReplaceAsync(Guid draftId, CommunityDraftStatus expected, CommunityDraft updated, CancellationToken ct = default);

    /// <summary>
    /// Atomically moves the original from Pending to Edited (with <see cref="CommunityDraft.SupersededBy"/> set to the new draft's id)
    /// and inserts <paramref name="replacement"/>. False, with nothing written, when the original is no longer Pending.
    /// </summary>
    Task<bool> TrySupersedeAsync(Guid originalId, CommunityDraft replacement, CancellationToken ct = default);

    /// <summary>Stores a new draft. False, with nothing written, when a draft with this id already exists.</summary>
    Task<bool> TryAddAsync(CommunityDraft draft, CancellationToken ct = default);

    /// <summary>Pending drafts whose approval is still open at <paramref name="openAt"/>, newest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<CommunityDraft>> ListPendingAsync(DateTimeOffset openAt, int limit, CancellationToken ct = default);

    /// <summary>
    /// [PROPOSED] Moves every Pending draft whose approval expired at or before <paramref name="now"/> to Expired, in one compare-and-set
    /// statement, and returns the ids it changed. A draft another change got to first is not in the result. Rows are never deleted.
    /// </summary>
    Task<IReadOnlyList<Guid>> ExpireDueAsync(DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>Finds a platform's policy record for the quality gates. The host binds it over <see cref="Policy.PlatformPolicyLoader"/>.</summary>
public interface ICommunityPolicySource
{
    /// <summary>The policy for a platform key, or null when none is known.</summary>
    Task<PlatformPolicy?> FindAsync(string platform, CancellationToken ct = default);
}

/// <summary>The hash a draft's approval is bound to: SHA-256 over every outward field of the post, each length-prefixed.</summary>
public static class DraftApprovalHash
{
    /// <summary>Computes the hash. Any change to the body, target, title or branches changes it, which voids the approval.</summary>
    public static byte[] Compute(OutboundPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        var sb = new StringBuilder("bazaar.community.draft.v1;");
        Append(sb, post.DraftId.ToString("D"));
        Append(sb, post.Platform);
        Append(sb, post.Kind);
        Append(sb, post.Target);
        Append(sb, post.Title);
        Append(sb, post.Body);
        Append(sb, post.HeadBranch);
        Append(sb, post.BaseBranch);
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>Lowercase hex SHA-256 of a body, the form <see cref="OutboundPost.BodySha256"/> carries.</summary>
    public static string BodySha256Hex(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private static void Append(StringBuilder sb, string? value) =>
        sb.Append(value is null ? "-" : value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value).Append(';');
}
