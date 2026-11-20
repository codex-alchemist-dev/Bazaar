using System.Net;
using System.Net.Quic;
using System.Security.Cryptography;
using Bazaar.Client.Transport.QuicNative;
using Bazaar.Server.Transport.QuicNative;
using Bazaar.Wire.Abstractions;

namespace Bazaar.Wire.Transport.QuicNative.Tests;

/// <summary>Checks that run on every platform, with or without MsQuic.</summary>
public sealed class QuicNativeAlwaysTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static QuicWireListener NewListener(bool streamPerSubscription = false) => new(new QuicWireListenerOptions
    {
        ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        Certificate = TestCertificates.CreateLocalhost(),
        StreamPerSubscription = streamPerSubscription,
    });

    [Fact]
    public void IsSupported_mirrors_System_Net_Quic_and_never_throws()
    {
        var connector = new QuicWireConnector();
        var listener = NewListener();
        Assert.Equal(QuicConnection.IsSupported, connector.IsSupported);
        Assert.Equal(QuicListener.IsSupported, listener.IsSupported);
        Assert.Equal(WireNames.Quic, connector.Transport);
        Assert.Equal(WireNames.Quic, listener.Transport);
        Assert.Empty(listener.Offers);
    }

    [Fact]
    public async Task Unsupported_platform_fails_with_PlatformNotSupported_not_a_crash()
    {
        Assert.SkipWhen(QuicConnection.IsSupported, "MsQuic is present here; the supported path is covered by the real-network tests.");
        var offer = new TransportOffer(WireNames.Quic, "localhost:4433", QuicWireSupport.Alpn, ["json"]);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(async () => await new QuicWireConnector().ConnectAsync(offer, ["json"], Ct));
        await using var listener = NewListener();
        await Assert.ThrowsAsync<PlatformNotSupportedException>(async () => await listener.StartAsync(Ct));
    }

    [Fact]
    public async Task Stream_per_subscription_is_refused_not_silently_ignored()
    {
        await using var listener = NewListener(streamPerSubscription: true);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await listener.StartAsync(Ct));
    }

    [Fact]
    public async Task Connector_rejects_a_foreign_offer_when_supported()
    {
        Assert.SkipUnless(QuicConnection.IsSupported, "Needs MsQuic.");
        var offer = new TransportOffer(WireNames.Grpc, "localhost:4433", null, ["json"]);
        await Assert.ThrowsAsync<ArgumentException>(async () => await new QuicWireConnector().ConnectAsync(offer, ["json"], Ct));
    }

    [Fact]
    public void Sha256_pin_matches_only_the_pinned_certificate()
    {
        using var cert = TestCertificates.CreateLocalhost();
        using var other = TestCertificates.CreateLocalhost();
        var pin = QuicCertificatePin.Sha256(cert.GetCertHash(HashAlgorithmName.SHA256));
        Assert.True(pin(cert));
        Assert.False(pin(other));
    }

    [Fact]
    public void Server_requires_at_least_one_codec()
    {
        Assert.Throws<ArgumentException>(() => new QuicWireListener(new QuicWireListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Certificate = TestCertificates.CreateLocalhost(),
            Codecs = [],
        }));
    }
}
