using Bazaar.Server.GitHub.Events;

namespace Bazaar.Server.GitHub.Ingress;

/// <summary>
/// One polled event and what to do with it. <see cref="Effects"/> is empty when <see cref="Apply"/> is false: an older event
/// is stored for the log but never applied over newer PR state (doc 21, Behaviour and edge cases).
/// </summary>
/// <param name="DedupeKey">The <c>github:poll:&lt;id&gt;</c> key from <see cref="GitHubEventDedupe.KeyFor"/>.</param>
public sealed record GitHubEventDelivery(GitHubEvent Event, string DedupeKey, bool Apply, IReadOnlyList<MappedGitHubEffect> Effects);

/// <summary>
/// Where mapped effects go. This is the documented seam to the GC log (doc 01) and the inbox (doc 06): those contracts are not
/// public on main yet, so this module binds to nothing in them. The host (or the PR service work package) supplies an
/// implementation; until then <see cref="UnwiredGitHubEventSink"/> keeps the poller disabled so no event is read and lost.
/// An implementation must be idempotent on <see cref="GitHubEventDelivery.DedupeKey"/>: a throw leaves the event unprocessed and it is delivered again.
/// </summary>
public interface IGitHubEventSink
{
    Task DeliverAsync(GitHubEventDelivery delivery, CancellationToken cancellationToken);
}

/// <summary>Default sink. It marks the seam as unwired: the poller does not start while this is the registered sink.</summary>
public sealed class UnwiredGitHubEventSink : IGitHubEventSink
{
    public Task DeliverAsync(GitHubEventDelivery delivery, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No IGitHubEventSink is registered; the GC and inbox wiring is a seam (doc 21, work package 5).");
}
