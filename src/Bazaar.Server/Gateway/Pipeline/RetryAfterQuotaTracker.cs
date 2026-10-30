using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Health;

namespace Bazaar.Server.Gateway.Pipeline;

/// <summary>
/// Maps an upstream <c>rate_limited</c> answer that carries <c>Retry-After</c> onto <see cref="QuotaState.RateLimited"/>
/// for that provider key and model class, until the retry time passes. Without a retry time nothing is recorded, so the
/// next call simply tries again. In memory: a restart clears it. Quota stays per key, never per agent.
/// </summary>
public sealed class RetryAfterQuotaTracker(TimeProvider time) : IQuotaTracker
{
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly Lock _gate = new();
    private readonly Dictionary<(ProviderKey Key, string ModelClass), DateTimeOffset> _until = [];

    public QuotaState? BlockedState(ProviderKey key, string modelClass) =>
        BlockedUntil(key, modelClass) is { } ? QuotaState.RateLimited : null;

    public DateTimeOffset? BlockedUntil(ProviderKey key, string modelClass)
    {
        lock (_gate)
        {
            if (!_until.TryGetValue((key, modelClass), out var until))
            {
                return null;
            }

            if (until > _time.GetUtcNow())
            {
                return until;
            }

            _until.Remove((key, modelClass));
            return null;
        }
    }

    public void Report(ProviderKey key, string modelClass, ModelEvent.Failure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Code != "rate_limited" || failure.RetryAfterSeconds is not { } seconds || seconds <= 0)
        {
            return;
        }

        var until = _time.GetUtcNow().AddSeconds(seconds);
        lock (_gate)
        {
            var slot = (key, modelClass);
            _until[slot] = _until.TryGetValue(slot, out var existing) && existing > until ? existing : until;
        }
    }
}
