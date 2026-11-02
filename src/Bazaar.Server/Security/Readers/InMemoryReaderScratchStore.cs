using System.Collections.Concurrent;

namespace Bazaar.Server.Security.Readers;

/// <summary>
/// In-memory scratch store. Per-session dictionaries are removed whole on purge, and purged session ids are remembered so a
/// late write from a stale session cannot recreate scratch space.
/// </summary>
public sealed class InMemoryReaderScratchStore : IReaderScratchStore
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, string>> _sessions = new();
    private readonly ConcurrentDictionary<Guid, byte> _purged = new();

    /// <inheritdoc />
    public Task PutAsync(Guid sessionId, string key, string value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ct.ThrowIfCancellationRequested();

        if (_purged.ContainsKey(sessionId))
        {
            throw new InvalidOperationException("The reader session has ended; its scratch space is closed.");
        }

        var items = _sessions.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));
        items[key] = value;

        // A purge can race with this write. Re-check, and drop anything written after the purge.
        if (_purged.ContainsKey(sessionId))
        {
            _sessions.TryRemove(sessionId, out _);
            throw new InvalidOperationException("The reader session has ended; its scratch space is closed.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetAsync(Guid sessionId, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();

        string? value = null;
        if (_sessions.TryGetValue(sessionId, out var items))
        {
            items.TryGetValue(key, out value);
        }

        return Task.FromResult(value);
    }

    /// <inheritdoc />
    public Task PurgeAsync(Guid sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _purged[sessionId] = 0;
        _sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    /// <summary>Number of live items for a session. Zero after purge.</summary>
    public int CountItems(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var items) ? items.Count : 0;
}
