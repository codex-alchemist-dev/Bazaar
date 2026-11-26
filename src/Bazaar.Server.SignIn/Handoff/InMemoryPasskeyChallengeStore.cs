using System.Security.Cryptography;

namespace Bazaar.Server.SignIn.Handoff;

/// <summary>In-memory <see cref="IPasskeyChallengeStore"/>: 60 s, single use, bounded. A challenge is public, so it is stored as issued.</summary>
public sealed class InMemoryPasskeyChallengeStore : IPasskeyChallengeStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    public const int DefaultCapacity = 4096;

    private readonly Dictionary<string, (string Challenge, DateTimeOffset Expiry)> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly int _capacity;

    public InMemoryPasskeyChallengeStore(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public Task<PasskeyChallenge> IssueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var id = InMemoryHandoffCodeStore.Base64Url(RandomNumberGenerator.GetBytes(16));
        var challenge = InMemoryHandoffCodeStore.Base64Url(RandomNumberGenerator.GetBytes(32));
        var expiry = now + Lifetime;
        lock (_gate)
        {
            if (_entries.Count >= _capacity)
            {
                foreach (var key in _entries.Where(kv => now >= kv.Value.Expiry).Select(kv => kv.Key).ToArray())
                {
                    _entries.Remove(key);
                }

                while (_entries.Count >= _capacity)
                {
                    _entries.Remove(_entries.MinBy(kv => kv.Value.Expiry).Key);
                }
            }

            _entries[id] = (challenge, expiry);
        }

        return Task.FromResult(new PasskeyChallenge(id, challenge, expiry));
    }

    public Task<string?> ConsumeAsync(string id, DateTimeOffset now, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64)
        {
            return Task.FromResult<string?>(null);
        }

        lock (_gate)
        {
            if (_entries.Remove(id, out var entry) && now < entry.Expiry)
            {
                return Task.FromResult<string?>(entry.Challenge);
            }
        }

        return Task.FromResult<string?>(null);
    }
}
