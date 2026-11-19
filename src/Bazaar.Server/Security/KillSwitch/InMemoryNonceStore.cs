namespace Bazaar.Server.Security.KillSwitch;

/// <summary>Process-local <see cref="INonceStore"/>. Expired entries are dropped whenever a new nonce is added.</summary>
public sealed class InMemoryNonceStore(TimeProvider? timeProvider = null) : INonceStore
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PendingNonce> _pending = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task AddAsync(PendingNonce nonce, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(nonce.Nonce);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var key in _pending.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList())
            {
                _pending.Remove(key);
            }

            _pending[Convert.ToHexStringLower(nonce.Nonce)] = nonce;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PendingNonce?> TryConsumeAsync(byte[] nonce, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        var key = Convert.ToHexStringLower(nonce);
        lock (_gate)
        {
            return Task.FromResult(_pending.Remove(key, out var found) ? found : null);
        }
    }
}
