using Bazaar.Kodo.Export;

namespace Bazaar.Kodo.Tests.Export;

public sealed class KodoWebhookUrlTests
{
    [Fact]
    public void RedactionKeepsSchemeHostAndNonDefaultPortOnly()
    {
        string redacted = KodoWebhookUrl.Redact("https://user:TOKEN-A@hooks.example.com:8443/services/SECRET-B?key=KEY-C#frag-D");

        Assert.Equal("https://hooks.example.com:8443/[redacted]", redacted);
        Assert.DoesNotContain("TOKEN-A", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-B", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("KEY-C", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("frag-D", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultPortIsNotShown()
    {
        Assert.Equal("https://hooks.example.com/[redacted]", KodoWebhookUrl.Redact("https://hooks.example.com/abc123"));
    }

    [Fact]
    public void AnIpv6HostKeepsItsBrackets()
    {
        Assert.Equal("https://[::1]:8443/[redacted]", KodoWebhookUrl.Redact("https://[::1]:8443/hook"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url secret-token")]
    [InlineData("/relative/path/secret-token")]
    public void TextThatIsNotAnAbsoluteUrlIsNeverEchoed(string? input)
    {
        string redacted = KodoWebhookUrl.Redact(input);

        Assert.Equal(KodoWebhookUrl.UnparsedText, redacted);
        if (!string.IsNullOrEmpty(input))
        {
            Assert.DoesNotContain("secret-token", redacted, StringComparison.Ordinal);
        }
    }
}
