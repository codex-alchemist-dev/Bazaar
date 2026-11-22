using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Identity.GitHubApp;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Identity.GitHubApp;

public class GitHubAppJwtSignerTests
{
    [Fact]
    public void Jwt_has_rs256_header_and_expected_claims()
    {
        using var rsa = GitHubAppTestSupport.CreateKey();
        var time = new FakeTimeProvider(GitHubAppTestSupport.Now);
        var signer = new GitHubAppJwtSigner(time);

        var jwt = signer.CreateJwt(123456, GitHubAppTestSupport.PemOf(rsa));
        var parts = jwt.Split('.');

        Assert.Equal(3, parts.Length);
        using var header = JsonDocument.Parse(GitHubAppTestSupport.Base64UrlDecode(parts[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());

        using var claims = JsonDocument.Parse(GitHubAppTestSupport.Base64UrlDecode(parts[1]));
        var now = GitHubAppTestSupport.Now.ToUnixTimeSeconds();
        Assert.Equal("123456", claims.RootElement.GetProperty("iss").GetString());
        Assert.Equal(now - 60, claims.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(now + 9 * 60, claims.RootElement.GetProperty("exp").GetInt64());
    }

    [Fact]
    public void Jwt_signature_verifies_with_the_public_key_and_rejects_tampering()
    {
        using var rsa = GitHubAppTestSupport.CreateKey();
        var signer = new GitHubAppJwtSigner(new FakeTimeProvider(GitHubAppTestSupport.Now));

        var parts = signer.CreateJwt(7, GitHubAppTestSupport.PemOf(rsa)).Split('.');
        var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        var signature = GitHubAppTestSupport.Base64UrlDecode(parts[2]);

        using var verifier = RSA.Create();
        verifier.ImportRSAPublicKey(rsa.ExportRSAPublicKey(), out _);
        Assert.True(verifier.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        var tampered = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1] + "x");
        Assert.False(verifier.VerifyData(tampered, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Claims_follow_the_clock()
    {
        using var rsa = GitHubAppTestSupport.CreateKey();
        var time = new FakeTimeProvider(GitHubAppTestSupport.Now);
        var signer = new GitHubAppJwtSigner(time);

        time.Advance(TimeSpan.FromMinutes(5));
        var parts = signer.CreateJwt(7, GitHubAppTestSupport.PemOf(rsa)).Split('.');

        using var claims = JsonDocument.Parse(GitHubAppTestSupport.Base64UrlDecode(parts[1]));
        Assert.Equal(GitHubAppTestSupport.Now.AddMinutes(5).ToUnixTimeSeconds() - 60, claims.RootElement.GetProperty("iat").GetInt64());
    }

    [Fact]
    public void Unreadable_pem_throws_without_echoing_the_input()
    {
        var signer = new GitHubAppJwtSigner(new FakeTimeProvider(GitHubAppTestSupport.Now));
        const string bogus = "-----BEGIN RSA PRIVATE KEY-----\nNOT-A-KEY-MATERIAL\n-----END RSA PRIVATE KEY-----";

        var ex = Assert.Throws<GitHubAppException>(() => signer.CreateJwt(7, bogus));

        Assert.DoesNotContain("NOT-A-KEY-MATERIAL", ex.ToString());
    }

    [Fact]
    public void Non_positive_app_id_is_refused()
    {
        using var rsa = GitHubAppTestSupport.CreateKey();
        var signer = new GitHubAppJwtSigner(new FakeTimeProvider(GitHubAppTestSupport.Now));

        Assert.Throws<ArgumentOutOfRangeException>(() => signer.CreateJwt(0, GitHubAppTestSupport.PemOf(rsa)));
    }
}
