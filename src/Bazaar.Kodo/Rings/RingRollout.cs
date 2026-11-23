namespace Bazaar.Kodo.Rings;

/// <summary>Where one ring of a release stands.</summary>
public enum RingStatus
{
    /// <summary>Not started.</summary>
    NotStarted,

    /// <summary>The release is rolling to this ring and being watched.</summary>
    Live,

    /// <summary>Halted by a rule. Nothing later starts until an approved resume.</summary>
    Halted,

    /// <summary>Stayed live and healthy for the probation. The next ring may start.</summary>
    Cleared,
}

/// <summary>One ring's progress.</summary>
/// <param name="Ring">The ring.</param>
/// <param name="Status">Its status.</param>
/// <param name="LiveSince">When it went live, or was last resumed. Null until live.</param>
/// <param name="HaltDigest">The digest of the decision that halted it, or null.</param>
public sealed record RingProgress(ReleaseRing Ring, RingStatus Status, DateTimeOffset? LiveSince, string? HaltDigest);

/// <summary>The progress of one release through its rings. Immutable: every change returns a new value.</summary>
/// <param name="ReleaseId">The release.</param>
/// <param name="Channel">Its channel.</param>
/// <param name="Rings">Canary, early and broad, in that order.</param>
public sealed record ReleaseRollout(string ReleaseId, ReleaseChannel Channel, IReadOnlyList<RingProgress> Rings)
{
    /// <summary>A rollout with every ring not started.</summary>
    public static ReleaseRollout Begin(string releaseId, ReleaseChannel channel)
    {
        ArgumentException.ThrowIfNullOrEmpty(releaseId);
        return new ReleaseRollout(releaseId, channel, Enum.GetValues<ReleaseRing>().Select(ring => new RingProgress(ring, RingStatus.NotStarted, null, null)).ToList());
    }

    /// <summary>The progress of one ring.</summary>
    public RingProgress Of(ReleaseRing ring) => Rings[(int)ring];

    /// <summary>True when any ring is halted.</summary>
    public bool IsHalted => Rings.Any(progress => progress.Status == RingStatus.Halted);

    internal ReleaseRollout With(RingProgress progress)
    {
        List<RingProgress> rings = Rings.ToList();
        rings[(int)progress.Ring] = progress;
        return this with { Rings = rings };
    }
}

/// <summary>A rollout change, or the fixed code of the refusal. A refusal changes nothing.</summary>
/// <param name="Rollout">The new rollout, or the unchanged one on refusal.</param>
/// <param name="Refusal">The refusal code, or null.</param>
public sealed record RingTransition(ReleaseRollout Rollout, string? Refusal)
{
    /// <summary>True when the change was made.</summary>
    public bool Applied => Refusal is null;
}

/// <summary>
/// The pure rules for moving a release through its rings. Halting touches the halted ring's status and nothing else: no other ring is
/// changed, and there is no operation here that reaches an installed release (doc 46 section 16.6; rollback is a separate, approval-gated
/// choice).
/// </summary>
public static class RingRollouts
{
    /// <summary>Starts a ring. Earlier rings must be cleared, nothing may be halted, and canary may be skipped only where the profile allows it.</summary>
    public static RingTransition Start(ReleaseRollout rollout, ReleaseRing ring, RingChannelProfile profile, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rollout);
        ArgumentNullException.ThrowIfNull(profile);
        if (!Enum.IsDefined(ring))
        {
            return new RingTransition(rollout, "unknown_ring");
        }

        if (rollout.IsHalted)
        {
            return new RingTransition(rollout, "rollout_halted");
        }

        if (rollout.Of(ring).Status != RingStatus.NotStarted)
        {
            return new RingTransition(rollout, "ring_already_started");
        }

        for (int earlier = 0; earlier < (int)ring; earlier++)
        {
            ReleaseRing earlierRing = (ReleaseRing)earlier;
            bool skippable = earlierRing == ReleaseRing.Canary && profile.AllowSkipCanary && profile.Channel != ReleaseChannel.Lts;
            if (rollout.Of(earlierRing).Status != RingStatus.Cleared && !skippable)
            {
                return new RingTransition(rollout, "earlier_ring_not_cleared");
            }
        }

        return new RingTransition(rollout.With(new RingProgress(ring, RingStatus.Live, now, null)), null);
    }

    /// <summary>Applies a halt decision. Only a live ring halts; no other ring changes.</summary>
    public static RingTransition Halt(ReleaseRollout rollout, RingHaltDecision decision)
    {
        ArgumentNullException.ThrowIfNull(rollout);
        ArgumentNullException.ThrowIfNull(decision);
        RingProgress progress = rollout.Of(decision.Ring);
        if (progress.Status != RingStatus.Live)
        {
            return new RingTransition(rollout, "ring_not_live");
        }

        return new RingTransition(rollout.With(progress with { Status = RingStatus.Halted, HaltDigest = decision.Digest }), null);
    }

    /// <summary>Resumes a halted ring after an approved resume. The result must be for this ring's halt and must have been granted.</summary>
    public static RingTransition Resume(ReleaseRollout rollout, ReleaseRing ring, RingResumeResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rollout);
        ArgumentNullException.ThrowIfNull(result);
        RingProgress progress = rollout.Of(ring);
        if (progress.Status != RingStatus.Halted)
        {
            return new RingTransition(rollout, "ring_not_halted");
        }

        if (!result.Resumed || result.DecisionDigest != progress.HaltDigest)
        {
            return new RingTransition(rollout, "resume_not_approved");
        }

        return new RingTransition(rollout.With(progress with { Status = RingStatus.Live, LiveSince = now }), null);
    }

    /// <summary>Clears a live ring once it has been live for the probation and its last verdict is healthy. Insufficient data never clears.</summary>
    public static RingTransition Clear(ReleaseRollout rollout, ReleaseRing ring, RingVerdict verdict, RingChannelProfile profile, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rollout);
        ArgumentNullException.ThrowIfNull(profile);
        RingProgress progress = rollout.Of(ring);
        if (progress.Status != RingStatus.Live || progress.LiveSince is not { } since)
        {
            return new RingTransition(rollout, "ring_not_live");
        }

        if (verdict != RingVerdict.Healthy)
        {
            return new RingTransition(rollout, "verdict_not_healthy");
        }

        if (now - since < profile.Probation)
        {
            return new RingTransition(rollout, "probation_not_over");
        }

        return new RingTransition(rollout.With(progress with { Status = RingStatus.Cleared }), null);
    }
}
