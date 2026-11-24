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
    public void Both_sides_derive_ALPN_from_the_one_shared_constant()
    {
        Assert.Equal("bazaar-wire/1", QuicWireSupport.Alpn); // doc 90 section 9
        var expected = System.Text.Encoding.UTF8.GetBytes(QuicWireSupport.Alpn);
        var list = QuicWireSupport.CreateApplicationProtocols();
        Assert.Equal(expected, Assert.Single(list).Protocol.ToArray());
        Assert.NotSame(list, QuicWireSupport.CreateApplicationProtocols());

        // Neither transport module may build an SslApplicationProtocol from a literal or from the offer: they share the helper.
        foreach (var file in new[] { "Bazaar.Server.Transport.QuicNative/QuicWireListener.cs", "Bazaar.Client.Transport.QuicNative/QuicWireConnector.cs" })
        {
            var text = File.ReadAllText(Path.Combine(RepoSrc(), file));
            Assert.Contains("QuicWireSupport.CreateApplicationProtocols()", text);
            Assert.DoesNotContain("new SslApplicationProtocol(", text);
            Assert.DoesNotContain("bazaar-wire/1\"", text);
        }
    }

    [Fact]
    public async Task Connector_refuses_an_offer_with_a_different_ALPN_instead_of_failing_the_handshake()
    {
        var offer = new TransportOffer(WireNames.Quic, "localhost:4433", "bazaar-wire/2", ["json"]);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await new QuicWireConnector().ConnectAsync(offer, ["json"], Ct));
    }

    private static string RepoSrc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Bazaar.Wire.Abstractions"))) dir = dir.Parent;
        return dir is null ? throw new DirectoryNotFoundException("Repo src folder not found.") : Path.Combine(dir.FullName, "src");
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
