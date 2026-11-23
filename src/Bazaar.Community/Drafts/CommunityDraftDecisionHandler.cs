using System.Text;
using System.Text.Json;
using Bazaar.Approvals;
using Bazaar.Community.Actions;
using Bazaar.Community.Gates;
using Bazaar.Community.Profile;
using Bazaar.Contracts.Approvals;
using Bazaar.Contracts.Community;
using Bazaar.Server.Community;
using Bazaar.Server.Security.Approvals;

namespace Bazaar.Community.Drafts;

/// <summary>Operator settings for draft decisions. Safe defaults; an operator may tighten them.</summary>
/// <param name="ApprovalLifetime">How long the approval of an edited draft stays open (doc 15 default: 24 hours).</param>
/// <param name="MaxEditedBodyChars">Longest edited body accepted, before the footer is added.</param>
/// <param name="ExpirySweepInterval">[PROPOSED] How often the expiry sweeper runs (default 5 minutes, floor 1 minute).</param>
public sealed record CommunityDraftOptions(TimeSpan? ApprovalLifetime = null, int MaxEditedBodyChars = 60_000, TimeSpan? ExpirySweepInterval = null)
{
    /// <summary>Shortest sweep interval an operator may set.</summary>
    public static readonly TimeSpan MinSweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>The sweep interval: 5 minutes when unset, never below <see cref="MinSweepInterval"/>.</summary>
    public TimeSpan SweepInterval => ExpirySweepInterval is { } v ? (v < MinSweepInterval ? MinSweepInterval : v) : TimeSpan.FromMinutes(5);

    /// <summary>The approval lifetime, 24 hours when unset.</summary>
    public TimeSpan Lifetime => ApprovalLifetime ?? TimeSpan.FromHours(24);
}

/// <summary>What the HTTP layer should answer. <see cref="Result"/> is set only on <see cref="DraftDecisionStatus.Ok"/>.</summary>
public enum DraftDecisionStatus
{
    /// <summary>200: decision recorded or replayed.</summary>
    Ok,

    /// <summary>400: malformed decision.</summary>
    BadRequest,

    /// <summary>403: the signature, key or device did not verify.</summary>
    Forbidden,

    /// <summary>404: no such draft.</summary>
    NotFound,

    /// <summary>409: already decided otherwise, in flight, or expired.</summary>
    Conflict,

    /// <summary>502: the platform refused the post for good.</summary>
    PostRefused,

    /// <summary>503: no adapter for the platform, or a retryable platform failure. The draft stays pending and the decision may be repeated.</summary>
    Unavailable,
}

/// <summary>Outcome of <see cref="CommunityDraftDecisionHandler.HandleAsync"/>. <see cref="Code"/> is a short snake_case reason, never body text.</summary>
public sealed record DraftDecisionResult(DraftDecisionStatus Status, string Code, CommunityDraftDecisionResultDto? Result = null);

