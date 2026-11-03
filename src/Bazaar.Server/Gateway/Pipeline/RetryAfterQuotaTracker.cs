using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Health;

namespace Bazaar.Server.Gateway.Pipeline;

/// <summary>
/// Turns quota failures into blocks for one provider key and model class (doc 05, work package 8). Quota is per key,
/// never per agent, because agents share a key.
/// <list type="bullet">
/// <item><c>rate_limited</c> with <c>Retry-After</c> blocks as <see cref="QuotaState.RateLimited"/> until the retry time.</item>
/// <item><c>spend_capped</c> blocks as <see cref="QuotaState.SpendCapped"/> until 00:00 UTC on the 1st of the next month.
/// It ignores any retry timer, so it is never a backoff.</item>
/// <item><c>rate_limited</c> without a usable retry time records nothing, so the next call simply tries again.</item>
/// </list>
/// When two blocks overlap, the one that lasts longer wins. In memory: a restart clears it.
/// </summary>
public sealed class RetryAfterQuotaTracker(TimeProvider time) : IQuotaTracker
{
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly Lock _gate = new();
    private readonly Dictionary<(ProviderKey Key, string ModelClass), Block> _blocks = [];

    public QuotaState? BlockedState(ProviderKey key, string modelClass) => Current(key, modelClass)?.State;

    public DateTimeOffset? BlockedUntil(ProviderKey key, string modelClass) => Current(key, modelClass)?.Until;

    public void Report(ProviderKey key, string modelClass, ModelEvent.Failure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var now = _time.GetUtcNow();
        var block = failure.Code switch
        {
            "spend_capped" => new Block(QuotaState.SpendCapped, NextMonthStart(now)),
            "rate_limited" when failure.RetryAfterSeconds is { } seconds && seconds > 0 =>
                new Block(QuotaState.RateLimited, now.AddSeconds(seconds)),
            _ => null,
        };

        if (block is null)
        {
            return;
        }

        lock (_gate)
        {
            var slot = (key, modelClass);
            if (!_blocks.TryGetValue(slot, out var existing) || existing.Until <= block.Until)
            {
                _blocks[slot] = block;
            }
        }
    }

    private Block? Current(ProviderKey key, string modelClass)
    {
        lock (_gate)
        {
            if (!_blocks.TryGetValue((key, modelClass), out var block))
            {
                return null;
            }

            if (block.Until > _time.GetUtcNow())
            {
                return block;
            }

            _blocks.Remove((key, modelClass));
            return null;
        }
    }

    /// <summary>The first instant of the next calendar month in UTC, strictly after <paramref name="now"/>.</summary>
    private static DateTimeOffset NextMonthStart(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
    }

    private sealed record Block(QuotaState State, DateTimeOffset Until);
}
