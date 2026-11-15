namespace Bazaar.Secrets.Fakes;

/// <summary>In-memory <see cref="ISecretRecordRepository"/> for tests and headless dev runs. Not for production data.</summary>
public sealed class InMemorySecretRecordRepository : ISecretRecordRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ScopeKeyRecord> _scopeKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Scope, string Name), SecretRecord> _secrets = new();

    public Task<ScopeKeyRecord?> GetScopeKeyAsync(string scopeKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_scopeKeys.GetValueOrDefault(scopeKey));
        }
    }

    public Task<bool> TryInsertScopeKeyAsync(ScopeKeyRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_scopeKeys.TryAdd(record.ScopeKey, record));
        }
    }

    public Task EraseScopeKeyAsync(string scopeKey, DateTimeOffset at, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_scopeKeys.TryGetValue(scopeKey, out var existing))
            {
                _scopeKeys[scopeKey] = existing with { WrappedKey = ReadOnlyMemory<byte>.Empty, DestroyedAt = at };
            }
        }

        return Task.CompletedTask;
    }

    public Task<SecretRecord?> GetSecretAsync(string scopeKey, string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_secrets.GetValueOrDefault((scopeKey, name)));
        }
    }

    public Task PutSecretAsync(SecretRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _secrets[(record.ScopeKey, record.Name)] = record;
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteSecretAsync(string scopeKey, string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_secrets.Remove((scopeKey, name)));
        }
    }

    public Task DeleteScopeSecretsAsync(string scopeKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var key in _secrets.Keys.Where(k => k.Scope == scopeKey).ToArray())
            {
                _secrets.Remove(key);
            }
        }

        return Task.CompletedTask;
    }
}
