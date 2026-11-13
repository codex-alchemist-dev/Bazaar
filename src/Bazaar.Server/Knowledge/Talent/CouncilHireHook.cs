using Bazaar.Server.Council.Membership;

namespace Bazaar.Server.Knowledge.Talent;

/// <summary>
/// Connects Council membership decisions (<see cref="MembershipMotionKind.Hire"/>) to the Talent Pool (doc 25 §2, doc 10).
/// </summary>
public sealed class CouncilHireHook
{
    private readonly ITalentPool _talentPool;

    public CouncilHireHook(ITalentPool talentPool)
    {
        _talentPool = talentPool ?? throw new ArgumentNullException(nameof(talentPool));
    }

    /// <summary>
    /// Processes a Council membership outcome. If the motion is an approved <see cref="MembershipMotionKind.Hire"/>,
    /// activates the corresponding template in the Talent Pool.
    /// </summary>
    public async Task<TemplateHireResult?> OnMembershipOutcomeAsync(
        MembershipOutcome outcome,
        string templateId,
        int version,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);

        if (outcome.Kind != MembershipMotionKind.Hire)
        {
            return null;
        }

        if (outcome.Decision is not (MembershipDecision.Approved or MembershipDecision.OwnerApproved))
        {
            return new TemplateHireResult(false, $"Council hire motion was not approved (decision: {outcome.Decision}).");
        }

        var approver = outcome.Decision == MembershipDecision.OwnerApproved ? "Council+Owner" : "Council";
        return await _talentPool.ApplyHireMotionAsync(templateId, version, approver, ct).ConfigureAwait(false);
    }
}
