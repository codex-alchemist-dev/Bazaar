using System.Collections.Immutable;
using System.Net;
using Cdax.Core;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.VmEgress;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.BrowserPool.Egress.Tests;

public class EgressTests
{
    private sealed class ScriptedStore : IEgressAllowlistStore
    {
        public Func<string, CancellationToken, Task<EgressRules?>> Get { get; set; } = (_, _) => Task.FromResult<EgressRules?>(null);

        public List<string> Asked { get; } = [];

        public Task<EgressRules?> GetAsync(string agentId, CancellationToken ct = default)
        {
            Asked.Add(agentId);
            return Get(agentId, ct);
        }

        public Task SaveAsync(string agentId, EgressRules rules, CancellationToken ct = default) => throw new InvalidOperationException("Read only.");

        public Task<bool> IsBlockedAsync(string agentId, CancellationToken ct = default) => throw new InvalidOperationException("Read only.");

        public Task<long> GetBlockEpochAsync(string agentId, CancellationToken ct = default) => throw new InvalidOperationException("Read only.");

        public Task<long> BlockAsync(string agentId, CancellationToken ct = default) => throw new InvalidOperationException("Read only.");

        public Task<bool> TryReleaseBlockAsync(string agentId, long expectedEpoch, CancellationToken ct = default) => throw new InvalidOperationException("Read only.");
    }

    private static EgressRules Rules(string address, int port) => new(ImmutableArray.Create("example.com"), IPAddress.Parse(address), port);

    private static AllowlistStorePoolEgressPolicy Policy(IEgressAllowlistStore? store, FakeTimeProvider? time = null, int timeoutSeconds = 5) =>
        new(store, time ?? new FakeTimeProvider(), new PoolEgressOptions { ReadTimeoutSeconds = timeoutSeconds });

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static ScriptedStore StoreWith(EgressRules? rules) => new() { Get = (_, _) => Task.FromResult(rules) };

    [Fact]
    public async Task A_stored_proxy_is_returned_as_an_endpoint()
    {
        var store = StoreWith(Rules("10.20.0.1", 3128));

        var endpoint = await Policy(store).ResolveAsync("agent-1", TestContext.Current.CancellationToken);

        Assert.Equal(new PoolEgressEndpoint("10.20.0.1", 3128), endpoint);
        Assert.Equal(["agent-1"], store.Asked);
    }

