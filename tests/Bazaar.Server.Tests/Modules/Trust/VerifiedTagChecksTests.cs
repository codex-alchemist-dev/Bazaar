using Bazaar.Server.Install.Trust;

namespace Bazaar.Server.Tests.Modules.Trust;

public class VerifiedTagChecksTests
{
    private static readonly DateTimeOffset _checked = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static VerifiedTagResults AllPass(string version) =>
        new(version, _checked, CheckState.Passed, CheckState.Passed, CheckState.Passed, CheckState.Passed, CheckState.Passed, CheckState.Passed);

    /// <summary>Returns the results with exactly one check set to <paramref name="state"/>, named as in the doc's order.</summary>
    private static VerifiedTagResults WithOne(string version, string check, CheckState state)
    {
        var all = AllPass(version);
        return check switch
        {
            "licence" => all with { Licence = state },
            "malware" => all with { Malware = state },
            "vulnerability" => all with { Vulnerability = state },
            "signature" => all with { Signature = state },
            "human-review" => all with { HumanReview = state },
            "sandbox-run" => all with { MonitoredSandboxRun = state },
            _ => throw new ArgumentOutOfRangeException(nameof(check)),
        };
    }

    private static VerifiedTagResults FailOne(string version, string check) => WithOne(version, check, CheckState.Failed);

    [Fact]
    public void All_checks_passing_is_verified()
    {
        var decisions = VerifiedTagChecks.Evaluate([AllPass("1.0.0")]);

        var decision = Assert.Single(decisions);
        Assert.True(decision.Verified);
        Assert.Empty(decision.FailedChecks);
        Assert.Empty(decision.PendingChecks);
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

    [Theory]
    [InlineData("licence")]
    [InlineData("human-review")]
    [InlineData("sandbox-run")]
    public void A_pending_check_withholds_the_tag_without_revoking_it(string check)
    {
        var decisions = VerifiedTagChecks.Evaluate([WithOne("1.0.0", check, CheckState.Pending)]);

        var decision = Assert.Single(decisions);
        Assert.False(decision.Verified);
        Assert.Empty(decision.FailedChecks);
        Assert.Equal(check, Assert.Single(decision.PendingChecks));
        Assert.Null(decision.RevokedBy);
    }

    [Fact]
    public void A_pending_version_is_not_revoked_and_a_later_pass_does_not_fail_it()
    {
        var decisions = VerifiedTagChecks.Evaluate(
        [
            AllPass("1.0.0"),
            WithOne("1.1.0", "malware", CheckState.Pending),
            AllPass("1.2.0"),
        ]);

        Assert.True(decisions[0].Verified);
        Assert.False(decisions[1].Verified);
        Assert.Null(decisions[1].RevokedBy);
        Assert.True(decisions[2].Verified);
    }

    [Fact]
    public void A_failure_is_still_revocation_when_another_check_is_pending()
    {
        var both = AllPass("1.1.0") with { Licence = CheckState.Failed, Malware = CheckState.Pending };

        var decision = Assert.Single(VerifiedTagChecks.Evaluate([both]));

        Assert.False(decision.Verified);
        Assert.Equal("licence", Assert.Single(decision.FailedChecks));
        Assert.Equal("malware", Assert.Single(decision.PendingChecks));
        Assert.Equal("1.1.0", decision.RevokedBy);
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
    public void Versions_are_ordered_by_semver_precedence_not_by_text()
    {
        // "1.10.0" is newer than "1.9.0", though it sorts earlier as text.
        var decisions = VerifiedTagChecks.Evaluate([AllPass("1.9.0"), AllPass("1.10.0")]);

        Assert.Equal(2, decisions.Count);
        Assert.All(decisions, decision => Assert.True(decision.Verified));
    }

    [Fact]
    public void A_prerelease_is_older_than_its_release()
    {
        var decisions = VerifiedTagChecks.Evaluate([AllPass("1.0.0-beta.2"), AllPass("1.0.0")]);

        Assert.Equal(2, decisions.Count);
    }

    [Fact]
    public void A_list_out_of_semver_order_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => VerifiedTagChecks.Evaluate([AllPass("1.2.0"), AllPass("1.1.0")]));

        Assert.Contains("oldest first", error.Message);
    }

    [Fact]
    public void A_duplicate_version_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => VerifiedTagChecks.Evaluate([AllPass("1.0.0"), AllPass("1.0.0+build.5")]));

        Assert.Contains("more than once", error.Message);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("01.0.0")]
    public void A_version_that_is_not_semver_is_refused(string version)
    {
        Assert.Throws<ArgumentException>(() => VerifiedTagChecks.Evaluate([AllPass(version)]));
    }

    [Fact]
    public void Empty_line_has_no_decisions()
    {
        Assert.Empty(VerifiedTagChecks.Evaluate([]));
    }
}
