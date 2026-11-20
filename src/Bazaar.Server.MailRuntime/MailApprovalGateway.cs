using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.ApprovalQueue;
using Bazaar.Server.MailOutbound;

namespace Bazaar.Server.MailRuntime;

/// <summary>Thrown when the approvals queue refuses a mail approval request. Nothing was filed; the pipeline's row stays awaiting approval and a resume asks again.</summary>
public sealed class MailApprovalRefusedException(ApprovalRefusal refusal, Exception inner)
    : InvalidOperationException($"The approvals queue refused the mail approval request ({refusal}).", inner)
{
    public ApprovalRefusal Refusal { get; } = refusal;
}

/// <summary>
/// <see cref="IMailApprovalGateway"/> over the shared approvals queue (doc 22, "Mail runtime (host wiring)"; doc 15 tiers). One request per message,
/// bound to the outbound message by its payload reference. The queue row carries a short summary and never the mail body, the subject line or full
/// addresses; the owner's card reads the message itself from the outbound store.
/// </summary>
public sealed class MailApprovalGateway(IApprovalQueue queue, IMailRuntimeOptionsSource options) : IMailApprovalGateway
{
    /// <summary>The queue kind for an outbound send awaiting the owner.</summary>
    public const string OutboundKind = "mail.outbound";

    private const int _maxSubjectLength = 200;

    public async Task<Guid> RequestAsync(MailApprovalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = new NewApproval(OutboundKind, request.Tier, Summarize(request), PrincipalFor(request.AgentId), request.IdempotencyKey, options.Current.EffectiveApprovalTtl);
        try
        {
            return await queue.RequestAsync(item, ct).ConfigureAwait(false);
        }
        catch (ApprovalRefusedException refused)
        {
            throw new MailApprovalRefusedException(refused.Refusal, refused);
        }
    }

    public async Task<MailApprovalStatus> GetStatusAsync(Guid approvalId, CancellationToken ct)
    {
        var found = await queue.GetAsync(approvalId, ct).ConfigureAwait(false);
        // An unknown id never reads as approved. Expired stands in for it: the pipeline ends the send instead of waiting forever.
        if (found is null) return MailApprovalStatus.Expired;
        return found.Status switch
        {
            ApprovalStatus.Pending => MailApprovalStatus.Pending,
            ApprovalStatus.Approved => MailApprovalStatus.Approved,
            ApprovalStatus.Denied => MailApprovalStatus.Denied,
            ApprovalStatus.Consumed => MailApprovalStatus.Consumed,
            _ => MailApprovalStatus.Expired,
        };
    }

    public Task<bool> ConsumeAsync(Guid approvalId, CancellationToken ct) => queue.TryConsumeAsync(approvalId, ct);

    // Domains only: the local parts and the subject line can carry personal data.
    private static string Summarize(MailApprovalRequest request)
    {
        var domains = request.Recipients
            .Select(r => r.Contains('@', StringComparison.Ordinal) ? r[(r.LastIndexOf('@') + 1)..].Trim().ToLowerInvariant() : string.Empty)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var text = $"Outbound mail from agent {request.AgentId} ({request.From.Domain}) to {request.Recipients.Count} recipient(s) at {(domains.Count == 0 ? "unknown domain" : string.Join(", ", domains))}; {request.AttachmentCount} attachment(s)";
        return text.Length <= _maxSubjectLength ? text : text[..(_maxSubjectLength - 3)] + "...";
    }

    // The queue wants a principal id; the pipeline knows an agent by a string. A Guid id is used as is, any other id maps to a stable Guid.
    private static Guid PrincipalFor(string agentId)
    {
        if (Guid.TryParse(agentId, out var id)) return id;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("bazaar.mail.agent:" + agentId));
        return new Guid(hash.AsSpan(0, 16));
    }
}
