namespace Bazaar.Server.Identity.BrowserProfiles;

/// <summary>
/// Host matching for cookies. A cookie domain covers a service when the service equals the domain or is a subdomain
/// of it. Matching is exact on the label boundary, so "evil-example.com" never matches "example.com".
/// </summary>
public static class CookieDomain
{
    /// <summary>Trims whitespace and a leading dot, and lower-cases the host.</summary>
    /// <exception cref="ArgumentException">The host is empty.</exception>
    public static string Normalize(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return host.Trim().TrimStart('.').ToLowerInvariant();
    }

    /// <summary>True when a cookie scoped to <paramref name="cookieDomain"/> may be sent to <paramref name="service"/>.</summary>
    public static bool Matches(string cookieDomain, string service) => IsWithin(service, cookieDomain);

    /// <summary>True when <paramref name="host"/> is <paramref name="parent"/> or a subdomain of it.</summary>
    public static bool IsWithin(string host, string parent)
    {
        var child = Normalize(host);
        var ancestor = Normalize(parent);
        return child == ancestor || child.EndsWith("." + ancestor, StringComparison.Ordinal);
    }
}
