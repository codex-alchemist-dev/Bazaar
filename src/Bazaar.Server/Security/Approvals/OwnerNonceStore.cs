namespace Bazaar.Server.Security.Approvals;

/// <summary>Remembers the owner nonces already used, so a signed payload works once (doc 32 section 5a).</summary>
public interface IOwnerNonceStore
{
    /// <summary>Records the nonce. Returns false when it was already used. Entries may be dropped after <paramref name="expiresAt"/>.</summary>
    Task<bool> TryUseAsync(byte[] nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}

/// <summary>Process-local nonce store. Use only in tests and single-process dev runs; the durable store is open work.</summary>
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
