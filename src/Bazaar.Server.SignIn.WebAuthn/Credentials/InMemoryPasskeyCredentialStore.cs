namespace Bazaar.Server.SignIn.WebAuthn.Credentials;

/// <summary>In-memory <see cref="IPasskeyCredentialStore"/>: the test seam and a stand-in where no database is configured.</summary>
public sealed class InMemoryPasskeyCredentialStore : IPasskeyCredentialStore
{
    private readonly List<StoredPasskeyCredential> _items = [];
    private readonly Lock _gate = new();

    public Task<StoredPasskeyCredential?> FindByCredentialIdAsync(byte[] credentialId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.FirstOrDefault(c => c.RevokedAt is null && c.CredentialId.AsSpan().SequenceEqual(credentialId)));
        }
    }

    public Task<bool> AddAsync(StoredPasskeyCredential credential, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        lock (_gate)
        {
            if (_items.Any(c => c.CredentialId.AsSpan().SequenceEqual(credential.CredentialId) || c.Id == credential.Id))
            {
                return Task.FromResult(false);
            }

            _items.Add(credential);
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryAdvanceSignCountAsync(Guid id, long expectedCount, long newCount, DateTimeOffset usedAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(c => c.Id == id && c.RevokedAt is null && c.SignCount == expectedCount);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            _items[index] = _items[index] with { SignCount = newCount };
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<byte[]>> ListCredentialIdsAsync(Guid principalId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<byte[]>>([.. _items.Where(c => c.PrincipalId == principalId && c.RevokedAt is null).Select(c => c.CredentialId)]);
        }
    }

    public Task<int> CountAsync(Guid principalId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.Count(c => c.PrincipalId == principalId && c.RevokedAt is null));
        }
    }

    public Task<bool> RevokeAsync(Guid id, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(c => c.Id == id && c.RevokedAt is null);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            _items[index] = _items[index] with { RevokedAt = now };
            return Task.FromResult(true);
        }
    }
}
