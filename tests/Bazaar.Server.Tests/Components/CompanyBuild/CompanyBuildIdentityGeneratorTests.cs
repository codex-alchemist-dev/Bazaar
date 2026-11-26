using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bazaar.CompanyIdentity;
using Bazaar.Server.Tls;

namespace Bazaar.Server.Tests.Components.CompanyBuild;

public sealed class CompanyBuildIdentityGeneratorTests
{
    private static readonly string _pin = new('b', 64);

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=company-server", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    [Theory]
    [InlineData("https://company.example/")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://localhost:8080/")]
    public void Create_accepts_https_and_loopback_http(string address)
    {
        var identity = CompanyServerIdentity.Create(new Uri(address), _pin);

        Assert.Equal(new Uri(address), identity.Address);
        Assert.Equal(_pin, identity.KeyHashPin);
    }

    [Theory]
    [InlineData("http://company.example/")]
    [InlineData("ftp://company.example/")]
    [InlineData("https://user:secret@company.example/")]
    public void Create_refuses_unsafe_addresses(string address)
    {
        Assert.Throws<ArgumentException>(() => CompanyServerIdentity.Create(new Uri(address), _pin));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEF")]
    [InlineData("B000000000000000000000000000000000000000000000000000000000000000")]
    public void Create_refuses_a_malformed_pin(string pin)
    {
        Assert.Throws<ArgumentException>(() => CompanyServerIdentity.Create(new Uri("https://company.example/"), pin));
    }

    [Fact]
    public void FromCertificate_pins_the_key_the_server_serves()
    {
        using var cert = SelfSigned();
        var serverCert = new ServerCertificate(cert, PublicKeyPin.Compute(cert));

        var identity = CompanyServerIdentity.Create(new Uri("https://company.example/"), serverCert.PublicKeyHash);

        Assert.Equal(PublicKeyPin.Compute(cert), identity.KeyHashPin);
    }

    [Fact]
    public void RenderSource_writes_the_address_and_pin_into_the_generated_identity()
    {
        var identity = CompanyServerIdentity.Create(new Uri("https://company.example/"), _pin);

        var source = CompanyBuildIdentityGenerator.RenderSource(identity);

        Assert.Contains("namespace Bazaar.CompanyIdentity;", source);
        Assert.Contains("public static class CompanyBuildIdentity", source);
        Assert.Contains("new Uri(\"https://company.example/\")", source);
        Assert.Contains($"\"{_pin}\"", source);
    }

    [Fact]
    public void WriteSource_replaces_the_placeholder_under_the_client_core_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "bazaar-identity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var identity = CompanyServerIdentity.Create(new Uri("https://company.example/"), _pin);

            var path = CompanyBuildIdentityGenerator.WriteSource(identity, root);

            Assert.Equal(Path.Combine(root, "CompanyBuildIdentity.cs"), path);
            Assert.Equal(CompanyBuildIdentityGenerator.RenderSource(identity), File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
