using Bazaar.Server.Modules.Releases;

namespace Bazaar.Server.Tests.Modules.Releases;

/// <summary>Channel eligibility and offers (doc 49 section 6). Pure.</summary>
public sealed class ReleaseEligibilityTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    private static ReleaseCandidate Rolling(ReleaseChannel channel, ReleaseRing ring = ReleaseRing.Canary) =>
        new("0.1.0", channel, ring, ReleaseCandidateStatus.Rolling, _at);

    [Theory]
    [InlineData(ReleaseChannel.Stable)]
    [InlineData(ReleaseChannel.Beta)]
    [InlineData(ReleaseChannel.Nightly)]
    [InlineData(ReleaseChannel.Lts)]
    public void Install_is_eligible_for_a_candidate_on_its_own_channel(ReleaseChannel channel)
    {
        Assert.True(ReleaseEligibility.IsEligible(channel, channel));
    }

    [Fact]
    public void No_candidate_crosses_channels_in_either_direction()
    {
        var channels = Enum.GetValues<ReleaseChannel>();

        foreach (var install in channels)
        {
            foreach (var candidate in channels.Where(c => c != install))
            {
                Assert.False(ReleaseEligibility.IsEligible(install, candidate), $"{install} install must not get a {candidate} candidate.");
            }
        }
    }

    [Fact]
    public void Beta_install_never_gets_a_stable_only_candidate_and_stable_never_gets_beta()
    {
        Assert.False(ReleaseEligibility.IsOffered("install-0001", ReleaseChannel.Beta, Rolling(ReleaseChannel.Stable), 100));
        Assert.False(ReleaseEligibility.IsOffered("install-0001", ReleaseChannel.Stable, Rolling(ReleaseChannel.Beta), 100));
    }

    [Fact]
    public void Candidate_that_is_not_rolling_is_never_offered()
    {
        var proposed = new ReleaseCandidate("0.1.0", ReleaseChannel.Beta, ReleaseRing.Canary, ReleaseCandidateStatus.Candidate, _at);
        var halted = new ReleaseCandidate("0.1.0", ReleaseChannel.Beta, ReleaseRing.Canary, ReleaseCandidateStatus.Halted, _at, "red");
        var complete = new ReleaseCandidate("0.1.0", ReleaseChannel.Beta, ReleaseRing.Broad, ReleaseCandidateStatus.Complete, _at);

        Assert.False(ReleaseEligibility.IsOffered("install-0001", ReleaseChannel.Beta, proposed, 100));
        Assert.False(ReleaseEligibility.IsOffered("install-0001", ReleaseChannel.Beta, halted, 100));
        Assert.False(ReleaseEligibility.IsOffered("install-0001", ReleaseChannel.Beta, complete, 100));
    }

    [Fact]
    public void Rolling_candidate_is_offered_only_inside_the_ring_percentage()
    {
        var candidate = Rolling(ReleaseChannel.Beta);

        Assert.False(ReleaseEligibility.IsOffered("install-0001", ReleaseChannel.Beta, candidate, 0));
        Assert.True(ReleaseEligibility.IsOffered("install-0001", ReleaseChannel.Beta, candidate, 100));

        var inside = RingBucket.IsInRing("install-0001", 10);
        Assert.Equal(inside, ReleaseEligibility.IsOffered("install-0001", ReleaseChannel.Beta, candidate, 10));
    }

    [Fact]
    public void IsOffered_rejects_a_null_candidate()
    {
        Assert.Throws<ArgumentNullException>(() => ReleaseEligibility.IsOffered("install-0001", ReleaseChannel.Beta, null!, 10));
    }
}
