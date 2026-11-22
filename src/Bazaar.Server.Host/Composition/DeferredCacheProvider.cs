using Bazaar.Server.Providers;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// The cache that consumers see while the host's cache selection is pending. Until <see cref="Resolve"/> runs, reads are misses, writes and
/// deletes are dropped, and the probe reports the cache as unusable with the reason. After resolution it delegates to the selected provider,
/// or keeps degrading when none was selected (C07: the cache is never a dependency that fails the platform).
/// </summary>
public sealed class DeferredCacheProvider : ICacheProvider
{
    /// <summary>Kind reported while the selection is pending or when nothing was selected.</summary>
    public const string KindName = "deferred";

    private readonly object _gate = new();
    private ICacheProvider? _selected;
    private bool _resolved;

    /// <inheritdoc />
    public string Kind => KindName;

    /// <summary>True once <see cref="Resolve"/> has run, whether or not a provider was selected.</summary>
    public bool IsResolved
    {
        get
        {
            lock (_gate)
            {
                return _resolved;
            }
        }
    }

    /// <summary>Records the host's decision. Null means no cache is used. A second call replaces the first.</summary>
    public void Resolve(ICacheProvider? provider)
    {
        lock (_gate)
        {
            _selected = provider;
            _resolved = true;
        }
    }

    /// <inheritdoc />
    public ProviderStatus Probe()
    {
        ICacheProvider? selected;
        bool resolved;
        lock (_gate)
        {
            selected = _selected;
            resolved = _resolved;
        }

        if (!resolved)
        {
            return new ProviderStatus(KindName, false, [], "cache selection is pending");
        }

        return selected?.Probe() ?? new ProviderStatus(KindName, false, [], "no cache is selected");
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan ttl, CancellationToken ct)
    {
        var selected = Current();
        if (selected is not null)
        {
            await selected.SetAsync(key, value, ttl, ct);
        }
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>?> GetAsync(string key, CancellationToken ct)
    {
        var selected = Current();
        return selected is null ? null : await selected.GetAsync(key, ct);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        var selected = Current();
        if (selected is not null)
        {
            await selected.DeleteAsync(key, ct);
        }
    }

    private ICacheProvider? Current()
    {
        lock (_gate)
        {
            return _resolved ? _selected : null;
        }
    }
}
