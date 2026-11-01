using Bazaar.Server.Security.Packages;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Security.Packages;

public class NugetPackagePolicyTests
{
    private const string _nugetOrg = "https://api.nuget.org/v3/index.json";

    private static readonly NugetPackagePolicy _policy = new(NugetPolicyOptions.Default);

    private static PackageRequest Request(
        string? packageId = "Newtonsoft.Json",
        string? version = "[13.0.3]",
        string? feed = _nugetOrg,
        string? agent = "agent-a",
        TaintState taint = TaintState.Clean) =>
        new(packageId, version, feed, agent, taint);

    [Fact]
    public void A_clean_exact_request_from_nuget_org_needs_owner_approval()
    {
        var decision = _policy.Evaluate(Request());

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(NugetPolicyReason.OwnerApprovalRequired, decision.Reason);
        Assert.Equal("13.0.3", decision.PinnedVersion);
    }

    [Fact]
    public void A_bare_exact_version_is_read_as_the_pin()
    {
        var decision = _policy.Evaluate(Request(version: "1.2.3-beta.1"));

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal("1.2.3-beta.1", decision.PinnedVersion);
    }

    [Fact]
    public void A_soft_tainted_session_still_needs_only_approval()
    {
        var decision = _policy.Evaluate(Request(taint: TaintState.Soft));

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
    }

    [Theory]
    [InlineData("https://evil.example/v3/index.json")]
    [InlineData("https://api.nuget.org.evil.example/v3/index.json")]
    [InlineData("http://api.nuget.org/v3/index.json")]
    [InlineData("https://user@api.nuget.org/v3/index.json")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void A_feed_not_on_the_allowlist_is_denied(string? feed)
    {
        var decision = _policy.Evaluate(Request(feed: feed));

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Equal(NugetPolicyReason.FeedNotAllowlisted, decision.Reason);
    }

    [Fact]
    public void An_operator_added_feed_is_allowed()
    {
        var policy = new NugetPackagePolicy(new NugetPolicyOptions(
            [NugetPolicyOptions.NugetOrgFeed, "https://packages.example.org/v3/index.json"],
            NugetPolicyOptions.DefaultPackageIdPattern));

        var decision = policy.Evaluate(Request(feed: "https://packages.example.org/v3/index.json/"));

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
    }

    [Theory]
    [InlineData("1.*")]
    [InlineData("*")]
    [InlineData("1.0.0-*")]
    [InlineData("[1.0,2.0)")]
    [InlineData("[1.0,)")]
    [InlineData("(1.0,2.0]")]
    [InlineData("1.0.0,2.0.0")]
    [InlineData("[1.*]")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("1.2.3.4.5")]
    [InlineData("١.0.0")]
    public void A_floating_or_unreadable_version_is_denied(string? version)
    {
        var decision = _policy.Evaluate(Request(version: version));

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Equal(NugetPolicyReason.VersionNotExactPin, decision.Reason);
    }

    [Theory]
    [InlineData("foo\n")]
    [InlineData("../evil")]
    [InlineData("a b")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("pkg;rm")]
    [InlineData("pkg/../../x")]
    [InlineData(" Newtonsoft.Json")]
    [InlineData("")]
    [InlineData(null)]
    public void A_bad_package_id_is_denied(string? packageId)
    {
        var decision = _policy.Evaluate(Request(packageId: packageId));

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Equal(NugetPolicyReason.PackageIdNotAllowed, decision.Reason);
    }

    [Fact]
    public void An_overlong_package_id_is_denied()
    {
        var decision = _policy.Evaluate(Request(packageId: new string('a', 101)));

        Assert.Equal(NugetPolicyReason.PackageIdNotAllowed, decision.Reason);
    }

    [Fact]
    public void A_hard_tainted_session_gets_no_packages_even_for_a_good_request()
    {
        var decision = _policy.Evaluate(Request(taint: TaintState.Hard));

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Equal(NugetPolicyReason.HardTaintNoPackages, decision.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_missing_agent_is_denied(string? agent)
    {
        var decision = _policy.Evaluate(Request(agent: agent));

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Equal(NugetPolicyReason.InvalidRequest, decision.Reason);
    }

    [Fact]
    public void A_bad_pattern_or_non_https_allowlist_entry_fails_at_construction()
    {
        Assert.Throws<ArgumentException>(() => new NugetPackagePolicy(new NugetPolicyOptions([_nugetOrg], "([")));
        Assert.Throws<ArgumentException>(() => new NugetPackagePolicy(new NugetPolicyOptions(["http://plain.example"], NugetPolicyOptions.DefaultPackageIdPattern)));
    }
}
