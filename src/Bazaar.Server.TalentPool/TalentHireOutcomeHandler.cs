namespace Bazaar.Server.TalentPool;

/// <summary>
/// Talent Pool side of the Council <c>hire</c> motion: opens the motion for a quarantined version and consumes the <c>council.decision</c>
/// outcome. Mismatched or unknown outcomes are ignored and audited; replays are no-ops.
/// </summary>
public sealed class TalentHireOutcomeHandler(
    ITalentTemplateStore templates, ICouncilHireMotions motions, ITalentHireOutcomeStore outcomes, ITalentAuditSink audit)
{
    /// <summary>Opens a hire motion for a quarantined version. Returns null when the version is missing or not quarantined.</summary>
    public async Task<Guid?> SubmitHireMotionAsync(Guid templateId, int version, string proposedBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposedBy);
        var template = await templates.GetAsync(templateId, version, ct).ConfigureAwait(false);
        if (template is null || template.Status != TalentTemplateStatus.Quarantined)
            return null;

        var motionId = await motions.SubmitHireMotionAsync(new HireMotionRequest(templateId, version, template.ContentSha, proposedBy), ct).ConfigureAwait(false);
        await outcomes.AddMotionAsync(new HireMotionRecord(motionId, templateId, version, template.ContentSha, null), ct).ConfigureAwait(false);
        return motionId;
    }

    /// <summary>Applies one outcome. Returns true when it was recorded, false when ignored or a replay.</summary>
    public async Task<bool> HandleAsync(CouncilHireOutcome outcome, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var motion = await outcomes.GetMotionAsync(outcome.MotionId, ct).ConfigureAwait(false);
        if (motion is null)
            return await IgnoreAsync(outcome, "unknown motion", ct).ConfigureAwait(false);
        if (motion.TemplateId != outcome.TemplateId || motion.Version != outcome.Version)
            return await IgnoreAsync(outcome, "template or version differs from the motion", ct).ConfigureAwait(false);
        if (motion.Result == outcome.Result)
            return false;
        if (motion.Result is { } current && current != CouncilHireResult.EscalatedToOwner)
            return await IgnoreAsync(outcome, $"motion already final ({current})", ct).ConfigureAwait(false);

        await outcomes.SetResultAsync(outcome.MotionId, outcome.Result, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> IgnoreAsync(CouncilHireOutcome outcome, string reason, CancellationToken ct)
    {
        await audit.RecordAsync(new TalentAuditEvent("talent.hire_outcome_ignored", outcome.MotionId, outcome.TemplateId, outcome.Version, reason), ct).ConfigureAwait(false);
        return false;
    }
}
