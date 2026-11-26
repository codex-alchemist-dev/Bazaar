namespace Bazaar.Server.WhatIf.Tests;

public sealed class SandboxRequestGuardTests
{
    private static SandboxRequest Valid(SandboxProfile? profile = null, string pushRef = "sandbox/what-if/x/keep") =>
        new(Guid.NewGuid(), "keep", Guid.NewGuid(), ["t"], new string('b', 64), profile ?? SandboxProfile.Default, pushRef);

    [Fact]
    public void A_default_profile_with_a_sandbox_ref_is_accepted()
    {
        Assert.Empty(SandboxRequestGuard.Check(Valid()));
    }

    [Fact]
    public void A_passport_token_is_rejected()
    {
        var violations = SandboxRequestGuard.Check(Valid(new SandboxProfile(PassportToken: "tok")));

        Assert.Contains(violations, v => v.Contains("Passport"));
    }

    [Fact]
    public void A_github_app_token_is_rejected()
    {
        var violations = SandboxRequestGuard.Check(Valid(new SandboxProfile(GitHubAppToken: "ghs_x")));

        Assert.Contains(violations, v => v.Contains("GitHub"));
    }

    [Theory]
    [InlineData(SandboxEgress.Allowlisted)]
    [InlineData(SandboxEgress.Open)]
    public void Egress_other_than_denied_is_rejected(SandboxEgress egress)
    {
        Assert.Contains(SandboxRequestGuard.Check(Valid(new SandboxProfile(egress))), v => v.Contains("egress"));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("refs/heads/main")]
    [InlineData("feature/sandbox/x")]
    [InlineData("sandbox/")]
    [InlineData("sandbox/../main")]
    [InlineData("sandbox//x")]
    [InlineData("sandbox/x y")]
    [InlineData("sandbox/x.lock")]
    [InlineData("sandbox/.hidden")]
    [InlineData("")]
    public void A_ref_outside_sandbox_is_rejected(string pushRef)
    {
        Assert.Contains(SandboxRequestGuard.Check(Valid(pushRef: pushRef)), v => v.Contains("sandbox/"));
        Assert.Throws<SandboxRequestRejectedException>(() => SandboxRequestGuard.EnsureValid(Valid(pushRef: pushRef)));
    }

    [Fact]
    public async Task The_fake_runner_applies_the_guard_itself()
    {
        var runner = new FakeSandboxRunner();

        await Assert.ThrowsAsync<SandboxRequestRejectedException>(
            () => runner.RunAsync(Valid(new SandboxProfile(PassportToken: "tok")), TestContext.Current.CancellationToken));
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task The_fake_runner_is_deterministic()
    {
        var runner = new FakeSandboxRunner();
        var request = Valid();

        var a = await runner.RunAsync(request, TestContext.Current.CancellationToken);
        var b = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(a, b);
    }
}
