using System.Text.Json.Serialization;

namespace Bazaar.Server.Browser.Cdp;

/// <summary>Source-generated JSON metadata for the Chrome DevTools Protocol messages this module sends (no reflection).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CdpSetCookiesRequest))]
[JsonSerializable(typeof(CdpClearCookiesRequest))]
internal sealed partial class CdpJsonContext : JsonSerializerContext
{
}

/// <summary>A <c>Network.setCookies</c> command.</summary>
internal sealed record CdpSetCookiesRequest(int Id, string Method, CdpSetCookiesParams Params);

/// <summary>Parameters of <c>Network.setCookies</c>.</summary>
internal sealed record CdpSetCookiesParams(IReadOnlyList<CdpCookie> Cookies);

/// <summary>One cookie in the <c>Network.setCookies</c> list. <c>Expires</c> is omitted for session cookies.</summary>
internal sealed record CdpCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    bool Secure,
    bool HttpOnly,
    double? Expires);

/// <summary>A <c>Network.clearBrowserCookies</c> command, which has no parameters.</summary>
internal sealed record CdpClearCookiesRequest(int Id, string Method);
