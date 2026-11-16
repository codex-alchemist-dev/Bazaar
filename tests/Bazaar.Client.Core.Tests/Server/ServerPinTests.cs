using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bazaar.Client.Core.Server;

namespace Bazaar.Client.Core.Tests.Server;

public sealed class ServerPinTests
{
    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=company-server", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static string OtherPin() => new string('a', 64);

    [Fact]
    public void Compute_is_64_lowercase_hex_and_matches_the_certificate()
    {
        using var cert = SelfSigned();
        var pin = ServerPin.Compute(cert);

        Assert.True(ServerPin.IsWellFormed(pin));
        Assert.True(ServerPin.Matches(cert, pin));
        Assert.False(ServerPin.Matches(cert, OtherPin()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCDEF")]
    [InlineData("A0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("g000000000000000000000000000000000000000000000000000000000000000")]
    public void IsWellFormed_rejects_anything_that_is_not_a_64_char_lowercase_hex_pin(string? pin)
    {
        Assert.False(ServerPin.IsWellFormed(pin));
    }

    [Fact]
    public void Pinned_handler_accepts_only_the_pinned_key_and_ignores_chain_errors()
    {
        using var pinned = SelfSigned();
        using var other = SelfSigned();
        var pin = ServerPin.Compute(pinned);

        using var handler = ServerPin.CreatePinnedHandler(pin);
        var callback = handler.SslOptions.RemoteCertificateValidationCallback!;

        Assert.True(callback(this, pinned, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(callback(this, other, null, SslPolicyErrors.None));
        Assert.False(callback(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    [Fact]
    public void Pinned_handler_refuses_a_malformed_pin()
    {
        Assert.Throws<ArgumentException>(() => ServerPin.CreatePinnedHandler("not-a-pin"));
    }

    [Fact]
    public void Https_endpoint_without_a_pin_fails_closed()
    {
        var endpoint = new ServerEndpoint(new Uri("https://company.example/"));

        Assert.Throws<InvalidOperationException>(() => endpoint.CreateHandler());
    }

    [Fact]
    public void Https_endpoint_with_a_pin_gets_a_handler()
    {
        var endpoint = new ServerEndpoint(new Uri("https://company.example/"), KeyHashPin: OtherPin());

        using var handler = endpoint.CreateHandler();
        Assert.IsType<SocketsHttpHandler>(handler);
    }

    [Fact]
    public void Plain_http_off_loopback_is_refused()
    {
        var endpoint = new ServerEndpoint(new Uri("http://company.example/"), KeyHashPin: OtherPin());

        Assert.Throws<InvalidOperationException>(() => endpoint.CreateHandler());
    }

    [Fact]
    public void Loopback_http_is_allowed_for_the_development_endpoint()
    {
        using var handler = ServerEndpoint.Development.CreateHandler();
        Assert.IsType<HttpClientHandler>(handler);
    }
}
