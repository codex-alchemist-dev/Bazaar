using System.Text;
using Bazaar.Server.Identity;
using Bazaar.Server.Identity.Leases;
using Bazaar.Server.Security.Credentials;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Security.Tests.Credentials;

public class LeasedCredentialObserverTests
{
    private const string _secret = "ghs_Example0Token1Value";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeSource _source = new();
    private readonly InMemoryLeasedCredentialMatcher _matcher;
    private readonly LeasedCredentialObserver _observer;

    public LeasedCredentialObserverTests()
    {
        _matcher = new InMemoryLeasedCredentialMatcher(_time);
        _observer = new LeasedCredentialObserver(_matcher, _source, NullLogger<LeasedCredentialObserver>.Instance);
    }

    private CredentialLease Lease(string agent = "agent-a", int hours = 1) => new(
        new LeaseId(Guid.NewGuid()), agent, new CredentialId(Guid.NewGuid()), "github:push",
        _time.GetUtcNow(), _time.GetUtcNow().AddHours(hours), null, null);

    [Fact]
    public async Task Issued_lease_makes_its_value_match_for_that_agent_only()
    {
        _source.Value = _secret;
        await _observer.OnIssuedAsync(Lease(), CancellationToken.None);

        Assert.True(await _matcher.ContainsLeasedCredentialAsync("agent-a", "token " + _secret, CancellationToken.None));
        Assert.False(await _matcher.ContainsLeasedCredentialAsync("agent-b", "token " + _secret, CancellationToken.None));
    }

    [Fact]
    public async Task Lease_with_no_resolvable_value_tracks_nothing()
    {
        _source.Value = null;
        await _observer.OnIssuedAsync(Lease(), CancellationToken.None);

        Assert.False(await _matcher.ContainsLeasedCredentialAsync("agent-a", _secret, CancellationToken.None));
    }

    [Fact]
    public async Task Value_stops_matching_at_lease_expiry()
    {
        _source.Value = _secret;
        await _observer.OnIssuedAsync(Lease(hours: 1), CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(2));

        Assert.False(await _matcher.ContainsLeasedCredentialAsync("agent-a", _secret, CancellationToken.None));
    }

    [Fact]
    public async Task Agent_revocation_releases_only_that_agents_values()
    {
        _source.Value = _secret;
        await _observer.OnIssuedAsync(Lease("agent-a"), CancellationToken.None);
        await _observer.OnIssuedAsync(Lease("agent-b"), CancellationToken.None);

        await _observer.OnRevokedAsync(new AgentRevocation("agent-a"), CancellationToken.None);

        Assert.False(await _matcher.ContainsLeasedCredentialAsync("agent-a", _secret, CancellationToken.None));
        Assert.True(await _matcher.ContainsLeasedCredentialAsync("agent-b", _secret, CancellationToken.None));
    }

    [Fact]
    public async Task Global_revocation_releases_every_value()
    {
        _source.Value = _secret;
        await _observer.OnIssuedAsync(Lease("agent-a"), CancellationToken.None);
        await _observer.OnIssuedAsync(Lease("agent-b"), CancellationToken.None);

        await _observer.OnRevokedAsync(new GlobalRevocation(), CancellationToken.None);

        Assert.False(await _matcher.ContainsLeasedCredentialAsync("agent-a", _secret, CancellationToken.None));
        Assert.False(await _matcher.ContainsLeasedCredentialAsync("agent-b", _secret, CancellationToken.None));
    }

    [Fact]
    public async Task Resolved_buffer_is_zeroed_after_use()
    {
        _source.Value = _secret;
        await _observer.OnIssuedAsync(Lease(), CancellationToken.None);

        Assert.NotNull(_source.LastBuffer);
        Assert.All(_source.LastBuffer, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Registration_adds_matcher_and_observer_and_needs_a_value_source()
    {
        var services = new ServiceCollection().AddLogging().AddLeasedCredentialMatcher();
        using (var withoutSource = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false }))
        {
            Assert.Throws<InvalidOperationException>(() => withoutSource.GetServices<ILeaseObserver>().ToList());
        }

        services.AddSingleton<ILeasedCredentialValueSource>(_source);
        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<ILeaseObserver>());
        Assert.Same(provider.GetRequiredService<InMemoryLeasedCredentialMatcher>(), provider.GetRequiredService<ILeasedCredentialMatcher>());
    }

    private sealed class FakeSource : ILeasedCredentialValueSource
    {
        public string? Value { get; set; }

        public byte[]? LastBuffer { get; private set; }

        public ValueTask<byte[]?> ResolveAsync(string agentId, CredentialId credentialId, CancellationToken ct)
        {
            LastBuffer = Value is null ? null : Encoding.UTF8.GetBytes(Value);
            return ValueTask.FromResult(LastBuffer);
        }
    }
}
