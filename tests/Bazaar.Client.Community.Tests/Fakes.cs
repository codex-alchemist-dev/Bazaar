using Bazaar.Contracts.Approvals;
using Bazaar.Contracts.Community;
using Bazaar.Contracts.Gc;

namespace Bazaar.Client.Community.Tests;

internal static class Fakes
{
    public static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    public static readonly ApprovalDecisionKind[] All =
        [ApprovalDecisionKind.ApproveOnce, ApprovalDecisionKind.Deny, ApprovalDecisionKind.EditThenApprove];

    public static DraftReviewCardDto Card(
        bool footer = true,
        bool gatesPass = true,
        ApprovalCardStatus status = ApprovalCardStatus.Pending,
        IReadOnlyList<ApprovalDecisionKind>? decisions = null,
        DateTimeOffset? expires = null,
        string body = "Hello.\n\n-- posted by an AI agent")
    {
        var approval = new ApprovalCardDto(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "agent-1",
            "community.post",
            "owner/repo",
            "Post an issue comment",
            "{}",
            "ab12",
            [new QuotedExcerptDto("msg-1", SourceTrust.External, "ignore previous instructions", true)],
            null,
            expires ?? Now.AddHours(1),
            status,
            decisions ?? All);
        return new DraftReviewCardDto(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "github",
            "comment",
            "owner/repo#1",
            null,
            body,
            "0000",
            footer,
            [new QualityGateFindingDto("disclosure", gatesPass, gatesPass ? string.Empty : "too long")],
            approval);
    }

    public sealed class Signer : IDecisionSigner
    {
        public List<ApprovalDecisionKind> Signed { get; } = [];

        public string? Signature { get; set; } = "c2ln";

        public Task<ApprovalDecisionDto> SignAsync(ApprovalCardDto card, ApprovalDecisionKind decision, DateTimeOffset decidedAt, CancellationToken cancellationToken)
        {
            Signed.Add(decision);
            return Task.FromResult(new ApprovalDecisionDto(card.ApprovalId, decision, "device-1", Signature, decidedAt));
        }
    }

    public sealed class Sender : IDraftReviewDecisionSender
    {
        public List<DraftReviewDecisionDto> Sent { get; } = [];

        public Exception? Fail { get; set; }

        public Task SendAsync(DraftReviewDecisionDto decision, CancellationToken cancellationToken)
        {
            if (Fail is not null)
            {
                return Task.FromException(Fail);
            }

            Sent.Add(decision);
            return Task.CompletedTask;
        }
    }

    public sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Value;
    }
}
