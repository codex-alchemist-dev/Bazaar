using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bazaar.Client.Transport.QuicNative;
using Bazaar.Server.Transport.QuicNative;
using Bazaar.Wire.Abstractions;

namespace Bazaar.Wire.Transport.QuicNative.Tests;

/// <summary>
/// Real MsQuic loopback tests. They run only with <c>BAZAAR_TEST_REAL_NET=1</c> and MsQuic present
/// (<c>libmsquic</c> on Linux); otherwise they are skipped.
/// </summary>
public sealed class QuicNativeRealNetTests
{
    private const string _skipReason = "Set BAZAAR_TEST_REAL_NET=1 and install MsQuic (libmsquic) to run.";

    public static bool Enabled => Environment.GetEnvironmentVariable("BAZAAR_TEST_REAL_NET") == "1" && QuicConnection.IsSupported && QuicListener.IsSupported;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(QuicWireListener Listener, X509Certificate2 Cert, TransportOffer Offer)> StartAsync(TimeSpan? helloTimeout = null, int maxFrame = 256 * 1024)
    {
        var cert = TestCertificates.CreateLocalhost();
        var listener = new QuicWireListener(new QuicWireListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Certificate = cert,
            Codecs = ["json", "cbor"],
            MaxFrameBytes = maxFrame,
            HelloTimeout = helloTimeout ?? TimeSpan.FromSeconds(5),
        });
        await listener.StartAsync(Ct);
        Assert.Single(listener.Offers);
        var offer = listener.Offers[0];
        return (listener, cert, offer with { Endpoint = $"localhost:{listener.LocalEndPoint!.Port}" });
    }

    private static QuicWireConnector Connector(X509Certificate2 cert) => new(new QuicWireConnectorOptions { PinCertificate = QuicCertificatePin.Sha256(cert.GetCertHash(HashAlgorithmName.SHA256)) });

    [Fact(SkipUnless = nameof(Enabled), Skip = _skipReason)]
    public async Task Loopback_echo_of_frames_with_the_chosen_codec()
    {
        var (listener, cert, offer) = await StartAsync();
        await using var _ = listener;
        await using var client = await Connector(cert).ConnectAsync(offer, ["cbor", "json"], Ct);
        var server = await listener.Accepted.ReadAsync(Ct);
        await using var __ = server;
        Assert.Equal(new WireProfile(WireNames.Quic, "cbor"), client.Profile);
        Assert.Equal(client.Profile, server.Profile);

        var big = new byte[200_000];
        new Random(3).NextBytes(big);
        await client.SendAsync(new byte[] { 1, 2, 3 }, Ct);
        var sending = client.SendAsync(big, Ct).AsTask();
        Assert.Equal(new byte[] { 1, 2, 3 }, (await server.ReceiveAsync(Ct)).Frame.ToArray());
        Assert.Equal(big, (await server.ReceiveAsync(Ct)).Frame.ToArray());
        await sending;
        await server.SendAsync(big, Ct);
        Assert.Equal(big, (await client.ReceiveAsync(Ct)).Frame.ToArray());
    }

    [Fact(SkipUnless = nameof(Enabled), Skip = _skipReason)]
    public async Task Oversized_frame_closes_the_connection_with_4400()
    {
        var (listener, cert, offer) = await StartAsync(maxFrame: 1000);
        await using var _ = listener;
        await using var client = await Connector(cert).ConnectAsync(offer, ["json"], Ct);
        var server = await listener.Accepted.ReadAsync(Ct);
        await using var __ = server;
        await client.SendAsync(new byte[10], Ct);
        Assert.Equal(10, (await server.ReceiveAsync(Ct)).Frame.Length);

        // A conforming client refuses to send 1001 bytes, so declare an oversized frame on the server's own stream.
        var raw = new QuicWireConnector(new QuicWireConnectorOptions { PinCertificate = QuicCertificatePin.Sha256(cert.GetCertHash(HashAlgorithmName.SHA256)), MaxFrameBytes = 1 << 20 });
        await using var big = await raw.ConnectAsync(offer, ["json"], Ct);
        var server2 = await listener.Accepted.ReadAsync(Ct);
        await using var ___ = server2;
        await big.SendAsync(new byte[5000], Ct);
        var received = await server2.ReceiveAsync(Ct);
        Assert.True(received.IsClosed);
        Assert.Equal(StreamWireConnection.BadFrame, received.CloseCode);
        var seen = await big.ReceiveAsync(Ct);
        Assert.True(seen.IsClosed);
        Assert.Equal(StreamWireConnection.BadFrame, seen.CloseCode);
    }

    [Fact(SkipUnless = nameof(Enabled), Skip = _skipReason)]
    public async Task Bad_preface_closes_with_4400()
    {
        var (listener, cert, _) = await StartAsync();
        await using var _ = listener;
        var pin = QuicCertificatePin.Sha256(cert.GetCertHash(HashAlgorithmName.SHA256));
        await using var connection = await QuicConnection.ConnectAsync(
            new QuicClientConnectionOptions
            {
                RemoteEndPoint = new DnsEndPoint("localhost", listener.LocalEndPoint!.Port),
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = QuicWireSupport.CreateApplicationProtocols(),
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && pin(new X509Certificate2(certificate)),
                },
            },
            Ct);
        await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, Ct);
        await stream.WriteAsync(new byte[] { 0x00, 0x01, 0x02, 0x03 }, Ct);
        var ex = await Assert.ThrowsAsync<QuicException>(async () =>
        {
            await stream.ReadExactlyAsync(new byte[1], Ct);
        });
        Assert.Equal(StreamWireConnection.BadFrame, (int?)ex.ApplicationErrorCode);
    }

    [Fact(SkipUnless = nameof(Enabled), Skip = _skipReason)]
    public async Task Silent_client_is_closed_with_4408()
    {
        var (listener, cert, offer) = await StartAsync(helloTimeout: TimeSpan.FromMilliseconds(500));
        await using var _ = listener;
        await using var client = await Connector(cert).ConnectAsync(offer, ["json"], Ct);
        var server = await listener.Accepted.ReadAsync(Ct);
        await using var __ = server;
        var received = await server.ReceiveAsync(Ct);
        Assert.Equal(StreamWireConnection.HelloTimeout, received.CloseCode);
    }

    [Fact(SkipUnless = nameof(Enabled), Skip = _skipReason)]
    public async Task Certificate_pin_mismatch_refuses_the_connection()
    {
        var (listener, _, offer) = await StartAsync();
        await using var _ = listener;
        using var other = TestCertificates.CreateLocalhost();
        // The pin callback returns false, so System.Net.Quic fails the handshake with an AuthenticationException that names the
        // RemoteCertificateValidationCallback. An ALPN failure is also an AuthenticationException, so the message must be checked too.
        var ex = await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(async () => await Connector(other).ConnectAsync(offer, ["json"], Ct));
        Assert.Contains("RemoteCertificateValidationCallback", ex.Message, StringComparison.Ordinal);
        Assert.Contains(QuicWireSupport.DiagnosticsMarker, ex.Message, StringComparison.Ordinal);
    }

    [Fact(SkipUnless = nameof(Enabled), Skip = _skipReason)]
    public async Task No_common_codec_closes_with_4400()
    {
        var (listener, cert, offer) = await StartAsync();
        await using var _ = listener;
        // The server finds no common codec and closes the connection with application error code 4400 (BadFrame) before it echoes a
        // preface; the client sees that close as a QuicException while reading the answer. (Assumption from the code paths: exact
        // surface is ConnectionAborted with ApplicationErrorCode 4400; if the CI run shows another QuicError, fix this assertion.)
        var ex = await Assert.ThrowsAsync<QuicException>(async () => await Connector(cert).ConnectAsync(offer, ["msgpack"], Ct));
        Assert.Equal(StreamWireConnection.BadFrame, (int?)ex.ApplicationErrorCode);
        Assert.Contains(QuicWireSupport.DiagnosticsMarker, Assert.IsType<string>(ex.Data[QuicWireSupport.DiagnosticsDataKey]), StringComparison.Ordinal);
    }
}
