namespace Bazaar.Server.CodingGit;

/// <summary>
/// In-process <see cref="IWorktreeLocks"/>. The server is one process per instance, so a map under a lock is enough;
/// a crash drops every lease, and an expired lease can be taken over, so no holder blocks a key forever.
/// </summary>
public sealed class WorktreeLocks(TimeProvider? timeProvider = null) : IWorktreeLocks
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly Dictionary<string, Lease> _held = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<IWorktreeLease?> TryAcquireAsync(string key, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            if (_held.TryGetValue(key, out var existing) && existing.IsLive(now))
            {
                return ValueTask.FromResult<IWorktreeLease?>(null);
            }

            var lease = new Lease(this, key, leaseDuration, now + leaseDuration);
            _held[key] = lease;
            return ValueTask.FromResult<IWorktreeLease?>(lease);
        }
    }

    private sealed class Lease(WorktreeLocks owner, string key, TimeSpan duration, DateTimeOffset expiresAt) : IWorktreeLease
    {
        private DateTimeOffset _expiresAt = expiresAt;
        private bool _released;

        public string Key { get; } = key;

        public bool IsHeld
        {
            get
            {
                lock (owner._sync)
                {
                    return IsLive(owner._time.GetUtcNow());
                }
            }
        }

        public bool TryRenew()
        {
            lock (owner._sync)
            {
                var now = owner._time.GetUtcNow();
                if (!IsLive(now))
                {
                    return false;
                }

                _expiresAt = now + duration;
                return true;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (owner._sync)
            {
                if (!_released)
                {
                    _released = true;
                    if (owner._held.TryGetValue(Key, out var current) && ReferenceEquals(current, this))
                    {
                        owner._held.Remove(Key);
                    }
                }
            }

            return ValueTask.CompletedTask;
        }

        // Caller holds owner._sync. A lease replaced after expiry is no longer in the map and counts as lost.
        internal bool IsLive(DateTimeOffset now) =>
            !_released && now < _expiresAt && owner._held.TryGetValue(Key, out var current) && ReferenceEquals(current, this);
    }
}
