using System;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.GitHub.Webhooks;

namespace Bazaar.Server.Tests.GitHub.Webhooks;

public class WebhookSignatureTests
{
    // Known-answer vector: the example in GitHub's webhook documentation.
    // The expected digest is recomputed below with HMACSHA256 from the same secret and body,
    // so the literal also proves the test's own HMAC call matches GitHub's published value.
    private static readonly byte[] _secret = Encoding.UTF8.GetBytes("It's a Secret to Everybody");
    private static readonly byte[] _body = Encoding.UTF8.GetBytes("Hello, World!");
    private const string _knownHeader = "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17";

    [Fact]
    public void KnownAnswerVectorMatchesGitHubDocumentationAndHmac()
    {
        byte[] recomputed = HMACSHA256.HashData(_secret, _body);

        Assert.Equal(_knownHeader, "sha256=" + Convert.ToHexStringLower(recomputed));
        Assert.True(WebhookSignature.Verify(_secret, _body, _knownHeader));
        Assert.Equal(_knownHeader, WebhookSignature.Compute(_secret, _body));
    }

    [Fact]
    public void WrongSecretFails()
    {
        byte[] otherSecret = Encoding.UTF8.GetBytes("not the secret");

        Assert.False(WebhookSignature.Verify(otherSecret, _body, _knownHeader));
    }

    [Fact]
    public void AlteredBodyFails()
    {
        byte[] altered = Encoding.UTF8.GetBytes("Hello, World?");

        Assert.False(WebhookSignature.Verify(_secret, altered, _knownHeader));
    }

    [Fact]
    public void MissingPrefixFails()
    {
        string withoutPrefix = _knownHeader["sha256=".Length..];

        Assert.False(WebhookSignature.Verify(_secret, _body, withoutPrefix));
    }

    [Fact]
    public void WrongPrefixFails()
    {
        string sha1Prefix = "sha1=" + _knownHeader["sha256=".Length..];

        Assert.False(WebhookSignature.Verify(_secret, _body, sha1Prefix));
    }

    [Theory]
    [InlineData("sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e1")]
    [InlineData("sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e170")]
    [InlineData("sha256=")]
    [InlineData("sha256=zz7107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17")]
    public void WrongLengthOrNonHexFails(string header)
    {
        Assert.False(WebhookSignature.Verify(_secret, _body, header));
    }

    [Fact]
    public void MissingHeaderFails()
    {
        Assert.False(WebhookSignature.Verify(_secret, _body, null));
        Assert.False(WebhookSignature.Verify(_secret, _body, string.Empty));
    }

    [Fact]
    public void EmptySecretIsRejectedAsConfigurationError()
    {
        Assert.Throws<ArgumentException>(() => WebhookSignature.Verify([], _body, _knownHeader));
        Assert.Throws<ArgumentException>(() => WebhookSignature.Compute([], _body));
    }
}
