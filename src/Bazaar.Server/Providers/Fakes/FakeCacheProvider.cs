namespace Bazaar.Server.Providers.Fakes;

/// <summary>In-memory cache provider for tests. Entries expire against an injectable clock.</summary>
public sealed class FakeCacheProvider : ICacheProvider
{
    private readonly bool _usable;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, (byte[] Value, DateTimeOffset Expires)> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Creates a fake cache with the given kind, usability and clock (defaults to the system clock).</summary>
    public FakeCacheProvider(string kind = "fake_cache", bool usable = true, TimeProvider? clock = null)
    {
        Kind = kind;
        _usable = usable;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Kind { get; }

    /// <inheritdoc />
    public ProviderStatus Probe() =>
        new(Kind, _usable, Array.Empty<string>(), _usable ? null : "fake provider unavailable");

    /// <inheritdoc />
    public Task SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan ttl, CancellationToken ct)
    {
        var expires = _clock.GetUtcNow() + ttl;
        var copy = value.ToArray();
        lock (_gate)
        {
            _entries[key] = (copy, expires);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>?> GetAsync(string key, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return Task.FromResult<ReadOnlyMemory<byte>?>(null);
            }

            if (entry.Expires <= now)
            {
                _entries.Remove(key);
                return Task.FromResult<ReadOnlyMemory<byte>?>(null);
            }

            return Task.FromResult<ReadOnlyMemory<byte>?>(entry.Value);
        }
    }
}
