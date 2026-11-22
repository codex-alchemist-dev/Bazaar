namespace Bazaar.Server.Identity.BrowserProfiles;

/// <summary>
/// Stores browser profiles. Cookie jars are kept only as encrypted blobs; reads decrypt on demand. Writing a jar
/// replaces the previous one.
/// </summary>
public interface IBrowserProfileStore
{
    /// <summary>Creates a profile. Anonymous profiles take no allowlist.</summary>
    Task<BrowserProfile> CreateAsync(
        string agentId,
        BrowserProfileKind kind,
        IEnumerable<string> serviceAllowlist,
        CancellationToken ct);

    /// <summary>Returns the profile, or null when it does not exist.</summary>
    Task<BrowserProfile?> FindAsync(Guid profileId, CancellationToken ct);

    /// <summary>
    /// Encrypts and stores the jar. Throws <see cref="InvalidOperationException"/> when an anonymous profile would get
    /// any cookie, and <see cref="ArgumentException"/> when an account cookie is outside the allowlist.
    /// </summary>
    Task WriteCookiesAsync(Guid profileId, CookieJar jar, CancellationToken ct);

    /// <summary>Decrypts and returns the stored jar (empty when none was written).</summary>
    Task<CookieJar> ReadCookiesAsync(Guid profileId, CancellationToken ct);
}
