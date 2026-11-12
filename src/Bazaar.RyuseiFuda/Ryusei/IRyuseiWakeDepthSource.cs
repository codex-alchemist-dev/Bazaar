namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// Reads the chain depth a fire-woken turn runs at (doc 38, Chain depth). A woken turn runs at the chain depth of the trigger
/// that fired, which is <c>ryusei_trigger.chain_depth</c> for the trigger of that <c>ryusei_fire</c> row.
/// </summary>
public interface IRyuseiWakeDepthSource
{
    /// <summary>The chain depth of the trigger that fired <paramref name="fireId"/>, or null when no such fire exists.</summary>
    Task<int?> ChainDepthForFireAsync(Guid fireId, CancellationToken ct);
}
