using Bazaar.Server.Browser.Abstractions;

namespace Bazaar.Server.Browser.Cdp;

/// <summary>
/// Builds the Chrome DevTools Protocol messages that set or clear cookies in a guest browser (doc 16, "cookies travel
/// over the guest channel"). Every cookie is sent with <c>secure: true</c> and <c>httpOnly: true</c>. Values are never
/// echoed into exception messages.
/// </summary>
public static class CdpCookieMapper
{
    private const string _setCookiesMethod = "Network.setCookies";
    private const string _clearCookiesMethod = "Network.clearBrowserCookies";

    /// <summary>Builds <c>Network.setCookies</c>. Session cookies (no <c>Expires</c>) omit the <c>expires</c> member.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="cookies"/> is null, or a cookie is null.</exception>
    /// <exception cref="ArgumentException">A cookie has an empty name or domain, or a name or value with a control character or ';'.</exception>
    public static byte[] BuildSetCookiesRequest(int requestId, IReadOnlyList<BrowserCookieEntry> cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);

        var mapped = new CdpCookie[cookies.Count];
        for (var i = 0; i < cookies.Count; i++)
        {
            mapped[i] = MapCookie(cookies[i], i);
        }

        var request = new CdpSetCookiesRequest(requestId, _setCookiesMethod, new CdpSetCookiesParams(mapped));
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request, CdpJsonContext.Default.CdpSetCookiesRequest);
    }

    /// <summary>Builds <c>Network.clearBrowserCookies</c>, which has no parameters.</summary>
    public static byte[] BuildClearCookiesRequest(int requestId)
    {
        var request = new CdpClearCookiesRequest(requestId, _clearCookiesMethod);
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request, CdpJsonContext.Default.CdpClearCookiesRequest);
    }

    private static CdpCookie MapCookie(BrowserCookieEntry? cookie, int index)
    {
        ArgumentNullException.ThrowIfNull(cookie);

        if (string.IsNullOrWhiteSpace(cookie.Name))
        {
            throw new ArgumentException($"Cookie at index {index} has an empty name.");
        }

        if (string.IsNullOrWhiteSpace(cookie.Domain))
        {
            throw new ArgumentException($"Cookie at index {index} has an empty domain.");
        }

        if (HasForbiddenCharacter(cookie.Name))
        {
            throw new ArgumentException($"Cookie at index {index} has a name with a control character or ';'.");
        }

        // The value is checked but never named in the message.
        if (HasForbiddenCharacter(cookie.Value))
        {
            throw new ArgumentException($"Cookie at index {index} has a value with a control character or ';'.");
        }

        double? expires = cookie.Expires is { } at
            ? at.ToUnixTimeMilliseconds() / 1000.0
            : null;

        return new CdpCookie(
            Name: cookie.Name,
            Value: cookie.Value,
            Domain: cookie.Domain,
            Path: cookie.Path,
            Secure: true,
            HttpOnly: true,
            Expires: expires);
    }

    private static bool HasForbiddenCharacter(string text)
    {
        foreach (var c in text)
        {
            if (c == ';' || char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }
}
