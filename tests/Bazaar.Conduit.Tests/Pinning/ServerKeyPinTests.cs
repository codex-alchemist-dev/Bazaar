using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Bazaar.Conduit.Pinning;

namespace Bazaar.Conduit.Tests.Pinning;

public sealed class ServerKeyPinTests
{
    private static readonly string _emptyKeyHash = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// Creates a self-signed P-256 certificate with its private key attached. The certificate is round-tripped through
    /// PKCS#12 because Windows SslStream cannot use an ephemeral key for server authentication. The caller owns the result.
    /// </summary>
    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx, string.Empty), string.Empty, X509KeyStorageFlags.EphemeralKeySet);
    }

    private static ConduitOffer Offer(string hash, bool terminatesTls) =>
        new("cloudflared", ConduitScope.Internet, new Uri("wss://pin.example.test:7443/"), hash, null, false, true, terminatesTls);

    [Fact]
    public void ComputeHash_MatchesIndependentDigestOfSubjectPublicKeyInfo()
    {
        using var cert = SelfSigned("pin-test");
        using var key = cert.GetECDsaPublicKey();
        Assert.NotNull(key);

        // The same SPKI bytes from the key object, not from the certificate, digested independently.
        var expected = "sha256:" + Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        Assert.Equal(expected, ServerKeyPin.ComputeHash(cert));
        Assert.Matches("^sha256:[0-9a-f]{64}$", ServerKeyPin.ComputeHash(cert));
    }

    [Fact]
    public void Matches_TrueForTheSameCertificate_FalseForAnotherCertificate()
    {
        using var cert = SelfSigned("first");
        using var other = SelfSigned("second");
        var hash = ServerKeyPin.ComputeHash(cert);

        Assert.True(ServerKeyPin.Matches(hash, cert));
        Assert.False(ServerKeyPin.Matches(hash, other));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:")]
    [InlineData("sha1:0000")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void Matches_RefusesMalformedExpectedHash(string expected)
    {
        using var cert = SelfSigned("malformed");

        Assert.False(ServerKeyPin.Matches(expected, cert));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:")]
    [InlineData("md5:abcdef")]
    public void CreatePinnedOptions_RefusesEmptyOrNonSha256Pin(string expected)
    {
        Assert.Throws<ArgumentException>(() => ServerKeyPin.CreatePinnedOptions(expected, "localhost"));
    }

    [Fact]
    public async Task PinnedOptions_AcceptsMatchingCertificate_OverLoopbackHandshake()
    {
        using var server = SelfSigned("pinned-server");
        var hash = ServerKeyPin.ComputeHash(server);

        var (clientOk, serverOk) = await HandshakeAsync(server, ServerKeyPin.CreatePinnedOptions(hash, "localhost"));

        Assert.True(clientOk);
        Assert.True(serverOk);
    }

    [Fact]
    public async Task PinnedOptions_RejectsDifferentCertificate_OverLoopbackHandshake()
    {
        using var server = SelfSigned("impostor-server");
        using var pinned = SelfSigned("real-server");
        var hash = ServerKeyPin.ComputeHash(pinned);

        var (clientOk, _) = await HandshakeAsync(server, ServerKeyPin.CreatePinnedOptions(hash, "localhost"));

        Assert.False(clientOk);
    }

    [Fact]
    public void ValidatePinnedOffer_DirectOfferWithPin_IsAccepted()
    {
        Assert.Null(ServerKeyPin.ValidatePinnedOffer(Offer(_emptyKeyHash, terminatesTls: false), allowUnpinned: false));
        Assert.Null(ServerKeyPin.ValidatePinnedOffer(Offer(_emptyKeyHash, terminatesTls: false), allowUnpinned: true));
    }

    [Fact]
    public void ValidatePinnedOffer_DirectOfferWithoutSha256Pin_IsRefused()
    {
        var reason = ServerKeyPin.ValidatePinnedOffer(Offer("md5:abc", terminatesTls: false), allowUnpinned: false);

        Assert.NotNull(reason);
        Assert.NotEqual(ServerKeyPin.UnpinnedWarning, reason);
    }

    [Fact]
    public void ValidatePinnedOffer_TerminatedTlsWithPinAndNoOptOut_IsAcceptedAndPinIsEnforced()
    {
        Assert.Null(ServerKeyPin.ValidatePinnedOffer(Offer(_emptyKeyHash, terminatesTls: true), allowUnpinned: false));
    }

    [Fact]
    public void ValidatePinnedOffer_TerminatedTlsWithoutPinAndNoOptOut_IsRefused()
    {
        var reason = ServerKeyPin.ValidatePinnedOffer(Offer(string.Empty, terminatesTls: true), allowUnpinned: false);

        Assert.NotNull(reason);
        Assert.NotEqual(ServerKeyPin.UnpinnedWarning, reason);
    }

    [Fact]
    public void ValidatePinnedOffer_TerminatedTlsWithOptOut_ReturnsWarning()
    {
        var result = ServerKeyPin.ValidatePinnedOffer(Offer(string.Empty, terminatesTls: true), allowUnpinned: true);

        Assert.Equal("unpinned-tls-termination-allowed", result);
    }

    /// <summary>
    /// Runs one TLS handshake on 127.0.0.1 with an ephemeral port. Returns whether each side completed it. A rejected
    /// client makes the server side fail too, which is expected and reported as false.
    /// </summary>
    private static async Task<(bool ClientOk, bool ServerOk)> HandshakeAsync(X509Certificate2 serverCert, SslClientAuthenticationOptions clientOptions)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = ServeOnceAsync(listener, serverCert);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var clientStream = new SslStream(client.GetStream());

            var clientOk = true;
            try
            {
                await clientStream.AuthenticateAsClientAsync(clientOptions, CancellationToken.None);
            }
            catch (System.Security.Authentication.AuthenticationException)
            {
                clientOk = false;
            }

            var serverOk = await serverTask;
            return (clientOk, serverOk);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<bool> ServeOnceAsync(TcpListener listener, X509Certificate2 serverCert)
    {
        using var tcp = await listener.AcceptTcpClientAsync();
        using var stream = new SslStream(tcp.GetStream());
        try
        {
            await stream.AuthenticateAsServerAsync(serverCert);
            return true;
        }
        catch (Exception e) when (e is System.Security.Authentication.AuthenticationException or IOException)
        {
            return false;
        }
    }
}
