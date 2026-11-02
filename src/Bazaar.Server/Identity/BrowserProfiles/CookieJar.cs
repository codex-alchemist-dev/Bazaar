using System.Text.Json;

namespace Bazaar.Server.Identity.BrowserProfiles;

/// <summary>
/// An immutable set of cookies. Use <see cref="ForService"/> or <see cref="ForServices"/> to narrow it so that only
/// cookies for the wanted sites leave the jar.
/// </summary>
public sealed class CookieJar
{
    private readonly BrowserCookie[] _cookies;

    /// <summary>Creates a jar from the given cookies (copied).</summary>
    public CookieJar(IEnumerable<BrowserCookie> cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        _cookies = cookies.ToArray();
    }

    /// <summary>An empty jar.</summary>
    public static CookieJar Empty { get; } = new([]);

    /// <summary>The cookies in this jar.</summary>
    public IReadOnlyList<BrowserCookie> Cookies => _cookies;

    /// <summary>Number of cookies in the jar.</summary>
    public int Count => _cookies.Length;

    /// <summary>The cookies whose domain covers <paramref name="service"/>, and nothing else.</summary>
    public CookieJar ForService(string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        return new(_cookies.Where(c => CookieDomain.Matches(c.Domain, service)));
    }

    /// <summary>
    /// The cookies that belong to an allowlisted site: each cookie domain is an allowlist entry or a subdomain of one.
    /// A cookie can narrow an allowlist entry but never widen it.
    /// </summary>
    public CookieJar ForAllowlist(IEnumerable<string> allowlist)
    {
        ArgumentNullException.ThrowIfNull(allowlist);
        var list = allowlist.ToArray();
        return new(_cookies.Where(c => list.Any(a => CookieDomain.IsWithin(c.Domain, a))));
    }

    /// <summary>Serialises the jar to UTF-8 JSON for encryption. Internal: only the store writes it.</summary>
    internal byte[] Serialize() =>
        JsonSerializer.SerializeToUtf8Bytes(_cookies, CookieJsonContext.Default.BrowserCookieArray);

    /// <summary>Reads a jar produced by <see cref="Serialize"/>.</summary>
    internal static CookieJar Deserialize(byte[] json)
    {
        var cookies = JsonSerializer.Deserialize(json, CookieJsonContext.Default.BrowserCookieArray)
            ?? throw new InvalidDataException("Cookie profile payload is empty.");
        return new CookieJar(cookies);
    }
}
