using System.Text;
using Bazaar.Server.Security.Credentials;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Security.Tests.Credentials;

public class InMemoryLeasedCredentialMatcherTests
{
    private const string _secret = "ghs_Example0Token1Value";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryLeasedCredentialMatcher _matcher;

    public InMemoryLeasedCredentialMatcherTests() => _matcher = new InMemoryLeasedCredentialMatcher(_time);

    private DateTimeOffset InAnHour => _time.GetUtcNow().AddHours(1);

    private void Track(string agent = "agent-a", string lease = "lease-1", string value = _secret) =>
        _matcher.Track(agent, lease, Encoding.UTF8.GetBytes(value), InAnHour);

    private ValueTask<bool> Contains(string text, string agent = "agent-a") =>
        _matcher.ContainsLeasedCredentialAsync(agent, text, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Text_holding_a_leased_value_matches()
    {
        Track();

        Assert.True(await Contains($"type this: {_secret} then press enter"));
        Assert.True(await Contains(_secret));
    }

    [Fact]
    public async Task Text_without_the_value_does_not_match()
    {
        Track();

        Assert.False(await Contains("hello world"));
        Assert.False(await Contains(_secret[..^1]));
        Assert.False(await Contains(""));
    }

    [Fact]
    public async Task Matching_is_case_sensitive()
    {
        Track();

        Assert.False(await Contains(_secret.ToUpperInvariant()));
    }

    [Fact]
    public async Task Non_ascii_values_match_on_their_utf8_bytes()
    {
        Track(value: "pässwörd-ключ");

        Assert.True(await Contains("x pässwörd-ключ y"));
        Assert.False(await Contains("x passwort-ключ y"));
    }

    [Fact]
    public async Task Another_agents_lease_does_not_match()
    {
        Track(agent: "agent-b");

        Assert.False(await Contains(_secret));
        Assert.True(await Contains(_secret, agent: "agent-b"));
    }

    [Fact]
    public async Task An_expired_lease_no_longer_matches()
    {
        Track();
        _time.Advance(TimeSpan.FromHours(1));

        Assert.False(await Contains(_secret));
    }

    [Fact]
    public async Task A_released_lease_no_longer_matches()
    {
        Track();
        Track(lease: "lease-2", value: "other-secret-value");
        _matcher.Release("agent-a", "lease-1");

        Assert.False(await Contains(_secret));
        Assert.True(await Contains("other-secret-value"));
    }

    [Fact]
    public async Task Releasing_an_agent_drops_every_lease()
    {
        Track();
        Track(lease: "lease-2", value: "other-secret-value");
        _matcher.ReleaseAgent("agent-a");

        Assert.False(await Contains(_secret));
        Assert.False(await Contains("other-secret-value"));
    }

    [Fact]
    public async Task Tracking_the_same_lease_again_replaces_its_value()
    {
        Track();
        Track(value: "rotated-secret-value");

        Assert.False(await Contains(_secret));
        Assert.True(await Contains("rotated-secret-value"));
    }

    [Fact]
    public async Task Releasing_unknown_leases_does_nothing()
    {
        _matcher.Release("agent-a", "missing");
        _matcher.ReleaseAgent("agent-z");

        Assert.False(await Contains(_secret));
    }

    [Fact]
    public async Task The_caller_buffer_is_copied()
    {
        var bytes = Encoding.UTF8.GetBytes(_secret);
        _matcher.Track("agent-a", "lease-1", bytes, InAnHour);
        Array.Clear(bytes);

        Assert.True(await Contains(_secret));
    }

    [Fact]
    public void Track_rejects_invalid_input()
    {
        var value = Encoding.UTF8.GetBytes(_secret);

        Assert.Throws<ArgumentException>(() => _matcher.Track(" ", "lease-1", value, InAnHour));
        Assert.Throws<ArgumentException>(() => _matcher.Track("agent-a", "", value, InAnHour));
        Assert.Throws<ArgumentException>(() => _matcher.Track("agent-a", "lease-1", ReadOnlySpan<byte>.Empty, InAnHour));
        Assert.Throws<ArgumentOutOfRangeException>(() => _matcher.Track("agent-a", "lease-1", value, _time.GetUtcNow()));
    }

    [Fact]
    public async Task Contains_rejects_a_blank_agent_and_null_text()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await Contains("x", agent: " "));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await Contains(null!));
    }

    [Fact]
    public async Task A_cancelled_check_throws()
    {
        Track();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await _matcher.ContainsLeasedCredentialAsync("agent-a", _secret, cts.Token));
    }

    [Fact]
    public void The_contract_returns_only_a_boolean()
    {
        var method = typeof(ILeasedCredentialMatcher).GetMethod(nameof(ILeasedCredentialMatcher.ContainsLeasedCredentialAsync));

        Assert.NotNull(method);
        Assert.Equal(typeof(ValueTask<bool>), method.ReturnType);
        Assert.Single(typeof(ILeasedCredentialMatcher).GetMethods());
    }
}
