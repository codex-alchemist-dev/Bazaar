using Bazaar.Wire.Abstractions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Transport.Grpc;

/// <summary>The <c>grpc</c> or <c>grpc-web</c> listener entry: what discovery and <c>welcome</c> advertise.</summary>
public sealed class GrpcTransportListener : IWireTransportListener
{
    private readonly IOptions<GrpcTransportOptions> _options;
    private readonly IOptions<WireTransportsEnabledOptions> _enabled;

    /// <summary>Creates a listener for <paramref name="transport"/> (<c>grpc</c> or <c>grpc-web</c>).</summary>
    public GrpcTransportListener(string transport, IOptions<GrpcTransportOptions> options, IOptions<WireTransportsEnabledOptions> enabled)
    {
        ArgumentException.ThrowIfNullOrEmpty(transport);
        _options = options;
        _enabled = enabled;
        Transport = transport;
    }

    /// <inheritdoc />
    public string Transport { get; }

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public IReadOnlyList<TransportOffer> Offers
    {
        get
        {
            var o = _options.Value;
            if (o.PublicEndpoint is null || !_enabled.Value.IsEnabled(Transport)) return [];
            return [new TransportOffer(Transport, o.PublicEndpoint, "h2", o.EnabledCodecs)];
        }
    }
}
