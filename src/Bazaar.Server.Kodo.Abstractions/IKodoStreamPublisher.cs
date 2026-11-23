using Bazaar.Contracts.Kodo;

namespace Bazaar.Server.Kodo;

/// <summary>
/// Where the Kodo monitor hands the events of the <c>kodo</c> stream (doc 46 section 15.2). <c>AddBazaarKodo</c> registers a publisher
/// that feeds the wire subscription engine's <c>kodo</c> stream on <c>/ws</c> (PROPOSED). A host may register its own before
/// <c>AddBazaarKodo</c> (every registration there is TryAdd). <see cref="NullKodoStreamPublisher"/> drops events.
/// </summary>
/// <remarks>
/// The monitor catches and logs any exception from the publisher and a cancelled token is the only way it stops a publish, so an
/// implementation can fail without hurting probes or the journal. Revisions are numbered by the monitor, gap-free per process.
/// </remarks>
public interface IKodoStreamPublisher
{
    /// <summary>Publishes one stream event (a snapshot, a state change or a heartbeat) to the stream's subscribers.</summary>
    ValueTask PublishAsync(KodoStreamEventDto streamEvent, CancellationToken cancellationToken);
}

/// <summary>A publisher that drops every event, for hosts and tests with no stream transport.</summary>
public sealed class NullKodoStreamPublisher : IKodoStreamPublisher
{
    public ValueTask PublishAsync(KodoStreamEventDto streamEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
