using Bazaar.Contracts.Kodo;

namespace Bazaar.Server.Kodo;

/// <summary>
/// Where the Kodo monitor hands the events of the <c>kodo</c> stream (doc 46 section 15.2). OPEN: doc 02's stream registry and the
/// <c>/ws</c> transport do not expose a registration seam to modules yet, so no transport is chosen here. A host that has one
/// registers its own publisher before <c>AddBazaarKodo</c> (every registration there is TryAdd). The default drops events.
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

/// <summary>The default publisher: it drops every event. Replace it when the host has a stream transport.</summary>
public sealed class NullKodoStreamPublisher : IKodoStreamPublisher
{
    public ValueTask PublishAsync(KodoStreamEventDto streamEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
