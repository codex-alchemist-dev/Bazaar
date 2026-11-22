using Bazaar.Server.Browser.Abstractions;

namespace Bazaar.Server.SealedPassport;

/// <summary>
/// The Browser module's cookie port over the sealed Passport (doc 14 "Browser", doc 61 WP4). Returns only unexpired cookies of allowlisted sites,
/// read straight from the sealed jar for each launch; nothing is cached, so ending a session has nothing to drop here.
/// </summary>
public sealed class SealedBrowserCookieSource(SealedPassportStore store, TimeProvider? time = null) : IBrowserCookieSource
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<IReadOnlyList<BrowserCookieEntry>> GetCookiesAsync(string sessionId, BrowserProfileRef profile, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(profile);

        // The sealed record is the authority, not the caller's copy of the profile.
        var record = await store.FindRecordAsync(profile.ProfileId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Browser profile not found.");
        if (record.Class != BrowserProfileClass.Account || record.AgentId != profile.AgentId)
        {
            throw new InvalidOperationException("Only an agent's own account profile gets cookies.");
        }

        var jar = await store.ReadCookiesAsync(profile.ProfileId, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        return [.. jar.Cookies.Where(c => (c.Expires is null || c.Expires > now) && record.ServiceAllowlist.Any(a => PassportDomains.IsWithin(c.Domain, a)))];
    }

    /// <inheritdoc />
    public void EndSession(string sessionId) => ArgumentException.ThrowIfNullOrEmpty(sessionId);
}
