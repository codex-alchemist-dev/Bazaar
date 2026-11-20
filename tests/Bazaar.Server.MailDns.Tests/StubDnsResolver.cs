using System.Net;

namespace Bazaar.Server.MailDns.Tests;

/// <summary>An in-memory resolver. A name with no entry is NXDOMAIN, so a test states only what exists. Names are case-insensitive.</summary>
internal sealed class StubDnsResolver : IDnsResolver
{
    private readonly Dictionary<string, DnsLookup<string>> _txt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DnsLookup<string>> _ptr = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DnsLookup<IPAddress>> _addresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DnsLookup<DnsMxRecord>> _mx = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names asked for TXT, in order. Lets a test check that a walk stopped.</summary>
    public List<string> TxtQueries { get; } = [];

    public bool ThrowOnEveryQuery { get; set; }

    public StubDnsResolver Txt(string name, params string[] records)
    {
        _txt[name] = DnsLookup.Ok<string>(records);
        return this;
    }

    public StubDnsResolver TxtFails(string name, DnsLookupStatus status)
    {
        _txt[name] = DnsLookup.Failed<string>(status);
        return this;
    }

    public StubDnsResolver Ptr(string ip, params string[] names)
    {
        _ptr[ip] = DnsLookup.Ok<string>(names);
        return this;
    }

    public StubDnsResolver PtrFails(string ip, DnsLookupStatus status)
    {
        _ptr[ip] = DnsLookup.Failed<string>(status);
        return this;
    }

    public StubDnsResolver Address(string name, params string[] ips)
    {
        _addresses[name] = DnsLookup.Ok<IPAddress>([.. ips.Select(IPAddress.Parse)]);
        return this;
    }

    public StubDnsResolver Mx(string name, params (int Preference, string Host)[] records)
    {
        _mx[name] = DnsLookup.Ok<DnsMxRecord>([.. records.Select(r => new DnsMxRecord(r.Preference, r.Host))]);
        return this;
    }

    public Task<DnsLookup<string>> TxtAsync(string name, CancellationToken cancellationToken)
    {
        Guard();
        TxtQueries.Add(name);
        return Task.FromResult(_txt.GetValueOrDefault(name) ?? DnsLookup.Failed<string>(DnsLookupStatus.NxDomain));
    }

    public Task<DnsLookup<string>> PtrAsync(IPAddress address, CancellationToken cancellationToken)
    {
        Guard();
        return Task.FromResult(_ptr.GetValueOrDefault(address.ToString()) ?? DnsLookup.Failed<string>(DnsLookupStatus.NxDomain));
    }

    public Task<DnsLookup<IPAddress>> AddressesAsync(string name, CancellationToken cancellationToken)
    {
        Guard();
        return Task.FromResult(_addresses.GetValueOrDefault(name) ?? DnsLookup.Failed<IPAddress>(DnsLookupStatus.NxDomain));
    }

    public Task<DnsLookup<DnsMxRecord>> MxAsync(string name, CancellationToken cancellationToken)
    {
        Guard();
        return Task.FromResult(_mx.GetValueOrDefault(name) ?? DnsLookup.Failed<DnsMxRecord>(DnsLookupStatus.NxDomain));
    }

    private void Guard()
    {
        if (ThrowOnEveryQuery)
        {
            throw new InvalidOperationException("stub resolver failure");
        }
    }
}
