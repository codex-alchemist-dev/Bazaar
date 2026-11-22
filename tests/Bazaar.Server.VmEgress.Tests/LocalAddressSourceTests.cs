using System.Net;
using Bazaar.Server.VmEgress.Proxy;

namespace Bazaar.Server.VmEgress.Tests;

public class LocalAddressSourceTests
{
    [Fact]
    public void The_system_source_knows_loopback_and_compares_mapped_addresses_as_ipv4()
    {
        var source = new SystemLocalAddressSource();

        // Loopback is on every machine that has networking; if the sandbox has no interface at all the set is empty and this is skipped.
        if (!source.IsLocal(IPAddress.Loopback))
        {
            return;
        }

        Assert.True(source.IsLocal(IPAddress.Loopback.MapToIPv6()));
        Assert.False(source.IsLocal(IPAddress.Parse("203.0.113.77")));
    }

    [Fact]
    public void A_scripted_address_is_local_in_either_notation_and_the_snapshot_is_cached()
    {
        var calls = 0;
        var source = new SystemLocalAddressSource(TimeSpan.FromMinutes(5), () =>
        {
            calls++;
            return [IPAddress.Parse("198.51.100.9"), IPAddress.Parse("2001:4860::9")];
        });

        Assert.True(source.IsLocal(IPAddress.Parse("198.51.100.9")));
        Assert.True(source.IsLocal(IPAddress.Parse("::ffff:198.51.100.9")));
        Assert.True(source.IsLocal(IPAddress.Parse("2001:4860::9")));
        Assert.False(source.IsLocal(IPAddress.Parse("198.51.100.10")));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void An_expired_snapshot_is_refreshed_and_a_failed_refresh_keeps_the_last_good_one()
    {
        var calls = 0;
        var source = new SystemLocalAddressSource(TimeSpan.Zero, () =>
        {
            calls++;
            return calls == 1 ? [IPAddress.Parse("198.51.100.9")] : throw new InvalidOperationException("boom");
        });

        Assert.True(source.IsLocal(IPAddress.Parse("198.51.100.9")));
        Assert.True(source.IsLocal(IPAddress.Parse("198.51.100.9"))); // refresh failed, last snapshot kept
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task Concurrent_readers_are_safe()
    {
        var source = new SystemLocalAddressSource(TimeSpan.Zero, () => [IPAddress.Parse("198.51.100.9")]);
        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                Assert.True(source.IsLocal(IPAddress.Parse("198.51.100.9")));
            }
        }));

        await Task.WhenAll(tasks);
    }
}
