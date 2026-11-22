using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Health;

/// <summary>A read of one key's breaker and its last active probe.</summary>
public sealed record HealthSnapshot(
    BreakerState State,
    int ConsecutiveFailures,
    DateTimeOffset LastChangedAt,
    ProbeResult? LastProbe);

/// <summary>
/// Keeps one <see cref="CircuitBreaker"/> per <see cref="ProviderKey"/> and feeds it passive health (real-call
/// outcomes) and active health (probe results). Quota states are per key, so the same tracker answers for every agent.
/// </summary>
public sealed class HealthTracker
{
    private readonly BreakerOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<ProviderKey, Entry> _entries = [];

    public HealthTracker(BreakerOptions options, TimeProvider? time = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? TimeProvider.System;
        // Validate once, eagerly, so a bad option fails at startup rather than on the first key.
        _ = new CircuitBreaker(_options, _time);
    }

    /// <summary>
    /// Whether a failure code counts against the provider's health. <c>provider_error</c>, <c>timeout</c> and
    /// <c>stream_dropped</c> count. <c>rate_limited</c> and <c>spend_capped</c> are quota facts, and
    /// <c>malformed_tool_json</c> is a model fault, so none of those count. Unknown codes do not count either.
    /// </summary>
    public static bool Classify(ModelEvent.Failure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.Code is "provider_error" or "timeout" or "stream_dropped";
    }

    /// <summary>
    /// Takes one call slot for the key. Call it before a real call, or before a probe. A <c>true</c> in HalfOpen
    /// must be followed by <see cref="Observe"/>, <see cref="RecordProbe"/> or <see cref="Abandon"/>.
    /// </summary>
    public bool TryAcquire(ProviderKey key) => GetOrCreate(key).Breaker.TryAcquire();

    /// <summary>Ends an acquired call without a health verdict, for example because it was cancelled.</summary>
    public void Abandon(ProviderKey key) => GetOrCreate(key).Breaker.Abandon();

    /// <summary>
    /// Passive health from a real call's event. <c>Done</c> is a success. A failure that <see cref="Classify"/>
    /// counts is a failure. Any other terminal failure (quota or model fault) ends the call without a health verdict.
    /// Other events are ignored.
    /// </summary>
    public void Observe(ProviderKey key, ModelEvent modelEvent)
    {
        ArgumentNullException.ThrowIfNull(modelEvent);
        var breaker = GetOrCreate(key).Breaker;
        switch (modelEvent)
        {
            case ModelEvent.Done:
                breaker.RecordSuccess();
                break;
            case ModelEvent.Failure failure when Classify(failure):
                breaker.RecordFailure();
                break;
            case ModelEvent.Failure:
                breaker.Abandon();
                break;
        }
    }

    /// <summary>Whether a call for the key would be allowed now. Takes no call slot.</summary>
    public bool IsAvailable(ProviderKey key) => GetOrCreate(key).Breaker.WouldAllow();

    /// <summary>Records an active probe's result and updates the breaker as its outcome.</summary>
    public void RecordProbe(ProviderKey key, ProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var entry = GetOrCreate(key);
        lock (_gate)
        {
            entry.LastProbe = result;
        }

        if (result.Healthy)
        {
            entry.Breaker.RecordSuccess();
        }
        else
        {
            entry.Breaker.RecordFailure();
        }
    }

    /// <summary>A snapshot of the key's breaker and last probe. An unseen key reads as closed with no probe.</summary>
    public HealthSnapshot GetState(ProviderKey key)
    {
        var entry = GetOrCreate(key);
        var (state, failures, changedAt) = entry.Breaker.Snapshot();
        ProbeResult? lastProbe;
        lock (_gate)
        {
            lastProbe = entry.LastProbe;
        }

        return new HealthSnapshot(state, failures, changedAt, lastProbe);
    }

    /// <summary><see cref="QuotaState.Down"/> while the key's breaker is open, otherwise null.</summary>
    public QuotaState? DownState(ProviderKey key) =>
        GetOrCreate(key).Breaker.State == BreakerState.Open ? QuotaState.Down : null;

    private Entry GetOrCreate(ProviderKey key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(new CircuitBreaker(_options, _time));
                _entries[key] = entry;
            }

            return entry;
        }
    }

    private sealed class Entry(CircuitBreaker breaker)
    {
        public CircuitBreaker Breaker { get; } = breaker;

        // Guarded by the tracker's gate.
        public ProbeResult? LastProbe { get; set; }
    }
}
