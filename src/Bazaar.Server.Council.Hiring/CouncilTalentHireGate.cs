using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Council.Hiring;

/// <summary>
/// Opens Talent hire votes on the Council and reports their quarantine state (docs/design/25-company-systems-knowledge.md,
/// work package 8). The vote decides quarantine release. Unanimity, follow-up and the owner escalation stay in the Council.
/// </summary>
public sealed class CouncilTalentHireGate(
    ICouncilService council,
    IHireDecisionStore store,
    HireMotionDraftBuilder builder,
    TimeProvider time) : IHireGate
{
    /// <inheritdoc />
    public async Task<HireVoteState> RequestAsync(HireRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // One open vote per template: a pending or released template returns its current state without a new vote.
        var current = await store.FindAsync(request.TemplateId, ct);
        if (current is not null && current.Status is HireStatus.Pending or HireStatus.Released)
        {
            return current;
        }

        var motion = builder.Build(request);
        var voteId = await council.OpenAsync(request.ProposerId, motion.Draft, ct);
        var pending = new HireVoteState(request.TemplateId, voteId, HireStatus.Pending, Decision: null, time.GetUtcNow());
        await store.SaveAsync(pending, ct);
        return pending;
    }

    /// <inheritdoc />
    public async Task<bool> IsReleasedAsync(string templateId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);

        var current = await store.FindAsync(templateId, ct);
        return current?.Status == HireStatus.Released;
    }
}
