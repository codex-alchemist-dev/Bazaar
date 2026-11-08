using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Bazaar.Server.GitHub.Adapter;

/// <summary>A successful GitHub read body, with the pagination link it carried.</summary>
internal sealed record GitHubResponse(string Body, Uri? NextLink);

/// <summary>An ETag and the response it validates. A 304 for this ETag means the stored response is current.</summary>
internal sealed record CacheEntry(string ETag, GitHubResponse Response);

/// <summary>
/// Bounded, process-local ETag cache for GitHub reads (doc 21, ETag reads). Keys include the installation id, so one
/// installation never receives another's cached bodies.
/// </summary>
internal sealed class GitHubResponseCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly int _maxEntries;

    public GitHubResponseCache(int maxEntries)
    {
        _maxEntries = maxEntries;
    }

    public bool TryGet(string key, [NotNullWhen(true)] out CacheEntry? entry) => _entries.TryGetValue(key, out entry);

    public void Set(string key, CacheEntry entry)
    {
        if (_maxEntries <= 0)
        {
            return;
        }

        if (!_entries.ContainsKey(key) && _entries.Count >= _maxEntries)
        {
            foreach (var victim in _entries.Keys)
            {
                _entries.TryRemove(victim, out _);
                break;
            }
        }

        _entries[key] = entry;
    }
}
