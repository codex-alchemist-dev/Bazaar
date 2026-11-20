using System.Text;
using Bazaar.Server.Browser.Abstractions;
using Bazaar.Server.Browser.Cdp;

namespace Bazaar.Server.Browser.Tests.Cdp;

public sealed class CdpCookieMapperTests
{
    private static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    [Fact]
    public void SetCookies_ExactJsonShape_ForExpiringCookie()
    {
        var cookie = new BrowserCookieEntry("sid", "abc123", ".example.com", "/", DateTimeOffset.FromUnixTimeSeconds(1767225600));

        var json = Utf8(CdpCookieMapper.BuildSetCookiesRequest(7, [cookie]));

        Assert.Equal(
            """{"id":7,"method":"Network.setCookies","params":{"cookies":[{"name":"sid","value":"abc123","domain":".example.com","path":"/","secure":true,"httpOnly":true,"expires":1767225600}]}}""",
            json);
    }

    [Fact]
    public void SetCookies_SessionCookie_OmitsExpires()
    {
        var cookie = new BrowserCookieEntry("sid", "abc123", "example.com", "/app", null);

        var json = Utf8(CdpCookieMapper.BuildSetCookiesRequest(3, [cookie]));

        Assert.Equal(
            """{"id":3,"method":"Network.setCookies","params":{"cookies":[{"name":"sid","value":"abc123","domain":"example.com","path":"/app","secure":true,"httpOnly":true}]}}""",
            json);
        Assert.DoesNotContain("expires", json);
    }

    [Fact]
    public void SetCookies_MixedList_KeepsOrderAndPerCookieExpiry()
    {
        var cookies = new[]
        {
            new BrowserCookieEntry("a", "1", "one.test", "/", null),
            new BrowserCookieEntry("b", "2", "two.test", "/x", DateTimeOffset.FromUnixTimeSeconds(100)),
        };

        var json = Utf8(CdpCookieMapper.BuildSetCookiesRequest(1, cookies));

        Assert.Equal(
            """{"id":1,"method":"Network.setCookies","params":{"cookies":[{"name":"a","value":"1","domain":"one.test","path":"/","secure":true,"httpOnly":true},{"name":"b","value":"2","domain":"two.test","path":"/x","secure":true,"httpOnly":true,"expires":100}]}}""",
            json);
    }

    [Fact]
    public void SetCookies_EmptyList_SendsEmptyArray()
    {
        var json = Utf8(CdpCookieMapper.BuildSetCookiesRequest(9, []));

        Assert.Equal("""{"id":9,"method":"Network.setCookies","params":{"cookies":[]}}""", json);
    }

    [Fact]
    public void ClearCookies_ExactJsonShape_HasNoParams()
    {
        var json = Utf8(CdpCookieMapper.BuildClearCookiesRequest(12));

        Assert.Equal("""{"id":12,"method":"Network.clearBrowserCookies"}""", json);
    }

    [Theory]
    [InlineData("", "example.com")]
    [InlineData("   ", "example.com")]
    [InlineData("sid", "")]
    [InlineData("sid", " ")]
    public void SetCookies_RefusesEmptyNameOrDomain(string name, string domain)
    {
        var cookie = new BrowserCookieEntry(name, "v", domain, "/", null);

        Assert.Throws<ArgumentException>(() => CdpCookieMapper.BuildSetCookiesRequest(1, [cookie]));
    }

    [Theory]
    [InlineData("bad\nname", "v")]
    [InlineData("bad\tname", "v")]
    [InlineData("na;me", "v")]
    [InlineData("sid", "bad\rvalue")]
    [InlineData("sid", "val;ue")]
    [InlineData("sid", "val\0ue")]
    public void SetCookies_RefusesControlCharactersAndSemicolon(string name, string value)
    {
        var cookie = new BrowserCookieEntry(name, value, "example.com", "/", null);

        Assert.Throws<ArgumentException>(() => CdpCookieMapper.BuildSetCookiesRequest(1, [cookie]));
    }

    [Fact]
    public void SetCookies_RejectsWholeBatchWhenOneCookieIsBad()
    {
        var cookies = new[]
        {
            new BrowserCookieEntry("ok", "1", "example.com", "/", null),
            new BrowserCookieEntry("bad", "x;y", "example.com", "/", null),
        };

        var ex = Assert.Throws<ArgumentException>(() => CdpCookieMapper.BuildSetCookiesRequest(1, cookies));

        Assert.Contains("index 1", ex.Message);
    }

    [Fact]
    public void SetCookies_NullList_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CdpCookieMapper.BuildSetCookiesRequest(1, null!));
    }

    [Fact]
    public void SetCookies_ValueNeverAppearsInExceptionMessage()
    {
        const string secret = "SUPER-SECRET-TOKEN-987";
        var badValues = new[] { secret + ";tail", secret + "\ncontrol", "\n" + secret };

        foreach (var value in badValues)
        {
            var cookie = new BrowserCookieEntry("sid", value, "example.com", "/", null);

            var ex = Assert.Throws<ArgumentException>(() => CdpCookieMapper.BuildSetCookiesRequest(1, [cookie]));

            Assert.DoesNotContain("SUPER-SECRET", ex.Message);
            Assert.DoesNotContain("SUPER-SECRET", ex.ToString());
        }
    }

    [Fact]
    public void SetCookies_ValueWithSpacesAndQuotes_IsJsonEscaped()
    {
        var cookie = new BrowserCookieEntry("sid", "a \"quoted\" b", "example.com", "/", null);

        var json = Utf8(CdpCookieMapper.BuildSetCookiesRequest(2, [cookie]));

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var parsed = doc.RootElement.GetProperty("params").GetProperty("cookies")[0].GetProperty("value").GetString();

        Assert.Equal("a \"quoted\" b", parsed);
    }
}
