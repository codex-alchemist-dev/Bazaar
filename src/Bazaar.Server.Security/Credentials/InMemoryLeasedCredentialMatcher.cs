using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Security.Credentials;

/// <summary>
/// Leased credential values held in process memory, for <see cref="ILeasedCredentialMatcher"/> (doc 15, "Reads for modules").
/// The lease path calls <see cref="Track"/> when it issues a lease and <see cref="Release"/> or <see cref="ReleaseAgent"/>
/// when it revokes one. A value stops matching at its expiry and is zeroed when it is released, replaced or found expired.
/// </summary>
/// <remarks>
/// The Identity lease store holds no values, so the adapter that feeds this matcher from the credential broker is a
/// follow-up. Thread-safe: every call takes one lock. Matching is an exact, case-sensitive match on UTF-8 bytes. No method
/// returns a value or says which lease matched.
/// </remarks>
public sealed class InMemoryLeasedCredentialMatcher(TimeProvider? timeProvider = null) : ILeasedCredentialMatcher
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<string, Entry>> _byAgent = new(StringComparer.Ordinal);

    /// <summary>Starts matching a leased value for the agent until <paramref name="expiresAt"/>. Replaces a value under the same lease key.</summary>
    /// <param name="agentId">The agent the lease belongs to. Must not be blank.</param>
    /// <param name="leaseKey">Identifies the lease, so it can be released. Must not be blank.</param>
    /// <param name="value">The credential value. Copied; the caller keeps and zeroes its own buffer. Must not be empty.</param>
    /// <param name="expiresAt">The lease's expiry. Must be in the future.</param>
    public void Track(string agentId, string leaseKey, ReadOnlySpan<byte> value, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseKey);
        if (value.IsEmpty)
        {
            throw new ArgumentException("A credential value must not be empty.", nameof(value));
        }

        if (expiresAt <= _time.GetUtcNow())
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "A lease must expire in the future.");
        }

        var entry = new Entry(value.ToArray(), expiresAt);
        lock (_gate)
        {
            if (!_byAgent.TryGetValue(agentId, out var leases))
            {
                leases = new Dictionary<string, Entry>(StringComparer.Ordinal);
                _byAgent[agentId] = leases;
            }

            if (leases.TryGetValue(leaseKey, out var previous))
            {
                previous.Clear();
            }

            leases[leaseKey] = entry;
        }
    }

    /// <summary>Stops matching one lease and zeroes its value. Releasing an unknown lease does nothing.</summary>
    public void Release(string agentId, string leaseKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseKey);
        lock (_gate)
        {
            if (_byAgent.TryGetValue(agentId, out var leases) && leases.Remove(leaseKey, out var entry))
            {
                entry.Clear();
                if (leases.Count == 0)
                {
                    _byAgent.Remove(agentId);
                }
            }
        }
    }

    /// <summary>Stops matching every lease of the agent (for example on a kill switch stop) and zeroes the values.</summary>
    public void ReleaseAgent(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        lock (_gate)
        {
            if (_byAgent.Remove(agentId, out var leases))
            {
                foreach (var entry in leases.Values)
                {
                    entry.Clear();
                }
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> ContainsLeasedCredentialAsync(string agentId, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(text);
        ct.ThrowIfCancellationRequested();
        if (text.Length == 0)
        {
            return ValueTask.FromResult(false);
        }

        var length = Encoding.UTF8.GetMaxByteCount(text.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var written = Encoding.UTF8.GetBytes(text, buffer);
            var haystack = buffer.AsSpan(0, written);
            var now = _time.GetUtcNow();
            lock (_gate)
            {
                if (!_byAgent.TryGetValue(agentId, out var leases))
                {
                    return ValueTask.FromResult(false);
                }

                var found = false;
                List<string>? expired = null;
                foreach (var (key, entry) in leases)
                {
                    if (entry.ExpiresAt <= now)
                    {
                        (expired ??= []).Add(key);
                        continue;
                    }

                    if (!found && haystack.IndexOf(entry.Value) >= 0)
                    {
                        found = true;
                    }
                }

                if (expired is not null)
                {
                    foreach (var key in expired)
                    {
                        leases.Remove(key, out var entry);
                        entry?.Clear();
                    }

                    if (leases.Count == 0)
                    {
                        _byAgent.Remove(agentId);
                    }
                }

                return ValueTask.FromResult(found);
            }
        }
        finally
        {
            // The text may hold the secret itself, so the copy is zeroed before the buffer goes back to the pool.
            CryptographicOperations.ZeroMemory(buffer.AsSpan(0, length));
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class Entry(byte[] value, DateTimeOffset expiresAt)
    {
        public byte[] Value { get; } = value;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public void Clear() => CryptographicOperations.ZeroMemory(Value);
    }
}
