namespace Bazaar.Server.Valkey.Tests;

/// <summary>The cache provider in managed mode: it takes the current connection from the supervisor and degrades while the child is down.</summary>
public class ValkeyManagedCacheTests
{
    [Fact]
    public void Managed_cache_refuses_an_endpoint_or_secret_reference()
    {
        var provider = new ValkeyCacheProvider(
            new ValkeyCacheOptions { Managed = true, Endpoint = "cache.internal:6379" },
            null,
            new TestClientFactory(),
            new TestConnectionSource { Value = "127.0.0.1:1" });

        var status = provider.Probe();

        Assert.False(status.Usable);
        Assert.Equal("a managed cache takes no endpoint or secret reference", status.Reason);
    }

    [Fact]
    public void Managed_cache_probe_is_usable_when_the_child_answers()
    {
        var factory = new TestClientFactory();
        var provider = new ValkeyCacheProvider(new ValkeyCacheOptions { Managed = true }, null, factory, new TestConnectionSource { Value = "127.0.0.1:1" });

        var status = provider.Probe();

        Assert.True(status.Usable);
        Assert.Equal("127.0.0.1:1", Assert.Single(factory.ConnectionStrings));
    }

    [Fact]
    public async Task Managed_cache_follows_a_child_that_moved_to_a_new_port()
    {
        var source = new TestConnectionSource { Value = "127.0.0.1:1000,password=p" };
        var factory = new TestClientFactory();
        var provider = new ValkeyCacheProvider(new ValkeyCacheOptions { Managed = true }, null, factory, source);
        var ct = TestContext.Current.CancellationToken;
        await provider.SetAsync("k", new byte[] { 1 }, TimeSpan.FromMinutes(1), ct);

        source.Value = "127.0.0.1:2000,password=p";
        await provider.SetAsync("k", new byte[] { 2 }, TimeSpan.FromMinutes(1), ct);

        Assert.Equal(["127.0.0.1:1000,password=p", "127.0.0.1:2000,password=p"], factory.ConnectionStrings);
        Assert.True(factory.Clients[0].Disposed, "the client for the old address is disposed");
        Assert.Equal(new byte[] { 2 }, factory.Clients[1].Store.Values.Single());
    }

    [Fact]
    public async Task Managed_cache_degrades_while_the_child_is_down_and_reports_it_unusable()
    {
        var source = new TestConnectionSource { Value = null };
        var factory = new TestClientFactory();
        var provider = new ValkeyCacheProvider(new ValkeyCacheOptions { Managed = true, StartWindowSeconds = 1 }, null, factory, source);
        var ct = TestContext.Current.CancellationToken;

        await provider.SetAsync("k", new byte[] { 1 }, TimeSpan.FromMinutes(1), ct);
        var value = await provider.GetAsync("k", ct);
        var status = provider.Probe();

        Assert.Null(value);
        Assert.Empty(factory.ConnectionStrings);
        Assert.False(status.Usable);
        Assert.Equal("managed server did not answer within 1 seconds of start", status.Reason);
    }

    [Fact]
    public void Managed_cache_status_never_names_the_password()
    {
        var source = new TestConnectionSource { Value = null };
        var provider = new ValkeyCacheProvider(new ValkeyCacheOptions { Managed = true, StartWindowSeconds = 1 }, null, new TestClientFactory(), source);

        var status = provider.Probe();

        Assert.DoesNotContain("password", status.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_start_window_defaults_to_fifteen_seconds()
    {
        Assert.Equal(15, new ValkeyCacheOptions().StartWindowSeconds);
    }

    [Fact]
    public void The_first_probe_retries_inside_the_start_window_until_the_child_answers()
    {
        var source = new StartingConnectionSource(readyAfterAttempts: 4);
        var provider = new ValkeyCacheProvider(
            new ValkeyCacheOptions { Managed = true, StartWindowSeconds = 1 },
            null,
            new TestClientFactory(),
            source);

        var status = provider.Probe();

        Assert.True(status.Usable);
        // Four refused attempts and the ready one inside the window, then the ping's own connection lookup.
        Assert.Equal(6, source.Attempts);
    }

    [Fact]
    public void An_expired_start_window_reports_unusable_without_throwing()
    {
        var source = new StartingConnectionSource(readyAfterAttempts: int.MaxValue);
        var provider = new ValkeyCacheProvider(
            new ValkeyCacheOptions { Managed = true, StartWindowSeconds = 1 },
            null,
            new TestClientFactory(),
            source);

        var status = provider.Probe();

        Assert.False(status.Usable);
        Assert.Equal("managed server did not answer within 1 seconds of start", status.Reason);
        Assert.True(source.Attempts > 1, "the probe retried before giving up");
    }

    [Fact]
    public void Only_the_first_probe_waits_for_the_window()
    {
        var source = new StartingConnectionSource(readyAfterAttempts: int.MaxValue);
        var provider = new ValkeyCacheProvider(
            new ValkeyCacheOptions { Managed = true, StartWindowSeconds = 1 },
            null,
            new TestClientFactory(),
            source);
        provider.Probe();
        var attemptsAfterFirst = source.Attempts;

        var status = provider.Probe();

        Assert.False(status.Usable);
        Assert.Equal(attemptsAfterFirst + 1, source.Attempts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void A_start_window_outside_one_to_120_seconds_is_a_configuration_error(int seconds)
    {
        var source = new StartingConnectionSource(readyAfterAttempts: 0);
        var provider = new ValkeyCacheProvider(
            new ValkeyCacheOptions { Managed = true, StartWindowSeconds = seconds },
            null,
            new TestClientFactory(),
            source);

        var status = provider.Probe();

        Assert.False(status.Usable);
        Assert.Equal("StartWindowSeconds must be between 1 and 120", status.Reason);
        Assert.Equal(0, source.Attempts);
    }

    /// <summary>A managed connection that is not ready until a number of attempts have been made, the way a starting child is.</summary>
    private sealed class StartingConnectionSource(int readyAfterAttempts) : IValkeyConnectionSource
    {
        public int Attempts { get; private set; }

        public ValueTask<string> GetConnectionStringAsync(CancellationToken ct)
        {
            Attempts++;
            return Attempts > readyAfterAttempts
                ? ValueTask.FromResult("127.0.0.1:1")
                : ValueTask.FromException<string>(new InvalidOperationException("The managed Valkey server is not running."));
        }
    }
}
