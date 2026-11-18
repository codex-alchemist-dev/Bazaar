namespace Bazaar.Server.Approvals;

/// <summary>Process-local nonce store. Use only in tests and single-process dev runs; the durable store is <see cref="Db.PostgresOwnerNonceStore"/>.</summary>
public sealed class InMemoryOwnerNonceStore : IOwnerNonceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<bool> TryUseAsync(byte[] nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        var key = Convert.ToHexStringLower(nonce);
        lock (_gate)
        {
            if (_used.ContainsKey(key))
            {
                return Task.FromResult(false);
            }

            _used[key] = expiresAt;
            return Task.FromResult(true);
        }
    }
}
