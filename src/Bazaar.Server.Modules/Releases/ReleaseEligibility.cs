namespace Bazaar.Server.Modules.Releases;

/// <summary>
/// Which candidates an install may be offered. Pure: the caller supplies the install's channel, id and ring percentage.
/// </summary>
public static class ReleaseEligibility
{
    /// <summary>
    /// Exact match. A candidate reaches only installs on its own channel, so a Beta install never gets a Stable-only
    /// candidate and a Stable install never gets a Beta candidate. Moving an install to another channel is a separate
    /// audited decision (doc 49 section 6), not something a candidate does.
    /// </summary>
    public static bool IsEligible(ReleaseChannel installChannel, ReleaseChannel candidateChannel) =>
        installChannel == candidateChannel;

    /// <summary>
    /// True when this install should be offered the candidate now: the candidate is rolling (a halted or not yet
    /// started candidate is never offered), the channel matches, and the install is inside the candidate's current ring.
    /// </summary>
    /// <param name="ringPercent">The rollout percentage for <see cref="ReleaseCandidate.Ring"/>, from settings.</param>
    public static bool IsOffered(string installId, ReleaseChannel installChannel, ReleaseCandidate candidate, int ringPercent)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return candidate.Status == ReleaseCandidateStatus.Rolling
            && IsEligible(installChannel, candidate.Channel)
            && RingBucket.IsInRing(installId, ringPercent);
    }
}
