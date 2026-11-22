using System.Text;
using Bazaar.Server.AccountApproval.Abstractions;
using Bazaar.Server.Security.Approvals;

namespace Bazaar.Server.AccountApproval.OwnerQueue;

/// <summary>
/// Hands an account approval request to the owner's Tier 2 queue (doc 15, "Peer approval of account creation"), through
/// <see cref="IOwnerApprovalEnqueuer"/>. It records tool <see cref="ToolName"/> for the requesting agent, with the request id
/// as the action key, so a repeat of one request never makes a second approval. It only asks; the owner's signed decision is
/// the only thing that approves.
/// </summary>
/// <remarks>
/// The summary holds only what the owner needs and is safe to show: the service, the requester, the rule that sent the request
/// to the owner, the terms link without user info, query or fragment, and vote counts. Peer model ids, reasoning hashes and
/// any other evidence field stay out. Failures and cancellation propagate, so the caller does not report a hand-off that did
/// not happen.
/// </remarks>
public sealed class OwnerQueueApprovalHandOff(IOwnerApprovalEnqueuer enqueuer) : IOwnerApprovalHandOff
{
    /// <summary>The tool name recorded on the approval.</summary>
    public const string ToolName = "account.request";

    /// <summary>Prefix of the action key, so account request ids cannot collide with another module's keys.</summary>
    public const string ActionKeyPrefix = "account-approval:";

    private const int _fieldLimit = 160;

    private readonly IOwnerApprovalEnqueuer _enqueuer = enqueuer ?? throw new ArgumentNullException(nameof(enqueuer));

    /// <inheritdoc />
    public async Task RequestOwnerApprovalAsync(OwnerApprovalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);

        var enqueue = new OwnerApprovalEnqueueRequest(
            request.Evidence.RequesterAgentId,
            ToolName,
            ActionKeyPrefix + request.RequestId,
            BuildSummary(request));
        await _enqueuer.EnqueueAsync(enqueue, ct).ConfigureAwait(false);
    }

    /// <summary>Builds the owner-facing summary. Pure, and capped to the enqueue port's summary limit.</summary>
    public static string BuildSummary(OwnerApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var e = request.Evidence;
        var sb = new StringBuilder();
        sb.Append("Account request ").Append(Clean(request.RequestId)).Append(": ")
          .Append(Clean(e.RequesterAgentId)).Append(" asks to create an account at ").Append(Clean(e.Service))
          .Append(". Sent to you by rule ").Append(Clean(request.Reason)).Append('.');

        if (SafeUrl(e.TermsUrl) is { } terms)
        {
            sb.Append(" Terms: ").Append(terms).Append('.');
        }

        if (e.SiteVote is { } vote)
        {
            sb.Append(" Site vote ").Append(Clean(vote.VoteReference)).Append(": ")
              .Append(vote.Approve).Append(" approve, ").Append(vote.Reject).Append(" reject, ")
              .Append(vote.Abstain).Append(" abstain of ").Append(vote.Eligible).Append(" eligible.");
        }

        var peers = e.PeerVotes ?? [];
        if (peers.Count > 0)
        {
            var approve = peers.Count(v => v.Kind == PeerVoteKind.Approve);
            sb.Append(" Peer votes: ").Append(approve).Append(" approve, ").Append(peers.Count - approve).Append(" refuse.");
        }

        var text = sb.ToString();
        return text.Length <= OwnerApprovalEnqueueRequest.MaxSummaryLength
            ? text
            : text[..(OwnerApprovalEnqueueRequest.MaxSummaryLength - 3)] + "...";
    }

    /// <summary>Control characters become spaces and the text is cut to a fixed length, so one field cannot fill the summary.</summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(none)";
        }

        var chars = value.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        var text = new string(chars).Trim();
        if (text.Length == 0)
        {
            return "(none)";
        }

        return text.Length <= _fieldLimit ? text : text[.._fieldLimit] + "...";
    }

    /// <summary>The terms link as scheme, host and path only. Anything else (other schemes, user info, bad text) is dropped.</summary>
    private static string? SafeUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        return Clean(uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped));
    }
}
