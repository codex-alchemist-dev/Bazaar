using System.Security.Cryptography;

namespace Bazaar.Server.Identity.BrowserProfiles;

/// <summary>
/// Makes decoy cookies for the mounted folder (doc 14, "Canary credentials"). A canary has a random name and a value
/// that is plainly fake. Any use of a canary is a detection event that applies hard taint, so the caller must check
/// <see cref="IsCanary"/> on every cookie it sends.
/// </summary>
public static class CanarySeeder
{
    private const string ValuePrefix = "canary-decoy-not-a-credential-";

    /// <summary>Returns a decoy cookie scoped to <paramref name="service"/>, with a fresh random name and value.</summary>
    public static BrowserCookie Seed(string service)
    {
        var domain = CookieDomain.Normalize(service);
        var name = "cnry_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var value = ValuePrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        return new BrowserCookie(name, value, domain, "/", null);
    }

    /// <summary>True when the cookie is a canary made by <see cref="Seed"/>.</summary>
    public static bool IsCanary(BrowserCookie cookie)
    {
        ArgumentNullException.ThrowIfNull(cookie);
        return cookie.Value.StartsWith(ValuePrefix, StringComparison.Ordinal);
    }
}
