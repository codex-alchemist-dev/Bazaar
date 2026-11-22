using System.Net;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bazaar.Server.Transport.WebTransport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Wire.Transport.WebTransport.Tests;

/// <summary>
/// Real Kestrel HTTP/3 smoke test (spike S-WTX). Runs only with <c>BAZAAR_TEST_REAL_NET=1</c> and MsQuic present. It proves the
/// preview switch and the endpoint wiring: Kestrel serves HTTP/3 and the handler answers a non-WebTransport request with 404.
/// A real WebTransport client (a browser) is out of scope for CI; see docs/spikes/S-WTX.md.
/// </summary>
public sealed class WebTransportRealNetTests
{
    public static bool Enabled => Environment.GetEnvironmentVariable("BAZAAR_TEST_REAL_NET") == "1" && QuicListener.IsSupported;

    [Fact(SkipUnless = nameof(Enabled), Skip = "Set BAZAAR_TEST_REAL_NET=1 and install MsQuic (libmsquic) to run.")]
    public async Task Kestrel_serves_http3_and_the_handler_refuses_a_plain_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new WebTransportWireListenerOptions { Enabled = true, Authorize = _ => ValueTask.FromResult(true) };
        WebTransportEndpointExtensions.EnableKestrelWebTransport(options);
        using var listener = new WebTransportWireListener(options);
        await listener.StartAsync(ct);
        using var cert = CreateCertificate();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = HttpProtocols.Http3;
            listen.UseHttps(cert);
        }));
        await using var app = builder.Build();
        app.UseWebTransportWire(listener);
        await app.StartAsync(ct);
        var address = new Uri(app.Urls.First());

        using var handler = new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, presented, _, _) => presented is not null && CryptographicOperations.FixedTimeEquals(presented.GetCertHash(HashAlgorithmName.SHA256), cert.GetCertHash(HashAlgorithmName.SHA256)) } };
        using var client = new HttpClient(handler) { DefaultRequestVersion = HttpVersion.Version30, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
        using var response = await client.GetAsync(new Uri(address, listener.Path), ct);
        Assert.Equal(HttpVersion.Version30, response.Version);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await app.StopAsync(ct);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var self = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pfx), null);
    }
}
