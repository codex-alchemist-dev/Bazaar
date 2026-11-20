namespace Bazaar.Server.Council.Membership;

/// <summary>
/// Receives membership outcomes. Doc 07 (Soul and lifecycle state) and doc 25 (hiring) implement it and apply the change.
/// The Council never writes agent tables itself (docs/design/10-council-and-voting.md, Effect).
/// </summary>
public interface IMembershipOutcomeSink
{
    /// <summary>Publishes one outcome. Implementations must be idempotent by <see cref="MembershipOutcome.VoteId"/>.</summary>
    Task PublishAsync(MembershipOutcome outcome, CancellationToken ct);
}
