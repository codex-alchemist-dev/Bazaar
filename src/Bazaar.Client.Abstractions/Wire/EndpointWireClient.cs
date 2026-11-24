using Bazaar.Client.Abstractions.Server;
using Bazaar.CompanyIdentity;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Client.Abstractions.Wire;

/// <summary>
/// Builds the socket client for a <see cref="ServerEndpoint"/>: the wss address from the endpoint's base address, the key-hash
/// pin enforced on the socket's certificate, and the endpoint's credential sent in <c>hello</c>. A head calls <see cref="Create"/>
/// once, then runs <see cref="WireClient.RunAsync"/>; every feature that needs a request takes the result as
/// <see cref="IWireRequester"/>. An endpoint that must not be contacted gets a transport that refuses every connection.
/// </summary>
public static class EndpointWireClient
{
    /// <summary>The server's socket path (<c>wire.path</c>, default <c>/ws</c>).</summary>
    public const string SocketPath = "ws";

    /// <summary>The socket address for an endpoint: http becomes ws, https becomes wss.</summary>
    public static Uri SocketUri(ServerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var builder = new UriBuilder(new Uri(endpoint.BaseAddress, SocketPath))
        {
            Scheme = string.Equals(endpoint.BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
        };
        // UriBuilder keeps the scheme's default port as -1 once the scheme changes; an explicit port is kept as given.
        if (endpoint.BaseAddress.IsDefaultPort)
        {
            builder.Port = -1;
        }

        return builder.Uri;
    }

    /// <summary>The transport for an endpoint: pinned for https, plain for loopback http, refusing for anything else.</summary>
    public static IWireTransportFactory CreateTransport(ServerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var refusal = endpoint.RefusalReason();
        if (refusal is not null)
        {
            return new RefusingTransport(refusal);
        }

        var pin = endpoint.KeyHashPin;
        return new WebSocketWireTransportFactory(
            SocketUri(endpoint),
            pin is null
                ? null
                : options => options.RemoteCertificateValidationCallback =
                    (_, certificate, _, _) => certificate is not null && ServerPin.Matches(certificate, pin));
    }

    /// <summary>Creates the client. The caller starts <see cref="WireClient.RunAsync"/> and cancels it on shutdown.</summary>
    /// <param name="endpoint">The server this client reaches.</param>
    /// <param name="cursors">Where replay cursors are kept.</param>
    /// <param name="deviceId">Stable device id sent in <c>hello</c>.</param>
    /// <param name="deviceClass">Device class the server uses to pick a UI profile, such as <c>android</c>.</param>
    /// <param name="clientBuild">Build label sent in <c>hello</c>.</param>
    public static WireClient Create(ServerEndpoint endpoint, ICursorStore cursors, string deviceId, string deviceClass, string clientBuild)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(cursors);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientBuild);

        return new WireClient(
            CreateTransport(endpoint),
            new JsonWireCodec(),
            cursors,
            new WireClientOptions
            {
                DeviceId = deviceId,
                DeviceClass = deviceClass,
                ClientBuild = clientBuild,
                Credential = endpoint.Credential,
            },
            TimeProvider.System);
    }

    private sealed class RefusingTransport(string reason) : IWireTransportFactory
    {
        public ValueTask<IWireConnection> ConnectAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<IWireConnection>(new InvalidOperationException(reason));
    }
}