/// <summary>
/// Applies the owner's signed reply to a draft-review card (doc 36 work package 8, doc 15 approval flow). The signature is the
/// doc 15 decision message verified against the owner's registered device key, bound to the draft's approval hash and expiry. The
/// approve decision posts through the platform adapter exactly once; deny records a denial; edit-then-approve is verified as a deny
/// of the original and stores the edited body as a new draft with its own approval, and never posts. No body is logged here.
/// </summary>
public sealed class CommunityDraftDecisionHandler(
    ICommunityDraftStore drafts,
    IOwnerKeyRegistry keys,
    IEnumerable<ICommunityAdapter> adapters,
    CommunityActionStore actions,
    ICommunityPolicySource policies,
    DisclosureOptions disclosure,
    TimeProvider? clock = null,
    CommunityDraftOptions? options = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly CommunityDraftOptions _options = options ?? new CommunityDraftOptions();
    private readonly ICommunityAdapter[] _adapters = [.. adapters];

    /// <summary>Handles one decision. Never throws for bad input; a failure writing the action log after a successful post propagates.</summary>
    public async Task<DraftDecisionResult> HandleAsync(Guid draftId, DraftReviewDecisionDto? dto, CancellationToken ct = default)
    {
        var shape = CheckShape(draftId, dto);
        if (shape is not null) return shape;
        var decision = dto!.Decision;

        var draft = await drafts.GetAsync(draftId, ct).ConfigureAwait(false);
        if (draft is null) return Fail(DraftDecisionStatus.NotFound, "unknown_draft");
        if (decision.ApprovalId != draft.ApprovalId) return Fail(DraftDecisionStatus.BadRequest, "approval_mismatch");

        // The signature comes first, so an unsigned or forged caller learns nothing about the draft's state.
        if (!await SignatureHoldsAsync(draft, decision, ct).ConfigureAwait(false))
            return Fail(DraftDecisionStatus.Forbidden, "invalid_signature");

        var kind = decision.Decision;
        switch (draft.Status)
        {
            case CommunityDraftStatus.Posted:
                return kind == ApprovalDecisionKind.ApproveOnce ? Ok(PostedDto(draft, alreadyDecided: true)) : Fail(DraftDecisionStatus.Conflict, "already_decided");
            case CommunityDraftStatus.Denied:
                return kind == ApprovalDecisionKind.Deny ? Ok(DeniedDto(draft, null, alreadyDecided: true)) : Fail(DraftDecisionStatus.Conflict, "already_decided");
            case CommunityDraftStatus.Edited:
                return kind == ApprovalDecisionKind.EditThenApprove ? await ReplayEditAsync(draft, ct).ConfigureAwait(false) : Fail(DraftDecisionStatus.Conflict, "already_decided");
            case CommunityDraftStatus.Posting:
                return Fail(DraftDecisionStatus.Conflict, "in_progress");
            case CommunityDraftStatus.Expired:
                return Fail(DraftDecisionStatus.Conflict, "expired");
            case CommunityDraftStatus.Failed:
                return Fail(DraftDecisionStatus.Conflict, "already_decided");
        }

        var now = _clock.GetUtcNow();
        if (now >= draft.ApprovalExpiresAt)
        {
            await drafts.TryReplaceAsync(draft.DraftId, CommunityDraftStatus.Pending, draft with { Status = CommunityDraftStatus.Expired }, ct).ConfigureAwait(false);
            return Fail(DraftDecisionStatus.Conflict, "expired");
        }

        return kind switch
        {
            ApprovalDecisionKind.Deny => await DenyAsync(draft, "owner_denied", reportReason: null, ct).ConfigureAwait(false),
            ApprovalDecisionKind.ApproveOnce => await ApproveAsync(draft, ct).ConfigureAwait(false),
            _ => await EditAsync(draft, dto.EditedBody!, now, ct).ConfigureAwait(false),
        };
    }

    private DraftDecisionResult? CheckShape(Guid draftId, DraftReviewDecisionDto? dto)
    {
        if (dto is null || dto.Decision is null || dto.DraftId != draftId || draftId == Guid.Empty)
            return Fail(DraftDecisionStatus.BadRequest, "bad_request");

        var d = dto.Decision;
        if (d.Decision is not (ApprovalDecisionKind.ApproveOnce or ApprovalDecisionKind.Deny or ApprovalDecisionKind.EditThenApprove))
            return Fail(DraftDecisionStatus.BadRequest, "unsupported_decision");
        if (!ApprovalSignature.IsValidDeviceId(d.DeviceId) || string.IsNullOrEmpty(d.SignatureBase64) || DecodeSignature(d.SignatureBase64) is null)
            return Fail(DraftDecisionStatus.BadRequest, "bad_request");

        if (d.Decision == ApprovalDecisionKind.EditThenApprove
            && (string.IsNullOrWhiteSpace(dto.EditedBody) || dto.EditedBody.Length > _options.MaxEditedBodyChars))
            return Fail(DraftDecisionStatus.BadRequest, "bad_edited_body");
        return null;
    }

    private static byte[]? DecodeSignature(string base64)
    {
        Span<byte> buffer = stackalloc byte[ApprovalSignature.SignatureLength];
        return Convert.TryFromBase64String(base64, buffer, out var written) && written == ApprovalSignature.SignatureLength ? buffer.ToArray() : null;
    }

    private async Task<bool> SignatureHoldsAsync(CommunityDraft draft, ApprovalDecisionDto decision, CancellationToken ct)
    {
        var key = await keys.FindAsync(decision.DeviceId, ct).ConfigureAwait(false);
        if (key is null || key.Revoked) return false;

        // Edit-then-approve is signed as a deny of this card: the edited body is a new draft with its own approval (doc 36).
        var signed = decision.Decision == ApprovalDecisionKind.ApproveOnce ? ApprovalDecision.Approve : ApprovalDecision.Deny;
        var message = ApprovalSignature.Message(draft.ApprovalId, signed, draft.ApprovalHash, draft.ApprovalExpiresAt, decision.DeviceId);
        return ApprovalSignature.Verify(key.PublicKeySpki, message, DecodeSignature(decision.SignatureBase64!)!);
    }

    private async Task<DraftDecisionResult> DenyAsync(CommunityDraft draft, string status, string? reportReason, CancellationToken ct)
    {
        var denied = draft with { Status = CommunityDraftStatus.Denied };
        if (!await drafts.TryReplaceAsync(draft.DraftId, CommunityDraftStatus.Pending, denied, ct).ConfigureAwait(false))
            return Fail(DraftDecisionStatus.Conflict, "in_progress");

        await RecordAsync(draft, status, body: null, externalId: null, ct).ConfigureAwait(false);
        return Ok(DeniedDto(denied, reportReason, alreadyDecided: false));
    }

    private async Task<DraftDecisionResult> ApproveAsync(CommunityDraft draft, CancellationToken ct)
    {
        // The approval binds the exact post. If the stored body or any outward field no longer matches what was signed, deny.
        if (DraftApprovalHash.BodySha256Hex(draft.Post.Body) != draft.Post.BodySha256
            || !DraftApprovalHash.Compute(draft.Post).AsSpan().SequenceEqual(draft.ApprovalHash))
            return await DenyAsync(draft, "denied_body_hash_mismatch", "body_hash_mismatch", ct).ConfigureAwait(false);

        // A failed gate or a missing disclosure footer never posts, whatever the signature says.
        if (draft.GateFailures.Count > 0 || !FooterPresent(draft))
            return await DenyAsync(draft, "denied_gates_failed", "gates_failed", ct).ConfigureAwait(false);

        var adapter = Array.Find(_adapters, a => string.Equals(a.Platform, draft.Post.Platform, StringComparison.OrdinalIgnoreCase));
        if (adapter is null) return Fail(DraftDecisionStatus.Unavailable, "no_adapter");

        var posting = draft with { Status = CommunityDraftStatus.Posting };
        if (!await drafts.TryReplaceAsync(draft.DraftId, CommunityDraftStatus.Pending, posting, ct).ConfigureAwait(false))
            return Fail(DraftDecisionStatus.Conflict, "in_progress");

        PostReceipt receipt;
        try
        {
            receipt = await adapter.PostAsync(draft.Post, new ApprovalId(draft.ApprovalId), ct).ConfigureAwait(false);
        }
        catch (CommunityPostException ex) when (!ex.Retryable)
        {
            await drafts.TryReplaceAsync(draft.DraftId, CommunityDraftStatus.Posting, draft with { Status = CommunityDraftStatus.Failed }, CancellationToken.None).ConfigureAwait(false);
            await RecordAsync(draft, "failed", body: null, externalId: null, CancellationToken.None).ConfigureAwait(false);
            return Fail(DraftDecisionStatus.PostRefused, "post_refused");
        }
#pragma warning disable CA1031 // Any other failure (retryable, cancelled, unexpected) leaves the draft retryable; the adapter is idempotent per draft id.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await drafts.TryReplaceAsync(draft.DraftId, CommunityDraftStatus.Posting, draft, CancellationToken.None).ConfigureAwait(false);
            if (ex is CommunityPostException) return Fail(DraftDecisionStatus.Unavailable, "post_retry_later");
            throw;
        }

        var posted = draft with
        {
            Status = CommunityDraftStatus.Posted,
            ExternalId = receipt.ExternalId,
            Url = receipt.Url.ToString(),
            Reused = receipt.Reused,
        };
        await drafts.TryReplaceAsync(draft.DraftId, CommunityDraftStatus.Posting, posted, CancellationToken.None).ConfigureAwait(false);
        await RecordAsync(draft, "done", draft.Post.Body, receipt.ExternalId, CancellationToken.None).ConfigureAwait(false);
        return Ok(PostedDto(posted, alreadyDecided: false));
    }

    private async Task<DraftDecisionResult> EditAsync(CommunityDraft draft, string editedBody, DateTimeOffset now, CancellationToken ct)
    {
        var newId = Guid.CreateVersion7(now);
        var body = BotProfileBuilder.AppendFooter(editedBody, draft.Profile, disclosure);
        var post = draft.Post with { DraftId = newId, Body = body, BodySha256 = DraftApprovalHash.BodySha256Hex(body) };

        var policy = await policies.FindAsync(post.Platform, ct).ConfigureAwait(false);
        // The gates read the agent's text without the footer: the footer carries the sponsor's disclosed contact, which the
        // personal-data gate would otherwise flag on every post.
        var gates = QualityGates.Evaluate(draft.GateInput with { Body = editedBody }, policy, DateOnly.FromDateTime(now.UtcDateTime));

        var replacement = draft with
        {
            DraftId = newId,
            Post = post,
            GateInput = draft.GateInput with { Body = editedBody },
            ApprovalId = Guid.CreateVersion7(now),
            ApprovalHash = DraftApprovalHash.Compute(post),
            ApprovalExpiresAt = now + _options.Lifetime,
            GateFailures = gates.Failures,
            Status = CommunityDraftStatus.Pending,
            ExternalId = null,
            Url = null,
            Reused = false,
            SupersededBy = null,
        };

        if (!await drafts.TrySupersedeAsync(draft.DraftId, replacement, ct).ConfigureAwait(false))
            return Fail(DraftDecisionStatus.Conflict, "in_progress");

        await RecordAsync(draft, "denied_edited", body: null, externalId: null, ct).ConfigureAwait(false);
        return Ok(EditedDto(draft.DraftId, replacement, alreadyDecided: false));
    }

    private async Task<DraftDecisionResult> ReplayEditAsync(CommunityDraft original, CancellationToken ct)
    {
        var next = original.SupersededBy is { } id ? await drafts.GetAsync(id, ct).ConfigureAwait(false) : null;
        return next is null ? Fail(DraftDecisionStatus.Conflict, "already_decided") : Ok(EditedDto(original.DraftId, next, alreadyDecided: true));
    }

    private async Task RecordAsync(CommunityDraft draft, string status, string? body, string? externalId, CancellationToken ct) =>
        _ = await actions.AppendAsync(
            new CommunityActionRecord(
                Id: 0,
                AgentId: draft.AgentId,
                SponsorId: draft.SponsorId,
                Platform: draft.Post.Platform,
                Action: draft.Action,
                TargetUrl: draft.Post.Target,
                Body: body,
                BodySha256: System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(draft.Post.Body)),
                ApprovalId: draft.ApprovalId.ToString("D"),
                TaintSourcesJson: draft.TaintSourcesJson,
                ExternalId: externalId,
                Status: status,
                CreatedAt: default),
            ct).ConfigureAwait(false);

    private bool FooterPresent(CommunityDraft draft) =>
        draft.Post.Body.TrimEnd().EndsWith(BotProfileBuilder.Footer(draft.Profile, disclosure), StringComparison.Ordinal);

    /// <summary>The review card for a draft: the exact body with its hash, the failed gates and the approval it is bound to.</summary>
    public DraftReviewCardDto ToCard(CommunityDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var canApprove = draft.GateFailures.Count == 0 && FooterPresent(draft);
        var pending = draft.Status == CommunityDraftStatus.Pending;
        IReadOnlyList<ApprovalDecisionKind> available = !pending
            ? []
            : canApprove
                ? [ApprovalDecisionKind.ApproveOnce, ApprovalDecisionKind.Deny, ApprovalDecisionKind.EditThenApprove]
                : [ApprovalDecisionKind.Deny, ApprovalDecisionKind.EditThenApprove];
        var approval = new ApprovalCardDto(
            draft.ApprovalId,
            draft.AgentId,
            "community.post",
            draft.Post.Target,
            $"{draft.Post.Kind} on {draft.Post.Platform}",
            PayloadText(draft.Post),
            Convert.ToHexStringLower(draft.ApprovalHash),
            [],
            null,
            draft.ApprovalExpiresAt,
            pending ? ApprovalCardStatus.Pending : ApprovalCardStatus.Denied,
            available);
        return new DraftReviewCardDto(
            draft.DraftId,
            draft.Post.Platform,
            draft.Post.Kind,
            draft.Post.Target,
            draft.Post.Title,
            draft.Post.Body,
            draft.Post.BodySha256,
            FooterPresent(draft),
            [.. draft.GateFailures.Select(f => new QualityGateFindingDto(f.Gate, false, f.Reason))],
            approval);
    }

    // The exact arguments as JSON text, without the body: the body is on the card, quoted, with its hash.
    private static string PayloadText(OutboundPost post)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("draftId", post.DraftId);
            w.WriteString("platform", post.Platform);
            w.WriteString("kind", post.Kind);
            w.WriteString("target", post.Target);
            if (post.Title is not null) w.WriteString("title", post.Title);
            w.WriteString("bodySha256", post.BodySha256);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static CommunityDraftDecisionResultDto PostedDto(CommunityDraft d, bool alreadyDecided) =>
        new(d.DraftId, "posted", null, d.ExternalId, d.Url, d.Reused, alreadyDecided, null);

    private static CommunityDraftDecisionResultDto DeniedDto(CommunityDraft d, string? reason, bool alreadyDecided) =>
        new(d.DraftId, "denied", reason, null, null, false, alreadyDecided, null);

    private CommunityDraftDecisionResultDto EditedDto(Guid originalId, CommunityDraft replacement, bool alreadyDecided) =>
        new(originalId, "edited", null, null, null, false, alreadyDecided, ToCard(replacement));

    private static DraftDecisionResult Ok(CommunityDraftDecisionResultDto dto) => new(DraftDecisionStatus.Ok, "ok", dto);

    private static DraftDecisionResult Fail(DraftDecisionStatus status, string code) => new(status, code);
}
