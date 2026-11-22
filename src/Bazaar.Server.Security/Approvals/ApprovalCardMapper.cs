using Bazaar.Contracts.Approvals;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Security.Approvals;

/// <summary>
/// Turns a stored approval into the card a client shows (doc 15, approval flow step 2). A pure mapping: it reads the
/// row and never changes it.
/// </summary>
/// <remarks>
/// Taint excerpts are always marked quoted. The payload text is the stored arguments and the hash is the dispatch hash
/// the decision signature covers, so the card and the signature check agree. Only decisions the service can honour are
/// listed as available.
/// </remarks>
public static class ApprovalCardMapper
{
    /// <summary>Longest excerpt sent to a card. Longer text is cut and marked with an ellipsis.</summary>
    public const int MaxExcerptLength = 1000;

    /// <summary>Decisions the approval service can record today. Others are shown only once the service supports them.</summary>
    public static IReadOnlyList<ApprovalDecisionKind> SupportedDecisions { get; } =
        [ApprovalDecisionKind.ApproveOnce, ApprovalDecisionKind.Deny];

    /// <summary>
    /// Builds the card for one approval.
    /// </summary>
    /// <param name="request">The stored approval.</param>
    /// <param name="now">Current server time. A pending request at or after its expiry shows as expired.</param>
    /// <param name="excerptText">Excerpt text keyed by taint source id. Sources without text keep their lineage with a null text.</param>
    /// <param name="cost">Cost placeholder, or null.</param>
    public static ApprovalCardDto ToCard(
        ApprovalRequest request,
        DateTimeOffset now,
        IReadOnlyDictionary<Guid, string>? excerptText = null,
        string? cost = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var status = CardStatus(request, now);
        var decisions = status == ApprovalCardStatus.Pending ? SupportedDecisions : [];

        var excerpts = request.TaintSources
            .Select(source => new QuotedExcerptDto(
                source.SourceId.ToString("D"),
                SourceTrustOf(source.Trust),
                Clip(excerptText is not null && excerptText.TryGetValue(source.SourceId, out var text) ? text : null),
                Quoted: true))
            .ToList();

        return new ApprovalCardDto(
            request.Id,
            request.AgentId,
            request.Tool,
            request.Target,
            Summary(request),
            request.PayloadJson,
            Convert.ToHexStringLower(request.ArgsHash),
            excerpts,
            cost,
            request.ExpiresAt,
            status,
            decisions);
    }

    private static ApprovalCardStatus CardStatus(ApprovalRequest request, DateTimeOffset now) =>
        request.Status switch
        {
            ApprovalStatus.Pending when now >= request.ExpiresAt => ApprovalCardStatus.Expired,
            ApprovalStatus.Pending => ApprovalCardStatus.Pending,
            ApprovalStatus.Approved => ApprovalCardStatus.Approved,
            ApprovalStatus.Denied => ApprovalCardStatus.Denied,
            ApprovalStatus.Expired => ApprovalCardStatus.Expired,
            ApprovalStatus.Consumed => ApprovalCardStatus.Consumed,
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Status, "Unknown approval status."),
        };

    private static string Summary(ApprovalRequest request) =>
        request.Target is null
            ? $"{request.AgentId} asks to run {request.Tool}."
            : $"{request.AgentId} asks to run {request.Tool} on {request.Target}.";

    private static string? Clip(string? text)
    {
        if (text is null || text.Length <= MaxExcerptLength)
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, MaxExcerptLength), "...");
    }

    private static SourceTrust SourceTrustOf(ContentTrust trust) =>
        trust switch
        {
            ContentTrust.Owner => SourceTrust.Owner,
            ContentTrust.System => SourceTrust.System,
            ContentTrust.MemberHuman => SourceTrust.MemberHuman,
            ContentTrust.Agent => SourceTrust.Agent,
            ContentTrust.External => SourceTrust.External,
            ContentTrust.Quarantined => SourceTrust.Quarantined,
            _ => throw new ArgumentOutOfRangeException(nameof(trust), trust, "Unknown content trust."),
        };
}
