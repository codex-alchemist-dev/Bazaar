namespace Bazaar.Server.Identity.BrowserProfiles;

/// <summary>
/// Materialises an account profile's cookies for one active session, for the mounted folder (doc 14, "Mounted
/// folder"). The cookies exist in memory only between <see cref="MaterializeAsync"/> and <see cref="EndSession"/>.
/// After <see cref="EndSession"/>, <see cref="TryGetActive"/> returns null and the handle's cookies throw.
/// </summary>
public sealed class CookieProfileMaterializer(IBrowserProfileStore store)
{
    private readonly System.Threading.Lock _gate = new();
    private readonly Dictionary<string, MaterializedCookieProfile> _active = new(StringComparer.Ordinal);

    /// <summary>
    /// Decrypts the account profile's cookies that match its allowlist and makes them active for
    /// <paramref name="sessionId"/>. A second call for the same session replaces the first.
    /// </summary>
    /// <exception cref="InvalidOperationException">The profile is missing or anonymous.</exception>
    public async Task<MaterializedCookieProfile> MaterializeAsync(string sessionId, Guid profileId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        var profile = await store.FindAsync(profileId, ct)
            ?? throw new InvalidOperationException("Browser profile not found.");
        if (profile.Kind != BrowserProfileKind.Account)
        {
            throw new InvalidOperationException("Only account profiles are materialised.");
        }

        var jar = (await store.ReadCookiesAsync(profileId, ct)).ForAllowlist(profile.ServiceAllowlist);
        var materialized = new MaterializedCookieProfile(sessionId, profileId, jar);

        lock (_gate)
        {
            if (_active.Remove(sessionId, out var previous))
            {
                previous.Dispose();
            }

            _active[sessionId] = materialized;
        }

        return materialized;
    }

    /// <summary>Returns the active materialisation for the session, or null when it is not active.</summary>
    public MaterializedCookieProfile? TryGetActive(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        lock (_gate)
        {
            return _active.GetValueOrDefault(sessionId);
        }
    }

    /// <summary>Ends the session and drops its cookies. Safe to call for a session that is not active.</summary>
    public void EndSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        lock (_gate)
        {
            if (_active.Remove(sessionId, out var materialized))
            {
                materialized.Dispose();
            }
        }
    }
}

/// <summary>
/// A session's decrypted cookie profile. Disposing it (which <see cref="CookieProfileMaterializer.EndSession"/> does)
/// drops the reference to the cookies, and <see cref="Cookies"/> then throws.
/// </summary>
public sealed class MaterializedCookieProfile : IDisposable
{
    private CookieJar? _jar;

    internal MaterializedCookieProfile(string sessionId, Guid profileId, CookieJar jar)
    {
        SessionId = sessionId;
        ProfileId = profileId;
        _jar = jar;
    }

    /// <summary>The session this materialisation belongs to.</summary>
    public string SessionId { get; }

    /// <summary>The account profile it was read from.</summary>
    public Guid ProfileId { get; }

    /// <summary>True until the session ends.</summary>
    public bool IsActive => _jar is not null;

    /// <summary>The cookies for the allowlisted sites.</summary>
    /// <exception cref="ObjectDisposedException">The session has ended.</exception>
    public IReadOnlyList<BrowserCookie> Cookies =>
        (_jar ?? throw new ObjectDisposedException(nameof(MaterializedCookieProfile))).Cookies;

    /// <summary>Ends this materialisation. The cookie references are dropped.</summary>
    public void Dispose() => _jar = null;
}
