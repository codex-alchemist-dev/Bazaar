namespace Bazaar.Kodo.Rings;

/// <summary>The result of checking one live ring.</summary>
/// <param name="Evaluation">The evaluation.</param>
/// <param name="Decision">The halt decision, or null when the ring did not halt.</param>
/// <param name="Rollout">The rollout after the check: the ring is halted when it tripped, otherwise unchanged.</param>
public sealed record RingCheck(RingEvaluation Evaluation, RingHaltDecision? Decision, ReleaseRollout Rollout);

/// <summary>
/// Checks live rings (doc 46 section 16.6). Picks the channel's thresholds, refuses a ring that is not live or a window that began
/// before the ring went live, evaluates, and on a halt returns the decision and the halted rollout. It reads time only from the
/// <see cref="TimeProvider"/>, and it does not alert, journal or store: the caller does that with the returned decision.
/// </summary>
public sealed class ReleaseRingMonitor
{
    private readonly Dictionary<ReleaseChannel, (RingChannelProfile Profile, RingHaltEvaluator Evaluator)> _channels = [];
    private readonly TimeProvider _time;

    /// <summary>Creates the monitor. Every profile is validated; a missing channel falls back to its shipped default.</summary>
    public ReleaseRingMonitor(IReadOnlyDictionary<ReleaseChannel, RingChannelProfile>? profiles, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _time = timeProvider;
        foreach (ReleaseChannel channel in Enum.GetValues<ReleaseChannel>())
        {
            RingChannelProfile profile = profiles is not null && profiles.TryGetValue(channel, out RingChannelProfile? custom)
                ? custom
                : RingChannelProfile.Defaults[channel];
            profile.Validate();
            if (profile.Channel != channel)
            {
                throw new ArgumentException($"Profile for {channel} names {profile.Channel}.", nameof(profiles));
            }

            _channels[channel] = (profile, new RingHaltEvaluator(profile.Halt));
        }
    }

    /// <summary>The profile in force for a channel.</summary>
    public RingChannelProfile ProfileFor(ReleaseChannel channel) => _channels[channel].Profile;

    /// <summary>Checks one ring of a rollout against its baseline.</summary>
    public RingCheck Check(ReleaseRollout rollout, ReleaseRing ring, CohortSignals cohort, CohortSignals? baseline)
    {
        ArgumentNullException.ThrowIfNull(rollout);
        ArgumentNullException.ThrowIfNull(cohort);
        RingHaltEvaluator evaluator = _channels[rollout.Channel].Evaluator;
        RingProgress progress = rollout.Of(ring);

        if (progress.Status != RingStatus.Live || progress.LiveSince is not { } liveSince)
        {
            return Insufficient(rollout, RingInsufficientCodes.RingNotLive);
        }

        if (cohort.Ring != ring || cohort.ReleaseId != rollout.ReleaseId)
        {
            return Insufficient(rollout, RingInsufficientCodes.InvalidSignals);
        }

        // A window that began before the ring went live (or was resumed) mixes in data from before the ring's current life.
        RingEvaluation evaluation = evaluator.Evaluate(cohort, baseline);
        if (evaluation.Verdict != RingVerdict.Halt && cohort.WindowStart < liveSince)
        {
            return new RingCheck(
                new RingEvaluation(RingVerdict.InsufficientData, [], [.. evaluation.InsufficientCodes, RingInsufficientCodes.WindowPredatesLive], []),
                null,
                rollout);
        }

        if (evaluation.Verdict != RingVerdict.Halt)
        {
            return new RingCheck(evaluation, null, rollout);
        }

        RingHaltDecision decision = RingHaltDecision.Create(evaluation, cohort, rollout.Channel, _time.GetUtcNow());
        RingTransition halted = RingRollouts.Halt(rollout, decision);
        return new RingCheck(evaluation, decision, halted.Rollout);
    }

    private static RingCheck Insufficient(ReleaseRollout rollout, string code) =>
        new(new RingEvaluation(RingVerdict.InsufficientData, [], [code], []), null, rollout);
}
