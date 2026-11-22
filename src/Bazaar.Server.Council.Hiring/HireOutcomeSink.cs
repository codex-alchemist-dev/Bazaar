using Bazaar.Server.Council.Membership;

namespace Bazaar.Server.Council.Hiring;

/// <summary>
/// Applies Council hire outcomes to the quarantine state (docs/design/10-council-and-voting.md, Effect; doc 25).
/// Only the vote the gate opened for a template counts. Other membership kinds are ignored, and a replay changes nothing.
/// The subject of a hire outcome is the template id (<see cref="MembershipOutcome.SubjectAgentId"/> carries it).
/// </summary>
public sealed class HireOutcomeSink(IHireDecisionStore store, TimeProvider time) : IMembershipOutcomeSink
{
    /// <inheritdoc />
    public async Task PublishAsync(MembershipOutcome outcome, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Kind != MembershipMotionKind.Hire)
        {
            return;
        }

        var current = await store.FindAsync(outcome.SubjectAgentId, ct);

        // An unknown template, or an outcome for a vote other than the current one, changes nothing (fail closed).
        if (current is null || current.VoteId != outcome.VoteId)
        {
            return;
        }

        // Already decided: a replay of the same outcome, or a late one, changes nothing.
        if (current.Status != HireStatus.Pending)
        {
            return;
        }

        var status = StatusFor(outcome);
        await store.SaveAsync(current with { Status = status, Decision = outcome.Decision, UpdatedAt = time.GetUtcNow() }, ct);
    }

    /// <summary>
    /// Released only for an explicit approve: an approved or owner-approved decision whose choice is yes. Everything else,
    /// including an approve with no choice, is blocked.
    /// </summary>
    internal static HireStatus StatusFor(MembershipOutcome outcome)
    {
        var approved = outcome.Decision is MembershipDecision.Approved or MembershipDecision.OwnerApproved;
        return approved && outcome.Choice == ApprovalChoices.Yes ? HireStatus.Released : HireStatus.Blocked;
    }
}
