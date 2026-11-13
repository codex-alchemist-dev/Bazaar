using Bazaar.Server.Modules.Trust;

namespace Bazaar.Server.Tests.Modules.Trust;

public class VerifiedTagChecksTests
{
    private static readonly DateTimeOffset Checked = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static VerifiedTagResults AllPass(string version) =>
        new(version, Checked, LicenceOk: true, MalwareOk: true, VulnerabilityOk: true, SignatureOk: true, HumanReviewRecorded: true, MonitoredSandboxRunPassed: true);

    /// <summary>Returns the results with exactly one check failed, named as in <see cref="VerifiedTagChecks.FailedChecks"/>.</summary>
    private static VerifiedTagResults FailOne(string version, string check)
    {
        var all = AllPass(version);
        return check switch
        {
            "licence" => all with { LicenceOk = false },
            "malware" => all with { MalwareOk = false },
            "vulnerability" => all with { VulnerabilityOk = false },
            "signature" => all with { SignatureOk = false },
            "human-review" => all with { HumanReviewRecorded = false },
            "sandbox-run" => all with { MonitoredSandboxRunPassed = false },
            _ => throw new ArgumentOutOfRangeException(nameof(check)),
        };
    }

    [Fact]
    public void All_checks_passing_is_verified()
    {
        var decisions = VerifiedTagChecks.Evaluate([AllPass("1.0.0")]);

        var decision = Assert.Single(decisions);
        Assert.True(decision.Verified);
        Assert.Empty(decision.FailedChecks);
        Assert.Null(decision.RevokedBy);
    }

    [Theory]
    [InlineData("licence")]
    [InlineData("malware")]
    [InlineData("vulnerability")]
    [InlineData("signature")]
    [InlineData("human-review")]
    [InlineData("sandbox-run")]
    public void Any_failed_check_means_not_verified(string check)
    {
        var decisions = VerifiedTagChecks.Evaluate([FailOne("1.0.0", check)]);

        var decision = Assert.Single(decisions);
        Assert.False(decision.Verified);
        Assert.Equal(check, Assert.Single(decision.FailedChecks));
        Assert.Equal("1.0.0", decision.RevokedBy);
    }

    [Fact]
    public void Earlier_versions_keep_the_tag_when_every_later_version_passes()
    {
        var decisions = VerifiedTagChecks.Evaluate([AllPass("1.0.0"), AllPass("1.1.0")]);

        Assert.All(decisions, decision => Assert.True(decision.Verified));
    }

    [Fact]
    public void A_later_failed_check_revokes_that_version_and_every_version_after_it()
    {
        var decisions = VerifiedTagChecks.Evaluate(
        [
            AllPass("1.0.0"),
            FailOne("1.1.0", "licence"),
            AllPass("1.2.0"),
        ]);

        Assert.Equal(3, decisions.Count);
        Assert.True(decisions[0].Verified);
        Assert.Null(decisions[0].RevokedBy);

        Assert.False(decisions[1].Verified);
        Assert.Equal("licence", Assert.Single(decisions[1].FailedChecks));
        Assert.Equal("1.1.0", decisions[1].RevokedBy);

        Assert.False(decisions[2].Verified);
        Assert.Empty(decisions[2].FailedChecks);
        Assert.Equal("1.1.0", decisions[2].RevokedBy);
    }

    [Fact]
    public void Empty_line_has_no_decisions()
    {
        Assert.Empty(VerifiedTagChecks.Evaluate([]));
    }
}
