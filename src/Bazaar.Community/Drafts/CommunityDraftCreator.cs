using Bazaar.Community.Gates;
using Bazaar.Community.Profile;
using Bazaar.Contracts.Community;
using Bazaar.Server.Community;

namespace Bazaar.Community.Drafts;

/// <summary>
/// An agent's proposed outbound post, before the disclosure footer. <see cref="Gate"/> carries the platform, kind and the agent's
/// own text (no footer) plus the duplicate-search and example evidence the quality gates read.
/// </summary>
/// <param name="AgentId">Agent that drafted it. Must match the profile.</param>
/// <param name="SponsorId">Human sponsor of that agent.</param>
/// <param name="Action">Outward action the post is.</param>
/// <param name="Profile">Bot profile whose footer is appended.</param>
/// <param name="Gate">The draft as the quality gates read it; its body is the agent text without the footer.</param>
/// <param name="Target">Target URL or <c>owner/repo</c> reference.</param>
/// <param name="Title">Title for an issue or PR; null for a comment.</param>
/// <param name="HeadBranch">Head branch for a PR; null otherwise.</param>
/// <param name="BaseBranch">Base branch for a PR; null otherwise.</param>
/// <param name="TaintSourcesJson">JSON array of taint sources for the action log; "[]" when none.</param>
public sealed record CommunityDraftProposal(
    string AgentId,
    string SponsorId,
    CommunityAction Action,
    BotProfile Profile,
    DraftPost Gate,
    string Target,
    string? Title = null,
    string? HeadBranch = null,
    string? BaseBranch = null,
    string TaintSourcesJson = "[]");

/// <summary>What <see cref="ICommunityDraftCreator.CreateAsync"/> reports. <see cref="Card"/> is set only when <see cref="Created"/>.</summary>
/// <param name="Created">True when the draft was stored as Pending (even with failed gates: the owner sees them and may edit or deny).</param>
/// <param name="Code">Short snake_case reason; "ok" when created.</param>
/// <param name="Card">The review card for the new draft.</param>
public sealed record CommunityDraftCreateResult(bool Created, string Code, DraftReviewCardDto? Card = null);

/// <summary>Turns an agent's proposed post into a stored, pending draft awaiting the sponsor's signed decision (doc 36 work package 8).</summary>
public interface ICommunityDraftCreator
{
    /// <summary>Validates the proposal, appends the footer, runs the gates, binds the approval hash and stores the draft.</summary>
    Task<CommunityDraftCreateResult> CreateAsync(CommunityDraftProposal proposal, CancellationToken ct = default);
}

/// <inheritdoc cref="ICommunityDraftCreator" />
public sealed class CommunityDraftCreator(
    ICommunityDraftStore drafts,
    ICommunityPolicySource policies,
    CommunityDraftDecisionHandler handler,
    DisclosureOptions disclosure,
    TimeProvider? clock = null,
    CommunityDraftOptions? options = null) : ICommunityDraftCreator
{
    private static readonly HashSet<CommunityAction> _postActions =
        [CommunityAction.ReplyApprovedThread, CommunityAction.NewPost, CommunityAction.NewIssue, CommunityAction.NewPullRequest, CommunityAction.EditOwn];

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly CommunityDraftOptions _options = options ?? new CommunityDraftOptions();

    /// <inheritdoc />
    public async Task<CommunityDraftCreateResult> CreateAsync(CommunityDraftProposal proposal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var invalid = Check(proposal);
        if (invalid is not null) return new CommunityDraftCreateResult(false, invalid);

        var now = _clock.GetUtcNow();
        var agentText = proposal.Gate.Body;
        var body = BotProfileBuilder.AppendFooter(agentText, proposal.Profile, disclosure);
        var post = new OutboundPost(
            Guid.CreateVersion7(now), proposal.Gate.Platform, proposal.Gate.Kind, proposal.Target, proposal.Title,
            body, DraftApprovalHash.BodySha256Hex(body), proposal.HeadBranch, proposal.BaseBranch);

        var policy = await policies.FindAsync(post.Platform, ct).ConfigureAwait(false);
        // Gate input is the agent's text without the footer: the footer carries the sponsor's disclosed contact, which the
        // personal-data gate would flag on every post. The same rule applies to an edited body (the decision handler).
        var gates = QualityGates.Evaluate(proposal.Gate, policy, DateOnly.FromDateTime(now.UtcDateTime));

        var draft = new CommunityDraft(
            post.DraftId, proposal.AgentId, proposal.SponsorId, proposal.Action, post, proposal.Gate, proposal.Profile,
            Guid.CreateVersion7(now), DraftApprovalHash.Compute(post), now + _options.Lifetime, gates.Failures,
            CommunityDraftStatus.Pending, proposal.TaintSourcesJson);

        if (!await drafts.TryAddAsync(draft, ct).ConfigureAwait(false))
            return new CommunityDraftCreateResult(false, "duplicate_draft");
        return new CommunityDraftCreateResult(true, "ok", handler.ToCard(draft));
    }

    private string? Check(CommunityDraftProposal p)
    {
        if (p.Profile is null || p.Gate is null) return "bad_request";
        if (string.IsNullOrWhiteSpace(p.AgentId) || string.IsNullOrWhiteSpace(p.SponsorId) || string.IsNullOrWhiteSpace(p.Target)
            || string.IsNullOrWhiteSpace(p.Gate.Platform) || string.IsNullOrWhiteSpace(p.Gate.Kind))
            return "bad_request";
        if (!_postActions.Contains(p.Action)) return "unsupported_action";
        if (string.IsNullOrWhiteSpace(p.Gate.Body) || p.Gate.Body.Length > _options.MaxEditedBodyChars) return "bad_body";
        if (BotProfileBuilder.Validate(p.Profile).Count > 0
            || !string.Equals(p.Profile.AgentId, p.AgentId, StringComparison.Ordinal)
            || !string.Equals(p.Profile.Platform, p.Gate.Platform, StringComparison.OrdinalIgnoreCase))
            return "bad_profile";
        return null;
    }
}
