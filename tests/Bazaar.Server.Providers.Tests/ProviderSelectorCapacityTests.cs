using Bazaar.Server.Providers;

namespace Bazaar.Server.Providers.Tests;

public class ProviderSelectorCapacityTests
{
    [Fact]
    public void Auto_skips_a_provider_whose_limit_is_below_the_required_concurrency()
    {
        var limited = new LimitedStub("windows_sandbox", maxConcurrent: 1);
        var pair = new LimitedStub("hyperv", maxConcurrent: 8);

        var selection = ProviderSelector.Select(ProviderRole.Vm, "auto", [limited, pair], ProviderRequirements.AgentPairConcurrency);

        Assert.Same(pair, selection.Provider);
        var refused = Assert.Single(selection.Statuses, s => s.Kind == "windows_sandbox");
        Assert.False(refused.Usable);
        Assert.Contains("declares 1 concurrent instance(s) and this role needs 2", refused.Reason);
    }

    [Fact]
    public void Auto_selects_nothing_when_every_candidate_is_below_the_required_concurrency()
    {
        var selection = ProviderSelector.Select(ProviderRole.Vm, "auto", [new LimitedStub("windows_sandbox", maxConcurrent: 1)], ProviderRequirements.AgentPairConcurrency);

        Assert.Null(selection.Provider);
        Assert.Contains("this role needs 2", selection.ToLogLine());
    }

    [Fact]
    public void An_explicit_kind_below_the_required_concurrency_fails_the_start_with_the_reason()
    {
        var limited = new LimitedStub("windows_sandbox", maxConcurrent: 1);

        var error = Assert.Throws<ProviderSelectionException>(() =>
            ProviderSelector.Select(ProviderRole.Vm, "windows_sandbox", [limited], ProviderRequirements.AgentPairConcurrency));

        Assert.Contains("this role needs 2", error.Message);
    }

    [Fact]
    public void A_limit_that_meets_the_requirement_is_eligible()
    {
        var provider = new LimitedStub("hyperv", maxConcurrent: 2);

        var selection = ProviderSelector.Select(ProviderRole.Vm, "hyperv", [provider], ProviderRequirements.AgentPairConcurrency);

        Assert.Same(provider, selection.Provider);
    }

    [Fact]
    public void A_provider_without_a_declared_limit_is_not_capacity_checked()
    {
        var unlimited = new PlainStub("plain");

        var selection = ProviderSelector.Select(ProviderRole.Vm, "auto", [unlimited], ProviderRequirements.AgentPairConcurrency);

        Assert.Same(unlimited, selection.Provider);
    }

    [Fact]
    public void A_required_concurrency_below_one_is_an_argument_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProviderSelector.Select(ProviderRole.Vm, "auto", [new PlainStub("plain")], 0));
    }

    [Fact]
    public void An_unusable_provider_keeps_its_own_probe_reason()
    {
        var selection = ProviderSelector.Select(ProviderRole.Vm, "auto", [new LimitedStub("windows_sandbox", maxConcurrent: 1, usable: false, reason: "feature off")], ProviderRequirements.AgentPairConcurrency);

        Assert.Equal("feature off", Assert.Single(selection.Statuses).Reason);
    }

    private sealed class LimitedStub(string kind, int maxConcurrent, bool usable = true, string? reason = null) : ICapacityLimitedProvider
    {
        public string Kind { get; } = kind;

        public int MaxConcurrentInstances { get; } = maxConcurrent;

        public ProviderStatus Probe() => new(Kind, usable, [], usable ? null : reason ?? "not usable");
    }

    private sealed class PlainStub(string kind) : IProvider
    {
        public string Kind { get; } = kind;

        public ProviderStatus Probe() => new(Kind, true, [], null);
    }
}