    [Fact]
    public async Task No_store_means_no_browser()
    {
        Assert.Null(await Policy(null).ResolveAsync("agent-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_agent_with_no_stored_rules_gets_no_browser()
    {
        Assert.Null(await Policy(StoreWith(null)).ResolveAsync("agent-1", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_agent_id_is_refused_without_asking_the_store(string agentId)
    {
        var store = StoreWith(Rules("10.20.0.1", 3128));

        Assert.Null(await Policy(store).ResolveAsync(agentId, TestContext.Current.CancellationToken));
        Assert.Empty(store.Asked);
    }

    [Fact]
    public async Task A_store_that_throws_gives_no_browser()
    {
        var store = new ScriptedStore { Get = (_, _) => throw new InvalidOperationException("down") };

        Assert.Null(await Policy(store).ResolveAsync("agent-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_store_that_faults_later_gives_no_browser()
    {
        var store = new ScriptedStore { Get = async (_, _) => { await Task.Yield(); throw new InvalidOperationException("down"); } };

        Assert.Null(await Policy(store).ResolveAsync("agent-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_read_that_does_not_finish_in_time_gives_no_browser()
    {
        var time = new FakeTimeProvider();
        var store = new ScriptedStore { Get = (_, _) => new TaskCompletionSource<EgressRules?>().Task };
        var policy = Policy(store, time, timeoutSeconds: 3);

        Task<PoolEgressEndpoint?> pending = policy.ResolveAsync("agent-1", TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(3));

        Assert.Null(await pending);
    }

    [Fact]
    public async Task The_caller_cancelling_propagates_and_is_not_a_verdict()
    {
        var store = new ScriptedStore { Get = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; } };
        var policy = Policy(store);
        using var cts = new CancellationTokenSource();

        Task<PoolEgressEndpoint?> pending = policy.ResolveAsync("agent-1", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.ResolveAsync("agent-1", cts.Token));
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    public async Task An_address_that_is_not_a_usable_ipv4_literal_gives_no_browser(string address)
    {
        Assert.Null(await Policy(StoreWith(Rules(address, 3128))).ResolveAsync("agent-1", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task A_port_outside_1_to_65535_gives_no_browser(int port)
    {
        Assert.Null(await Policy(StoreWith(Rules("10.20.0.1", port))).ResolveAsync("agent-1", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public async Task The_port_range_ends_are_accepted(int port)
    {
        Assert.Equal(new PoolEgressEndpoint("10.20.0.1", port), await Policy(StoreWith(Rules("10.20.0.1", port))).ResolveAsync("agent-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_allowlist_is_never_part_of_the_answer()
    {
        var endpoint = await Policy(StoreWith(Rules("10.20.0.1", 3128))).ResolveAsync("agent-1", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("example.com", endpoint!.ToString());
    }

    [Fact]
    public void Options_are_validated()
    {
        Assert.Equal(5, PoolEgressOptions.Bind(Config()).ReadTimeoutSeconds);
        Assert.Equal(9, PoolEgressOptions.Bind(Config(("Bazaar:BrowserPoolEgress:ReadTimeoutSeconds", "9"))).ReadTimeoutSeconds);
        Assert.Throws<InvalidOperationException>(() => PoolEgressOptions.Bind(Config(("Bazaar:BrowserPoolEgress:ReadTimeoutSeconds", "0"))));
        Assert.Throws<InvalidOperationException>(() => PoolEgressOptions.Bind(Config(("Bazaar:BrowserPoolEgress:ReadTimeoutSeconds", "61"))));
        Assert.Throws<InvalidOperationException>(() => PoolEgressOptions.Bind(Config(("Bazaar:BrowserPoolEgress:ReadTimeoutSeconds", "x"))));
    }

    [Fact]
    public void Manifest_is_as_designed()
    {
        ComponentManifest m = new BrowserPoolEgressComponent().Manifest;

        Assert.Equal("browser-pool-egress", m.Id);
        Assert.Equal(ComponentKind.Adapter, m.Kind);
        Assert.Equal(["browser-pool"], m.DependsOn);
        Assert.Equal(["browser-pool:egress"], m.Provides);
    }

    private static ServiceProvider Build(bool egressFirst, Action<IServiceCollection>? host = null)
    {
        var services = new ServiceCollection();
        IConfiguration config = Config();
        if (egressFirst)
        {
            new BrowserPoolEgressComponent().AddServices(services, config);
            new BrowserPoolComponent().AddServices(services, config);
        }
        else
        {
            new BrowserPoolComponent().AddServices(services, config);
            new BrowserPoolEgressComponent().AddServices(services, config);
        }

        host?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_real_policy_replaces_the_default_in_either_order(bool egressFirst)
    {
        using ServiceProvider sp = Build(egressFirst, s => s.AddSingleton<IEgressAllowlistStore>(StoreWith(Rules("10.20.0.1", 3128))));

        Assert.IsType<AllowlistStorePoolEgressPolicy>(sp.GetRequiredService<IPoolEgressPolicy>());
        Assert.Single(sp.GetServices<IPoolEgressPolicy>());
        Assert.Equal(new PoolEgressEndpoint("10.20.0.1", 3128), await sp.GetRequiredService<IPoolEgressPolicy>().ResolveAsync("agent-1", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Without_an_allowlist_store_no_agent_has_a_proxy(bool egressFirst)
    {
        using ServiceProvider sp = Build(egressFirst);

        Assert.Null(await sp.GetRequiredService<IPoolEgressPolicy>().ResolveAsync("agent-1", TestContext.Current.CancellationToken));
    }
}
