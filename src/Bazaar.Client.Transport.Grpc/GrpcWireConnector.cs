using System.Net;
using Bazaar.Client.Transport.Grpc.Generated;
using Bazaar.Wire.Abstractions;
using Grpc.Core;
using Grpc.Net.Client;

namespace Bazaar.Client.Transport.Grpc;

/// <summary>
/// The <c>grpc</c> connector (doc 90 section 9): one bidirectional <c>bazaar.wire.v1.Session/Connect</c> stream per
/// wire connection. Runs over HTTP/2, and over HTTP/3 when <see cref="GrpcConnectorOptions.PreferHttp3"/> is set and the platform has it.
/// </summary>
public sealed class GrpcWireConnector : IWireTransportConnector
{
    private readonly GrpcConnectorOptions _options;

    /// <summary>Creates a connector.</summary>
    public GrpcWireConnector(GrpcConnectorOptions? options = null) => _options = options ?? new GrpcConnectorOptions();

    /// <inheritdoc />
    public string Transport => WireNames.Grpc;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async ValueTask<IWireConnection> ConnectAsync(TransportOffer offer, IReadOnlyList<string> codecs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(codecs);
        if (codecs.Count == 0) throw new ArgumentException("At least one codec is required.", nameof(codecs));

        var channel = CreateChannel(offer);
        AsyncDuplexStreamingCall<Frame, Frame>? call = null;
        try
        {
            var headers = new Metadata { { GrpcMetadata.Codec, string.Join(',', codecs) } };
            var credential = _options.CredentialProvider is null ? null : await _options.CredentialProvider(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(credential)) headers.Add(GrpcMetadata.Authorization, "Bearer " + credential);
            if (_options.DeviceId is not null) headers.Add(GrpcMetadata.Device, _options.DeviceId);

            call = new Session.SessionClient(channel).Connect(headers, cancellationToken: cancellationToken);
            var responseHeaders = await call.ResponseHeadersAsync.ConfigureAwait(false);
            var codec = responseHeaders.GetValue(GrpcMetadata.Codec);
            if (codec is null)
            {
                // A refusal arrives as a trailers-only response: empty headers, and the status shows on the first read.
                await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false);
            }

            if (codec is null || !codecs.Contains(codec, StringComparer.Ordinal))
            {
                throw new InvalidDataException("The server chose a codec the client did not offer.");
            }

            return new GrpcClientConnection(channel, call, new WireProfile(Transport, codec), _options.MaxFrameBytes);
        }
        catch (RpcException ex)
        {
            call?.Dispose();
            channel.Dispose();
            throw new IOException("gRPC connect failed: " + ex.StatusCode.ToString(), ex);
        }
        catch
        {
            call?.Dispose();
            channel.Dispose();
            throw;
        }
    }

    private GrpcChannel CreateChannel(TransportOffer offer)
    {
        var address = Uri.TryCreate(offer.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : new Uri("https://" + offer.Endpoint, UriKind.Absolute);
        HttpMessageHandler inner = _options.HttpHandlerFactory?.Invoke() ?? new SocketsHttpHandler { EnableMultipleHttp2Connections = true };
        HttpMessageHandler handler = _options.PreferHttp3 ? new Http3PreferenceHandler(inner) : inner;
        return GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = _options.MaxMessageBytes,
            MaxSendMessageSize = _options.MaxMessageBytes,
        });
    }

}

/// <summary>Asks for HTTP/3 and lets the runtime fall back to HTTP/2 or lower when it cannot.</summary>
internal sealed class Http3PreferenceHandler : DelegatingHandler
{
    public Http3PreferenceHandler(HttpMessageHandler inner)
        : base(inner)
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Version = HttpVersion.Version30;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        return base.SendAsync(request, cancellationToken);
    }
}
