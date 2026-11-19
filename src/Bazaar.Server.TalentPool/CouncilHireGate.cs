namespace Bazaar.Server.TalentPool;

/// <summary>
/// <see cref="ITalentHireGate"/> over recorded Council outcomes. Approves only a motion for exactly this content whose outcome is
/// passed (unanimous) or owner_approved; everything else, including a pending or escalated motion, denies.
/// </summary>
public sealed class CouncilHireGate(ITalentHireOutcomeStore outcomes) : ITalentHireGate
{
    public async Task<bool> HasHirePassedAsync(Guid templateId, int version, string contentSha, CancellationToken ct)
    {
        var motions = await outcomes.ListMotionsAsync(templateId, version, ct).ConfigureAwait(false);
        return motions.Any(m =>
            string.Equals(m.ContentSha, contentSha, StringComparison.Ordinal)
            && m.Result is CouncilHireResult.Passed or CouncilHireResult.OwnerApproved);
    }
}
