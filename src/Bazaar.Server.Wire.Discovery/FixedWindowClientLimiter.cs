namespace Bazaar.Server.Wire.Discovery;

/// <summary>Fixed-window request counter per client key, with a hard cap on tracked keys.</summary>
internal sealed class FixedWindowClientLimiter(TimeProvider time)
{
    private const string Overflow = "*";
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _clients = new(StringComparer.Ordinal);

    /// <summary>Counts one request; false means refused, with the time until the window ends in <paramref name="retryAfter"/>.</summary>
    public bool TryAcquire(string key, int limit, TimeSpan window, int maxKeys, out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_clients.ContainsKey(key) && _clients.Count >= maxKeys)
            {
                Sweep(now, window);
                if (_clients.Count >= maxKeys)
                {
                    key = Overflow;
                }
            }

            var (start, count) = _clients.TryGetValue(key, out var entry) ? entry : (now, 0);
            if (now - start >= window)
            {
                (start, count) = (now, 0);
            }

            if (count >= limit)
            {
                _clients[key] = (start, count);
                retryAfter = start + window - now;
                return false;
            }

            _clients[key] = (start, count + 1);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private void Sweep(DateTimeOffset now, TimeSpan window)
    {
        foreach (var key in _clients.Where(p => now - p.Value.Start >= window).Select(p => p.Key).ToArray())
        {
            _clients.Remove(key);
        }
    }
}
