namespace Bazaar.Server.Identity.BrowserProfiles;

/// <summary>
/// Returns the cookies that one lease may inject into one browser session. Only cookies whose domain covers the
/// lease's service are returned, so a cookie for service A never reaches service B. Anonymous profiles are refused.
/// </summary>
public sealed class CookieInjector(IBrowserProfileStore store, TimeProvider time)
{
    /// <summary>Returns the cookies to inject for <paramref name="service"/> under <paramref name="lease"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// The profile is anonymous, belongs to another agent, or the lease has expired.
    /// </exception>
    /// <exception cref="ArgumentException">The lease is for a different service.</exception>
    public async Task<IReadOnlyList<BrowserCookie>> InjectAsync(
        BrowserLease lease,
        string service,
        BrowserProfile profile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        if (profile.Kind == BrowserProfileKind.Anonymous)
        {
            throw new InvalidOperationException("Anonymous browser profiles never receive injected cookies.");
        }

        if (lease.AgentId != profile.AgentId)
        {
            throw new InvalidOperationException("The lease belongs to a different agent than the profile.");
        }

        if (CookieDomain.Normalize(lease.Service) != CookieDomain.Normalize(service))
        {
            throw new ArgumentException("The lease is for a different service.", nameof(service));
        }

        var now = time.GetUtcNow();
        if (lease.ExpiresAt <= now)
        {
            throw new InvalidOperationException("The lease has expired.");
        }

        if (!profile.ServiceAllowlist.Any(a => CookieDomain.IsWithin(service, a)))
        {
            return [];
        }

        var jar = await store.ReadCookiesAsync(profile.ProfileId, ct);
        return jar.ForService(service).Cookies
            .Where(c => c.Expires is null || c.Expires > now)
            .ToArray();
    }
}
