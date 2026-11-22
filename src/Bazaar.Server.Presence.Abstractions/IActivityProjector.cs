namespace Bazaar.Server.Presence.Abstractions;

/// <summary>
/// The single writer of <c>agent_activity</c> (docs/design/23, "Event sources and subscription"). Sources hand it events; it
/// serializes them per agent, drops duplicates and late events, upserts the row, bumps the revision and publishes the delta.
/// </summary>
public interface IActivityProjector
{
    /// <summary>Applies one event. An event older than the last applied source time, or a duplicate of it, changes nothing.</summary>
    ValueTask OnEventAsync(ActivityEvent e, CancellationToken ct);
}
