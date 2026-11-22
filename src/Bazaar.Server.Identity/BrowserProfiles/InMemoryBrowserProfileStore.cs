namespace Bazaar.Server.Identity.BrowserProfiles;

/// <summary>
/// In-memory <see cref="IBrowserProfileStore"/> for tests and for the fake DEK path. Cookie payloads are encrypted
/// with <see cref="IProfileEncryptor"/> before they are stored; this class never holds a plaintext jar.
/// </summary>
public sealed class InMemoryBrowserProfileStore(IProfileEncryptor encryptor) : IBrowserProfileStore
{
    private readonly System.Threading.Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _profiles = [];

    private sealed record Entry(BrowserProfile Profile, ProfileCiphertext? Blob);

    /// <inheritdoc />
    public Task<BrowserProfile> CreateAsync(
        string agentId,
        BrowserProfileKind kind,
        IEnumerable<string> serviceAllowlist,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentNullException.ThrowIfNull(serviceAllowlist);
        ct.ThrowIfCancellationRequested();

        var allowlist = serviceAllowlist.Select(CookieDomain.Normalize).Distinct(StringComparer.Ordinal).ToArray();
        if (kind == BrowserProfileKind.Anonymous && allowlist.Length > 0)
        {
            throw new ArgumentException("An anonymous profile takes no service allowlist.", nameof(serviceAllowlist));
        }

        var profile = new BrowserProfile(Guid.NewGuid(), agentId, kind, allowlist);
        lock (_gate)
        {
            _profiles[profile.ProfileId] = new Entry(profile, null);
        }

        return Task.FromResult(profile);
    }

    /// <inheritdoc />
    public Task<BrowserProfile?> FindAsync(Guid profileId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_profiles.TryGetValue(profileId, out var entry) ? entry.Profile : null);
        }
    }

    /// <inheritdoc />
    public Task WriteCookiesAsync(Guid profileId, CookieJar jar, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jar);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var entry = Find(profileId);
            if (entry.Profile.Kind == BrowserProfileKind.Anonymous)
            {
                if (jar.Count > 0)
                {
                    throw new InvalidOperationException("Anonymous browser profiles never hold cookies.");
                }

                _profiles[profileId] = entry with { Blob = null };
                return Task.CompletedTask;
            }

            foreach (var cookie in jar.Cookies)
            {
                if (!entry.Profile.ServiceAllowlist.Any(a => CookieDomain.IsWithin(cookie.Domain, a)))
                {
                    throw new ArgumentException($"Cookie {cookie} is outside this profile's service allowlist.", nameof(jar));
                }
            }

            var blob = encryptor.Encrypt(entry.Profile.AgentId, jar.Serialize());
            _profiles[profileId] = entry with { Blob = blob };
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<CookieJar> ReadCookiesAsync(Guid profileId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Entry entry;
        lock (_gate)
        {
            entry = Find(profileId);
        }

        if (entry.Blob is null)
        {
            return Task.FromResult(CookieJar.Empty);
        }

        var plaintext = encryptor.Decrypt(entry.Profile.AgentId, entry.Blob);
        return Task.FromResult(CookieJar.Deserialize(plaintext));
    }

    private Entry Find(Guid profileId) =>
        _profiles.TryGetValue(profileId, out var entry)
            ? entry
            : throw new KeyNotFoundException("Browser profile not found.");
}
