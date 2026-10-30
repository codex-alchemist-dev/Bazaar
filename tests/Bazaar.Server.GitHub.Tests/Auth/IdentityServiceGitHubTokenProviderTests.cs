using Bazaar.Server.GitHub.Auth;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.GitHub.Tests.Auth;

public sealed class IdentityServiceGitHubTokenProviderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static GitHubTokenScope Scope(long installationId = 1) => new(
        installationId,
        [10L, 20L],
        new Dictionary<string, string> { ["contents"] = "write", ["pull_requests"] = "write" });

    private static IdentityTokenResponse Ok(string token, DateTimeOffset expiresAt) =>
        new(200, token, expiresAt, null);

    [Fact]
    public async Task Second_call_within_lifetime_is_served_from_cache()
    {
        var time = new FakeTimeProvider(Start);
        var client = new FakeIdentityServiceTokenClient((_, _) => Task.FromResult(Ok("ghs_first", Start.AddHours(1))));
        var provider = new IdentityServiceGitHubTokenProvider(client, time);

        var first = await provider.GetTokenAsync(Scope(), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(30));
        var second = await provider.GetTokenAsync(Scope(), CancellationToken.None);

        Assert.Equal(1, client.Calls);
        Assert.Equal("ghs_first", first.Value);
        Assert.Equal("ghs_first", second.Value);
    }

    [Fact]
    public async Task Token_is_refreshed_once_it_is_within_five_minutes_of_expiry()
    {
        var time = new FakeTimeProvider(Start);
        var issued = 0;
        var client = new FakeIdentityServiceTokenClient((n, _) =>
        {
            issued = n;
            return Task.FromResult(Ok($"ghs_{n}", time.GetUtcNow().AddHours(1)));
        });
        var provider = new IdentityServiceGitHubTokenProvider(client, time);

        var first = await provider.GetTokenAsync(Scope(), CancellationToken.None);
        Assert.Equal("ghs_1", first.Value);

        // 54 minutes in: still more than five minutes before expiry, so the cached token is used.
        time.Advance(TimeSpan.FromMinutes(54));
        Assert.Equal("ghs_1", (await provider.GetTokenAsync(Scope(), CancellationToken.None)).Value);
        Assert.Equal(1, client.Calls);

        // 56 minutes in: inside the five-minute margin, so a new exchange happens.
        time.Advance(TimeSpan.FromMinutes(2));
        var refreshed = await provider.GetTokenAsync(Scope(), CancellationToken.None);

        Assert.Equal(2, client.Calls);
        Assert.Equal(2, issued);
        Assert.Equal("ghs_2", refreshed.Value);
    }

    [Fact]
    public async Task Concurrent_calls_for_one_scope_cause_exactly_one_exchange()
    {
        var time = new FakeTimeProvider(Start);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeIdentityServiceTokenClient(async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Ok("ghs_shared", Start.AddHours(1));
        });
        var provider = new IdentityServiceGitHubTokenProvider(client, time);

        var callers = Enumerable.Range(0, 16)
            .Select(_ => provider.GetTokenAsync(Scope(), CancellationToken.None))
            .ToArray();

        release.SetResult();
        var tokens = await Task.WhenAll(callers);

        Assert.Equal(1, client.Calls);
        Assert.All(tokens, t => Assert.Equal("ghs_shared", t.Value));
    }

    [Fact]
    public async Task Different_scopes_are_exchanged_separately()
    {
        var time = new FakeTimeProvider(Start);
        var client = new FakeIdentityServiceTokenClient((n, _) => Task.FromResult(Ok($"ghs_{n}", Start.AddHours(1))));
        var provider = new IdentityServiceGitHubTokenProvider(client, time);

        var a = await provider.GetTokenAsync(Scope(installationId: 1), CancellationToken.None);
        var b = await provider.GetTokenAsync(Scope(installationId: 2), CancellationToken.None);

        Assert.Equal(2, client.Calls);
        Assert.NotEqual(a.Value, b.Value);
    }

    [Fact]
    public async Task Failed_exchange_throws_without_the_token_and_is_not_cached()
    {
        var time = new FakeTimeProvider(Start);
        var client = new FakeIdentityServiceTokenClient((n, _) => Task.FromResult(
            n == 1
                ? new IdentityTokenResponse(403, "ghs_leaked_value", Start.AddHours(1), "scope_denied")
                : Ok("ghs_recovered", Start.AddHours(1))));
        var provider = new IdentityServiceGitHubTokenProvider(client, time);

        var ex = await Assert.ThrowsAsync<GitHubTokenException>(() => provider.GetTokenAsync(Scope(), CancellationToken.None));

        Assert.Equal(403, ex.StatusCode);
        Assert.Equal("scope_denied", ex.ErrorCode);
        Assert.Contains("403", ex.Message);
        Assert.Contains("scope_denied", ex.Message);
        Assert.DoesNotContain("ghs_leaked_value", ex.Message);

        // The failure was not cached, so the next call exchanges again and succeeds.
        var token = await provider.GetTokenAsync(Scope(), CancellationToken.None);
        Assert.Equal("ghs_recovered", token.Value);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task Success_without_a_token_or_expiry_throws()
    {
        var time = new FakeTimeProvider(Start);
        var client = new FakeIdentityServiceTokenClient((_, _) => Task.FromResult(new IdentityTokenResponse(200, null, null, null)));
        var provider = new IdentityServiceGitHubTokenProvider(client, time);

        var ex = await Assert.ThrowsAsync<GitHubTokenException>(() => provider.GetTokenAsync(Scope(), CancellationToken.None));

        Assert.Equal(200, ex.StatusCode);
    }

    [Fact]
    public async Task Already_expired_token_is_rejected()
    {
        var time = new FakeTimeProvider(Start);
        var client = new FakeIdentityServiceTokenClient((_, _) => Task.FromResult(Ok("ghs_old", Start.AddMinutes(-1))));
        var provider = new IdentityServiceGitHubTokenProvider(client, time);

        await Assert.ThrowsAsync<GitHubTokenException>(() => provider.GetTokenAsync(Scope(), CancellationToken.None));
    }

    [Fact]
    public async Task Scopes_whose_permission_text_collides_under_naive_joining_are_cached_separately()
    {
        var time = new FakeTimeProvider(Start);
        var client = new FakeIdentityServiceTokenClient((n, _) => Task.FromResult(Ok($"ghs_{n}", Start.AddHours(1))));
        var provider = new IdentityServiceGitHubTokenProvider(client, time);

        var first = await provider.GetTokenAsync(
            new GitHubTokenScope(1, [1L], new Dictionary<string, string> { ["a"] = "b=c" }), CancellationToken.None);
        var second = await provider.GetTokenAsync(
            new GitHubTokenScope(1, [1L], new Dictionary<string, string> { ["a=b"] = "c" }), CancellationToken.None);

        Assert.Equal(2, client.Calls);
        Assert.NotEqual(first.Value, second.Value);
    }

    [Fact]
    public async Task Hostile_error_code_is_replaced_in_message_and_exception()
    {
        var time = new FakeTimeProvider(Start);
        var client = new FakeIdentityServiceTokenClient((_, _) => Task.FromResult(
            new IdentityTokenResponse(403, null, null, "bad\nINJECTED line\r\n\u0007")));
        var provider = new IdentityServiceGitHubTokenProvider(client, time);

        var ex = await Assert.ThrowsAsync<GitHubTokenException>(() => provider.GetTokenAsync(Scope(), CancellationToken.None));

        Assert.Equal("unknown", ex.ErrorCode);
        Assert.Contains("error code unknown", ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.DoesNotContain('\r', ex.Message);
    }

    [Fact]
    public async Task Long_valid_error_code_is_capped_at_64_characters()
    {
        var time = new FakeTimeProvider(Start);
        var client = new FakeIdentityServiceTokenClient((_, _) => Task.FromResult(
            new IdentityTokenResponse(422, null, null, new string('e', 200))));
        var provider = new IdentityServiceGitHubTokenProvider(client, time);

        var ex = await Assert.ThrowsAsync<GitHubTokenException>(() => provider.GetTokenAsync(Scope(), CancellationToken.None));

        Assert.Equal(64, ex.ErrorCode!.Length);
    }

    [Fact]
    public void Token_to_string_does_not_contain_the_value()
    {
        var token = new GitHubToken("ghs_super_secret_value", Start.AddHours(1));
        var response = Ok("ghs_super_secret_value", Start.AddHours(1));

        Assert.DoesNotContain("ghs_super_secret_value", token.ToString());
        Assert.DoesNotContain("ghs_super_secret_value", response.ToString());
    }
}

/// <summary>Fake Identity Service client. The delegate receives the 1-based call number.</summary>
internal sealed class FakeIdentityServiceTokenClient(
    Func<int, CancellationToken, Task<IdentityTokenResponse>> respond) : IIdentityServiceTokenClient
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Task<IdentityTokenResponse> ExchangeAsync(GitHubTokenScope scope, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _calls);
        return respond(n, cancellationToken);
    }
}
