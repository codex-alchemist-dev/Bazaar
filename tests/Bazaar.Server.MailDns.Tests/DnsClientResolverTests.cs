using System.Net;

namespace Bazaar.Server.MailDns.Tests;

/// <summary>The shipped resolver: failures are results, and DNS text is capped. No test here needs the internet.</summary>
public sealed class DnsClientResolverTests
{
    [Fact]
    public void ShapeTxt_caps_the_record_count_and_each_record_length()
    {
        var values = Enumerable.Range(0, 100).Select(_ => new string('x', MailDnsLimits.MaxTxtLength + 500));

        var shaped = MailDnsLimits.ShapeTxt(values);

        Assert.Equal(MailDnsLimits.MaxRecords, shaped.Count);
        Assert.All(shaped, text => Assert.Equal(MailDnsLimits.MaxTxtLength, text.Length));
    }

    [Fact]
    public void Safe_reduces_dns_text_to_a_plain_character_set_and_a_length_cap()
    {
        var safe = MailDnsLimits.Safe("<b>evil</b>\n" + new string('a', 500));

        Assert.DoesNotContain("<", safe);
        Assert.DoesNotContain("\n", safe);
        Assert.True(safe.Length <= 80);
        Assert.Equal("selector._domainkey.example.com", MailDnsLimits.Safe("selector._domainkey.example.com"));
    }

    [Fact]
    public async Task An_unreachable_name_server_gives_a_failure_result_not_an_exception()
    {
        var options = new DnsResolverOptions { QueryTimeout = TimeSpan.FromMilliseconds(200) };
        options.Nameservers.Add(new IPEndPoint(IPAddress.Loopback, 9)); // nothing answers on the discard port
        var resolver = new DnsClientResolver(options);

        var txt = await resolver.TxtAsync("example.com", CancellationToken.None);
        var ptr = await resolver.PtrAsync(IPAddress.Parse("192.0.2.1"), CancellationToken.None);
        var addresses = await resolver.AddressesAsync("example.com", CancellationToken.None);
        var mx = await resolver.MxAsync("example.com", CancellationToken.None);

        Assert.All(
            new[] { txt.Status, ptr.Status, addresses.Status, mx.Status },
            status => Assert.True(status is DnsLookupStatus.Timeout or DnsLookupStatus.ServFail, status.ToString()));
        Assert.Empty(txt.Records);
    }

    [Fact]
    public async Task Cancelling_the_caller_token_throws_instead_of_reporting_a_timeout()
    {
        var options = new DnsResolverOptions { QueryTimeout = TimeSpan.FromSeconds(5) };
        options.Nameservers.Add(new IPEndPoint(IPAddress.Loopback, 9));
        var resolver = new DnsClientResolver(options);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.TxtAsync("example.com", cts.Token));
    }
}
