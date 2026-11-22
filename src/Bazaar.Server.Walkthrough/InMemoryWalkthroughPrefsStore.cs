using System.Collections.Concurrent;

namespace Bazaar.Server.Walkthrough;

/// <summary>Keeps preferences in memory. For tests and a host with no durable preference store yet; a restart forgets them.</summary>
public sealed class InMemoryWalkthroughPrefsStore : IWalkthroughPrefsStore
{
    private readonly ConcurrentDictionary<string, WalkthroughPrefs> _items = new(StringComparer.Ordinal);

    public Task<WalkthroughPrefs?> GetAsync(string personId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_items.TryGetValue(personId, out var prefs) ? prefs : null);

    public Task SetAsync(string personId, WalkthroughPrefs prefs, CancellationToken cancellationToken = default)
    {
        _items[personId] = prefs;
        return Task.CompletedTask;
    }
}
