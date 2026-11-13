using System.Globalization;
using System.Net;

namespace Bazaar.Conduit.PeerToPeer;

/// <summary>A server-reflexive address: the public address and port that a STUN server saw for this host.</summary>
/// <param name="Endpoint">The mapped address.</param>
/// <param name="Server">The configured "host:port" string of the server that answered.</param>
/// <param name="GatheredAt">When the answer was received, from the injected clock.</param>
public sealed record StunReflexiveCandidate(IPEndPoint Endpoint, string Server, DateTimeOffset GatheredAt);

/// <summary>Result of one gathering pass. <see cref="Candidate"/> is null when every server failed.</summary>
/// <param name="Candidate">The first valid reflexive address, or null.</param>
/// <param name="Reason">Null on success; otherwise a machine-readable reason such as <c>no-stun-response</c>.</param>
public sealed record StunGatherResult(StunReflexiveCandidate? Candidate, string? Reason);

/// <summary>
/// Gathers one server-reflexive candidate by asking configured STUN servers in order. A server that fails to resolve,
/// times out, or answers with a mismatched or malformed message counts as a failure for that server only.
/// </summary>
public sealed class StunGatherer
{
    /// <summary>Reason reported when no configured server answered with a usable address.</summary>
    public const string NoStunResponseReason = "no-stun-response";

    private static readonly TimeSpan _defaultPerServerTimeout = TimeSpan.FromSeconds(3.5);

    private readonly IStunTransport _transport;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly TimeSpan _perServerTimeout;

    /// <summary>Creates a gatherer.</summary>
    /// <param name="transport">Sends requests and receives replies.</param>
    /// <param name="resolve">Host name resolver. Defaults to <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>.</param>
    /// <param name="perServerTimeout">Time budget for each server, including retransmissions.</param>
    public StunGatherer(IStunTransport transport, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, TimeSpan? perServerTimeout = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _resolve = resolve ?? Dns.GetHostAddressesAsync;
        _perServerTimeout = perServerTimeout ?? _defaultPerServerTimeout;
    }

    /// <summary>Asks each server in order and returns the first valid reflexive address.</summary>
    /// <param name="servers">Servers as "host:port" strings; IPv6 literals use brackets.</param>
    /// <param name="time">Clock used for <see cref="StunReflexiveCandidate.GatheredAt"/>.</param>
    /// <param name="cancellationToken">Cancels the whole pass.</param>
    public async Task<StunGatherResult> GatherAsync(IReadOnlyList<string> servers, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(time);

        foreach (var server in servers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var endpoint = await ResolveAsync(server, cancellationToken).ConfigureAwait(false);
            if (endpoint is null)
            {
                continue;
            }

            var request = StunMessage.CreateBindingRequest();
            byte[]? reply;
            try
            {
                reply = await _transport.ExchangeAsync(endpoint, request.Packet, _perServerTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                reply = null;
            }

            var mapped = reply is null ? null : StunMessage.ReadMappedAddress(reply, request.TransactionId);
            if (mapped is not null)
            {
                return new StunGatherResult(new StunReflexiveCandidate(mapped, server, time.GetUtcNow()), null);
            }
        }

        return new StunGatherResult(null, NoStunResponseReason);
    }

    private async Task<IPEndPoint?> ResolveAsync(string server, CancellationToken cancellationToken)
    {
        if (!TryParseServer(server, out var host, out var port))
        {
            return null;
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            return new IPEndPoint(literal, port);
        }

        try
        {
            var addresses = await _resolve(host, cancellationToken).ConfigureAwait(false);
            return addresses.Length == 0 ? null : new IPEndPoint(addresses[0], port);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Splits "host:port" or "[ipv6]:port". Returns false for anything else.</summary>
    internal static bool TryParseServer(string text, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var trimmed = text.Trim();
        string portText;

        if (trimmed.StartsWith('['))
        {
            var close = trimmed.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || close + 1 >= trimmed.Length || trimmed[close + 1] != ':')
            {
                return false;
            }

            host = trimmed[1..close];
            portText = trimmed[(close + 2)..];
        }
        else
        {
            var colon = trimmed.LastIndexOf(':');
            if (colon <= 0)
            {
                return false;
            }

            host = trimmed[..colon];
            portText = trimmed[(colon + 1)..];
            if (host.Contains(':', StringComparison.Ordinal))
            {
                return false;
            }
        }

        return host.Length > 0
            && int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is > 0 and <= 65535;
    }
}
