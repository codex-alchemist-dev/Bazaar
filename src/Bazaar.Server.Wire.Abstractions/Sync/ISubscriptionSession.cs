using Bazaar.Contracts.Wire;

namespace Bazaar.Server.Wire.Sync;

/// <summary>What <see cref="ISubscriptionSession.SubscribeAsync"/> decided, and the work to start once the response is queued.</summary>
public sealed class SubscribeOutcome
{
    private readonly IReadOnlyList<Func<Task>> _starts;

    /// <summary>Creates an outcome from the <c>res</c> payload and the per-stream work to start after it is sent.</summary>
    public SubscribeOutcome(SubscribeResult result, IReadOnlyList<Func<Task>> starts)
    {
        Result = result;
        _starts = starts;
    }

    /// <summary>The <c>res</c> payload; send it before calling <see cref="StartAsync"/> so replay never overtakes it.</summary>
    public SubscribeResult Result { get; }

    /// <summary>Runs replay (or sends <c>resync</c>) for every accepted stream. Completes when each stream is live or closed.</summary>
    public Task StartAsync() => Task.WhenAll(_starts.Select(start => start()));
}

/// <summary>One connection's subscriptions as other modules see them. Thread-safe: <c>subscribe</c> requests may overlap.</summary>
public interface ISubscriptionSession : IDisposable
{
    /// <summary>Streams this connection is subscribed to.</summary>
    IReadOnlyCollection<string> Streams { get; }

    /// <summary>
    /// Checks access and registers for live events on each stream, then returns. A stream gets <c>ok</c>, <c>denied</c>
    /// (also for unknown streams and GCs, so membership does not leak) or <c>resync</c>.
    /// </summary>
    Task<SubscribeOutcome> SubscribeAsync(Subscribe request, CancellationToken ct);

    /// <summary>Drops streams; unknown names are ignored.</summary>
    void Unsubscribe(IEnumerable<string> streams);
}
