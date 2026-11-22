using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.WebAdmin.Security;

/// <summary>
/// In-memory panel session store (doc 51 section 5). The cookie value is 32 random bytes (base64url); only its SHA-256 hash
/// (hex) is kept, as the session id. Lookup is by hash, so no secret comparison happens on attacker-timed data.
/// </summary>
public sealed class InMemoryAdminSessionStore : IAdminSessionStore
{
    private readonly ConcurrentDictionary<string, PanelSession> _sessions = new(StringComparer.Ordinal);
    private readonly AdminSessionOptions _options;
    private readonly IAdminRevocationCheck _revocation;

    public InMemoryAdminSessionStore(AdminSessionOptions options, IAdminRevocationCheck revocation)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(revocation);
        _options = options.Clamped();
        _revocation = revocation;
    }

    public Task<CreatedPanelSession> CreateAsync(AdminIdentity identity, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var cookie = Base64Url(RandomNumberGenerator.GetBytes(32));
        var session = new PanelSession(
            HashHex(cookie),
            identity.PrincipalId,
            identity.Kind,
            identity.DeviceId,
            now,
            now,
            now + _options.Absolute);
        _sessions[session.Id] = session;
        return Task.FromResult(new CreatedPanelSession(cookie, session));
    }

    public async Task<PanelSession?> ValidateAsync(string cookieValue, DateTimeOffset now, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cookieValue) || cookieValue.Length > 256)
        {
            return null;
        }

        var id = HashHex(cookieValue);
        if (!_sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        if (now >= session.AbsoluteExpiry || now >= session.LastSeen + _options.Sliding)
        {
            _sessions.TryRemove(id, out _);
            return null;
        }

        if (await _revocation.IsRevokedAsync(session.PrincipalId, session.DeviceId, ct).ConfigureAwait(false))
        {
            _sessions.TryRemove(id, out _);
            return null;
        }

        var slid = session with { LastSeen = now > session.LastSeen ? now : session.LastSeen };
        _sessions[id] = slid;
        return slid;
    }

    public Task RevokeAsync(string sessionId, CancellationToken ct = default)
    {
        if (sessionId is not null)
        {
            _sessions.TryRemove(sessionId, out _);
        }

        return Task.CompletedTask;
    }

    private static string HashHex(string cookie) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cookie)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
