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
        var provider = new ValkeyCacheProvider(new ValkeyCacheOptions { Managed = true }, null, factory, source);
        var ct = TestContext.Current.CancellationToken;

        await provider.SetAsync("k", new byte[] { 1 }, TimeSpan.FromMinutes(1), ct);
        var value = await provider.GetAsync("k", ct);
        var status = provider.Probe();

        Assert.Null(value);
        Assert.Empty(factory.ConnectionStrings);
        Assert.False(status.Usable);
        Assert.Equal("connection failed (InvalidOperationException)", status.Reason);
    }

    [Fact]
    public void Managed_cache_status_never_names_the_password()
    {
        var source = new TestConnectionSource { Value = null };
        var provider = new ValkeyCacheProvider(new ValkeyCacheOptions { Managed = true }, null, new TestClientFactory(), source);

        var status = provider.Probe();

        Assert.DoesNotContain("password", status.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
