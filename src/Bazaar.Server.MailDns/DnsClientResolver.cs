using System.Net;
using System.Net.Sockets;
using DnsClient;
using DnsClient.Protocol;

namespace Bazaar.Server.MailDns;

/// <summary>Settings for <see cref="DnsClientResolver"/>.</summary>
public sealed class DnsResolverOptions
{
    /// <summary>How long one query may take before it is reported as a timeout.</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Name servers to ask. Empty means the host's configured resolvers.</summary>
    public IList<IPEndPoint> Nameservers { get; } = [];
}

/// <summary>
/// The shipped <see cref="IDnsResolver"/>, over the DnsClient package. Each query has a timeout; a timeout, NXDOMAIN, SERVFAIL or
/// a network error comes back as a <see cref="DnsLookupStatus"/>, never an exception (only caller cancellation throws). Answers
/// are capped at <see cref="MailDnsLimits.MaxRecords"/> records and <see cref="MailDnsLimits.MaxTxtLength"/> characters per TXT,
/// because DNS text is untrusted.
/// </summary>
public sealed class DnsClientResolver : IDnsResolver
{
    private readonly LookupClient _client;
    private readonly TimeSpan _timeout;

    public DnsClientResolver(DnsResolverOptions? options = null)
    {
        options ??= new DnsResolverOptions();
        _timeout = options.QueryTimeout > TimeSpan.Zero ? options.QueryTimeout : TimeSpan.FromSeconds(3);
        var clientOptions = options.Nameservers.Count > 0
            ? new LookupClientOptions([.. options.Nameservers])
            : new LookupClientOptions();
        clientOptions.Timeout = _timeout;
        clientOptions.Retries = 1;
        clientOptions.UseCache = false;
        clientOptions.ThrowDnsErrors = false;
        _client = new LookupClient(clientOptions);
    }

    public async Task<DnsLookup<string>> TxtAsync(string name, CancellationToken cancellationToken) =>
        await QueryAsync<string>(
            token => _client.QueryAsync(name, QueryType.TXT, cancellationToken: token),
            answer => MailDnsLimits.ShapeTxt(answer.Answers.TxtRecords().Select(record => string.Concat(record.Text))),
            cancellationToken).ConfigureAwait(false);

    public async Task<DnsLookup<string>> PtrAsync(IPAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        return await QueryAsync<string>(
            token => _client.QueryReverseAsync(address, cancellationToken: token),
            answer => [.. answer.Answers.PtrRecords().Select(record => record.PtrDomainName.Value).Take(MailDnsLimits.MaxRecords)],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DnsLookup<IPAddress>> AddressesAsync(string name, CancellationToken cancellationToken)
    {
        var v4 = await QueryAsync(token => _client.QueryAsync(name, QueryType.A, cancellationToken: token), Addresses, cancellationToken).ConfigureAwait(false);
        var v6 = await QueryAsync(token => _client.QueryAsync(name, QueryType.AAAA, cancellationToken: token), Addresses, cancellationToken).ConfigureAwait(false);
        if (v4.Status == DnsLookupStatus.Ok || v6.Status == DnsLookupStatus.Ok)
        {
            return DnsLookup.Ok<IPAddress>([.. v4.Records.Concat(v6.Records).Take(MailDnsLimits.MaxRecords)]);
        }

        // Both failed. A missing name is reported only when both say so; any other failure wins, as it is the less certain one.
        return v4.Status == DnsLookupStatus.NxDomain ? v6 : v4;
    }

    public async Task<DnsLookup<DnsMxRecord>> MxAsync(string name, CancellationToken cancellationToken) =>
        await QueryAsync<DnsMxRecord>(
            token => _client.QueryAsync(name, QueryType.MX, cancellationToken: token),
            answer => [.. answer.Answers.MxRecords().Select(record => new DnsMxRecord(record.Preference, record.Exchange.Value)).Take(MailDnsLimits.MaxRecords)],
            cancellationToken).ConfigureAwait(false);

    private static List<IPAddress> Addresses(IDnsQueryResponse answer) =>
    [
        .. answer.Answers.OfType<DnsClient.Protocol.AddressRecord>().Select(record => record.Address).Take(MailDnsLimits.MaxRecords),
    ];

    private async Task<DnsLookup<T>> QueryAsync<T>(
        Func<CancellationToken, Task<IDnsQueryResponse>> ask,
        Func<IDnsQueryResponse, IReadOnlyList<T>> read,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The client has its own timeout; this one is the backstop for a stuck socket.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout * 2 + TimeSpan.FromSeconds(1));
        try
        {
            var response = await ask(deadline.Token).ConfigureAwait(false);

            if (response.HasError)
            {
                return DnsLookup.Failed<T>(response.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain
                    ? DnsLookupStatus.NxDomain
                    : DnsLookupStatus.ServFail);
            }

            return DnsLookup.Ok(read(response));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return DnsLookup.Failed<T>(DnsLookupStatus.Timeout);
        }
        catch (DnsResponseException ex) when (ex.Code == DnsClient.DnsResponseCode.ConnectionTimeout)
        {
            return DnsLookup.Failed<T>(DnsLookupStatus.Timeout);
        }
        catch (Exception ex) when (ex is DnsResponseException or SocketException or ArgumentException or InvalidOperationException)
        {
            return DnsLookup.Failed<T>(DnsLookupStatus.ServFail);
        }
    }
}
