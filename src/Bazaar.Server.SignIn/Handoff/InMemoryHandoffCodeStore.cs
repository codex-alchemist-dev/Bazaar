using System.Security.Cryptography;
using System.Text;
using Bazaar.Roles.Abstractions;

namespace Bazaar.Server.SignIn.Handoff;

/// <summary>
/// In-memory <see cref="IHandoffCodeStore"/>. Codes live 60 s and redeem once. Only the SHA-256 hash is a key, so a lookup compares
/// hashes (no secret-dependent comparison) and a memory dump holds nothing replayable. The store is bounded: when full it drops
/// expired entries, then the oldest.
/// </summary>
public sealed class InMemoryHandoffCodeStore : IHandoffCodeStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    public const int DefaultCapacity = 4096;
    public const int MaxCodeLength = 256;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly int _capacity;

    public InMemoryHandoffCodeStore(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>The number of codes held, for tests.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public Task<string> IssueAsync(SignInIdentity identity, string audience, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!SignInAudiences.IsKnown(audience))
        {
            throw new ArgumentException("Unknown audience.", nameof(audience));
        }

        var code = Base64Url(RandomNumberGenerator.GetBytes(32));
        var key = Hash(code);
        lock (_gate)
        {
            if (_entries.Count >= _capacity)
            {
                Evict(now);
            }

            _entries[key] = new Entry(identity, audience, now + Lifetime);
        }

        return Task.FromResult(code);
    }

    public Task<SignInIdentity?> RedeemAsync(string code, string audience, DateTimeOffset now, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(code) || code.Length > MaxCodeLength)
        {
            return Task.FromResult<SignInIdentity?>(null);
        }

        var key = Hash(code);
        Entry? entry;
        lock (_gate)
        {
            // Removed before any check: a wrong-audience or late attempt burns the code as well.
            if (_entries.Remove(key, out var found))
            {
                entry = found;
            }
            else
            {
                entry = null;
            }
        }

        if (entry is null || now >= entry.Expiry || !string.Equals(entry.Audience, audience, StringComparison.Ordinal))
        {
            return Task.FromResult<SignInIdentity?>(null);
        }

        return Task.FromResult<SignInIdentity?>(entry.Identity);
    }

    private void Evict(DateTimeOffset now)
    {
        foreach (var key in _entries.Where(kv => now >= kv.Value.Expiry).Select(kv => kv.Key).ToArray())
        {
            _entries.Remove(key);
        }

        while (_entries.Count >= _capacity)
        {
            var oldest = _entries.MinBy(kv => kv.Value.Expiry).Key;
            _entries.Remove(oldest);
        }
    }

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Entry(SignInIdentity Identity, string Audience, DateTimeOffset Expiry);
}
