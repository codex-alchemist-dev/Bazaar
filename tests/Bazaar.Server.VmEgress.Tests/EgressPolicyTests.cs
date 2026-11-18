using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using Bazaar.Server.VmEgress.Proxy;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmEgress.Tests;

public class EgressPolicyTests
{
    private sealed class ScriptedGate : IEgressApprovalGate
    {
        public EgressApprovalState Answer { get; set; } = EgressApprovalState.Pending;

        public List<(string Agent, string[] Additions, int Tier)> Requests { get; } = [];

        public Task<EgressApprovalState> RequestGrowthAsync(string agentId, IReadOnlyList<string> additions, int tier, CancellationToken ct = default)
        {
            Requests.Add((agentId, [.. additions], tier));
            return Task.FromResult(Answer);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(VmEgressOptions? options = null)
        {
            Options = options ?? new VmEgressOptions { DefaultAllowlist = ["api.github.com", "*.gateway.example"] };
            Auditor = new EgressAuditor(Audit, Options, TimeProvider.System);
            Host = new EgressProxyHost(Options, Auditor);
            Policy = new EgressPolicyService(Store, Enforcer, Host, Gate, Auditor, Options);
        }

        public VmEgressOptions Options { get; }

        public CapturingAuditLog Audit { get; } = new();

        public InMemoryEgressAllowlistStore Store { get; } = new();

        public RecordingEgressEnforcer Enforcer { get; } = new();

        public ScriptedGate Gate { get; } = new();

        public EgressAuditor Auditor { get; }

        public EgressProxyHost Host { get; }

        public EgressPolicyService Policy { get; }

        public static EgressRules RulesFor(params string[] entries) => new([.. entries], IPAddress.Loopback, 0);

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private static async Task<bool> IsListeningAsync(IPEndPoint endpoint)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint, TestContext.Current.CancellationToken);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    [Fact]
    public async Task First_initialise_seeds_the_default_allowlist_without_asking()
    {
        await using var rig = new Rig();

        var result = await rig.Policy.InitialiseAsync("a1", IPAddress.Loopback, 0, TestContext.Current.CancellationToken);

        Assert.Equal(EgressApplyOutcome.Applied, result.Outcome);
        Assert.Empty(rig.Gate.Requests);
        var stored = await rig.Store.GetAsync("a1", TestContext.Current.CancellationToken);
        Assert.Equal(["*.gateway.example", "api.github.com"], stored!.AllowedDestinations);
        var applied = rig.Enforcer.AppliedFor("a1")!;
        Assert.NotEqual(0, applied.ProxyPort); // the enforcer always gets the real, bound port
        Assert.Equal(rig.Host.Get("a1")!.LocalEndPoint!.Port, applied.ProxyPort);
        Assert.True(await IsListeningAsync(rig.Host.Get("a1")!.LocalEndPoint!));
        Assert.Contains("\"change\":\"seed\"", Assert.Single(rig.Audit.OfKind(EgressAuditor.RuleChangeKind)).Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialise_keeps_rules_an_agent_already_has()
    {
        await using var rig = new Rig();
        await rig.Store.SaveAsync("a1", Rig.RulesFor("only.example"), TestContext.Current.CancellationToken);

        var result = await rig.Policy.InitialiseAsync("a1", IPAddress.Loopback, 0, TestContext.Current.CancellationToken);

        Assert.True(result.IsApplied);
        Assert.Equal(["only.example"], (await rig.Store.GetAsync("a1", TestContext.Current.CancellationToken))!.AllowedDestinations);
        Assert.Empty(rig.Gate.Requests);
    }

    [Fact]
    public async Task Growth_waits_for_approval_and_changes_nothing_while_pending()
    {
        await using var rig = new Rig();
        await rig.Policy.InitialiseAsync("a1", IPAddress.Loopback, 0, TestContext.Current.CancellationToken);
        var callsBefore = rig.Enforcer.Calls.Count;

        var result = await rig.Policy.ApplyAsync("a1", Rig.RulesFor("api.github.com", "*.gateway.example", "new.example"), TestContext.Current.CancellationToken);

        Assert.Equal(EgressApplyOutcome.ApprovalPending, result.Outcome);
        var request = Assert.Single(rig.Gate.Requests);
        Assert.Equal("a1", request.Agent);
        Assert.Equal(["new.example"], request.Additions);
        Assert.Equal(2, request.Tier);
        Assert.Equal(callsBefore, rig.Enforcer.Calls.Count);
        Assert.DoesNotContain("new.example", (await rig.Store.GetAsync("a1", TestContext.Current.CancellationToken))!.AllowedDestinations);
        Assert.Contains("grow-pending", rig.Audit.OfKind(EgressAuditor.RuleChangeKind).Last().Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Growth_is_applied_once_the_gate_approves_it()
    {
        await using var rig = new Rig();
        await rig.Policy.InitialiseAsync("a1", IPAddress.Loopback, 0, TestContext.Current.CancellationToken);
        rig.Gate.Answer = EgressApprovalState.Approved;

        var result = await rig.Policy.ApplyAsync("a1", Rig.RulesFor("api.github.com", "*.gateway.example", "new.example"), TestContext.Current.CancellationToken);

        Assert.True(result.IsApplied);
        Assert.Contains("new.example", (await rig.Store.GetAsync("a1", TestContext.Current.CancellationToken))!.AllowedDestinations);
        Assert.Contains("new.example", rig.Enforcer.AppliedFor("a1")!.AllowedDestinations);
        var change = rig.Audit.OfKind(EgressAuditor.RuleChangeKind).Last().Payload;
        Assert.Contains("\"change\":\"grow\"", change, StringComparison.Ordinal);
        Assert.Contains("new.example", change, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_growth_leaves_the_allowlist_alone()
    {
        await using var rig = new Rig();
        await rig.Policy.InitialiseAsync("a1", IPAddress.Loopback, 0, TestContext.Current.CancellationToken);
        rig.Gate.Answer = EgressApprovalState.Denied;

        var result = await rig.Policy.ApplyAsync("a1", Rig.RulesFor("api.github.com", "evil.example"), TestContext.Current.CancellationToken);

        Assert.Equal(EgressApplyOutcome.ApprovalDenied, result.Outcome);
        Assert.DoesNotContain("evil.example", (await rig.Store.GetAsync("a1", TestContext.Current.CancellationToken))!.AllowedDestinations);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public async Task The_approval_tier_can_tighten_but_never_drop_below_the_floor(int configured, int expected)
    {
        await using var rig = new Rig(new VmEgressOptions { AllowlistChangeApprovalTier = configured });

        await rig.Policy.ApplyAsync("a1", Rig.RulesFor("new.example"), TestContext.Current.CancellationToken);

        Assert.Equal(expected, Assert.Single(rig.Gate.Requests).Tier);
    }

    [Theory]
    [InlineData("example.com", "a.example.com", true)]
    [InlineData("*.example.com", "a.example.com", false)]
    [InlineData("example.com:443", "example.com", true)]
    [InlineData("example.com", "example.com", false)]
    [InlineData("example.com", "example.com:8443", true)]
    [InlineData("example.com", "*.example.com", true)]
    public async Task Only_widening_changes_go_to_the_gate(string current, string next, bool needsApproval)
    {
        await using var rig = new Rig();
        await rig.Store.SaveAsync("a1", Rig.RulesFor(current), TestContext.Current.CancellationToken);

        var result = await rig.Policy.ApplyAsync("a1", Rig.RulesFor(next), TestContext.Current.CancellationToken);

        Assert.Equal(needsApproval, rig.Gate.Requests.Count == 1);
        if (!needsApproval)
        {
            Assert.True(result.IsApplied);
        }
    }

    [Fact]
    public async Task A_shrink_applies_at_once_and_the_proxy_stops_allowing_what_was_removed()
    {
        await using var rig = new Rig();
        rig.Gate.Answer = EgressApprovalState.Approved;
        await rig.Policy.ApplyAsync("a1", Rig.RulesFor("a.example", "b.example"), TestContext.Current.CancellationToken);
        rig.Gate.Requests.Clear();
        rig.Gate.Answer = EgressApprovalState.Denied;

        var result = await rig.Policy.ApplyAsync("a1", Rig.RulesFor("a.example"), TestContext.Current.CancellationToken);

        Assert.True(result.IsApplied);
        Assert.Empty(rig.Gate.Requests);
        Assert.Equal(["a.example"], (await rig.Store.GetAsync("a1", TestContext.Current.CancellationToken))!.AllowedDestinations);
        var proxy = rig.Host.Get("a1")!;
        using var client = await RawClient.ConnectAsync(proxy.LocalEndPoint!);
        await client.SendAsync("CONNECT b.example:443 HTTP/1.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 403", (await client.ReadToEndAsync(TimeSpan.FromSeconds(5))).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_entries_are_rejected_and_nothing_changes()
    {
        await using var rig = new Rig();

        var result = await rig.Policy.ApplyAsync("a1", Rig.RulesFor("ok.example", "*.com"), TestContext.Current.CancellationToken);

        Assert.Equal(EgressApplyOutcome.Invalid, result.Outcome);
        Assert.Empty(rig.Gate.Requests);
        Assert.Empty(rig.Enforcer.Calls);
        Assert.Null(await rig.Store.GetAsync("a1", TestContext.Current.CancellationToken));
        Assert.Null(rig.Host.Get("a1"));
    }

    [Fact]
    public async Task Block_all_closes_the_proxy_and_the_switch_rule_together()
    {
        await using var rig = new Rig();
        await rig.Policy.InitialiseAsync("a1", IPAddress.Loopback, 0, TestContext.Current.CancellationToken);
        var endpoint = rig.Host.Get("a1")!.LocalEndPoint!;
        Assert.True(await IsListeningAsync(endpoint));

        await rig.Policy.BlockAllAsync("a1", TestContext.Current.CancellationToken);

        Assert.False(await IsListeningAsync(endpoint));
        Assert.True(rig.Enforcer.IsBlocked("a1"));
        Assert.True(await rig.Store.IsBlockedAsync("a1", TestContext.Current.CancellationToken));
        Assert.Contains("block-all", rig.Audit.OfKind(EgressAuditor.RuleChangeKind).Last().Payload, StringComparison.Ordinal);
        // The approved allowlist is kept for the release.
        Assert.NotNull(await rig.Store.GetAsync("a1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task While_blocked_apply_does_nothing_until_released()
    {
        await using var rig = new Rig();
        await rig.Policy.InitialiseAsync("a1", IPAddress.Loopback, 0, TestContext.Current.CancellationToken);
        await rig.Policy.BlockAllAsync("a1", TestContext.Current.CancellationToken);
        var calls = rig.Enforcer.Calls.Count;

        var blocked = await rig.Policy.ApplyAsync("a1", Rig.RulesFor("api.github.com"), TestContext.Current.CancellationToken);
        var initialised = await rig.Policy.InitialiseAsync("a1", IPAddress.Loopback, 0, TestContext.Current.CancellationToken);

        Assert.Equal(EgressApplyOutcome.Blocked, blocked.Outcome);
        Assert.Equal(EgressApplyOutcome.Blocked, initialised.Outcome);
        Assert.Equal(calls, rig.Enforcer.Calls.Count);
        Assert.True(rig.Host.Get("a1")!.IsStopped);

        var released = await rig.Policy.ReleaseBlockAsync("a1", TestContext.Current.CancellationToken);

        Assert.True(released.IsApplied);
        Assert.False(rig.Enforcer.IsBlocked("a1"));
        Assert.Empty(rig.Gate.Requests); // release restores what was approved; it never widens
        Assert.True(await IsListeningAsync(rig.Host.Get("a1")!.LocalEndPoint!));
    }

    [Fact]
    public async Task Block_all_still_closes_the_proxy_when_the_enforcer_fails_and_says_so()
    {
        await using var rig = new Rig();
        await rig.Policy.InitialiseAsync("a1", IPAddress.Loopback, 0, TestContext.Current.CancellationToken);
        var endpoint = rig.Host.Get("a1")!.LocalEndPoint!;
        rig.Enforcer.FailWith = new InvalidOperationException("host down");

        var error = await Assert.ThrowsAsync<AggregateException>(() => rig.Policy.BlockAllAsync("a1", TestContext.Current.CancellationToken));

        Assert.Contains(error.InnerExceptions, e => e.Message == "host down");
        Assert.False(await IsListeningAsync(endpoint));
        Assert.True(await rig.Store.IsBlockedAsync("a1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Block_all_for_an_agent_with_no_proxy_is_safe_and_idempotent()
    {
        await using var rig = new Rig();

        await rig.Policy.BlockAllAsync("ghost", TestContext.Current.CancellationToken);
        await rig.Policy.BlockAllAsync("ghost", TestContext.Current.CancellationToken);

        Assert.True(rig.Enforcer.IsBlocked("ghost"));
    }

    [Fact]
    public async Task A_failing_enforcer_leaves_no_running_proxy_and_no_saved_rules()
    {
        await using var rig = new Rig();
        rig.Enforcer.FailWith = new InvalidOperationException("no switch");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Policy.InitialiseAsync("a1", IPAddress.Loopback, 0, TestContext.Current.CancellationToken));

        Assert.True(rig.Host.Get("a1")!.IsStopped);
        Assert.Null(await rig.Store.GetAsync("a1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Module_defaults_fail_closed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Bazaar.Server.Security.Audit.IAuditLog>(new CapturingAuditLog());
        services.AddBazaarVmEgress(b => b.DefaultAllowlist.Add("api.github.com"));
        await using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<IEgressPolicy>();

        // No enforcer is registered, so the policy refuses to claim default-deny.
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.InitialiseAsync("a1", IPAddress.Loopback, 0, TestContext.Current.CancellationToken));

        // No approval gate is registered, so growth is refused.
        var grown = await policy.ApplyAsync("a1", new EgressRules(ImmutableArray.Create("x.example"), IPAddress.Loopback, 0), TestContext.Current.CancellationToken);
        Assert.Equal(EgressApplyOutcome.ApprovalDenied, grown.Outcome);
    }
}
