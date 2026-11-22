namespace Bazaar.Constellation.Communities.Abstractions;

/// <summary>
/// Groups nodes into communities and lays the groups out so each community sits together. The work runs off the
/// calling thread, so a UI caller never blocks, and it honours cancellation between phases.
/// </summary>
public interface ICommunityLayout
{
    /// <exception cref="ArgumentException">An edge names a node that is not in <paramref name="nodes"/>, or a node id is empty.</exception>
    Task<CommunityLayoutResult> ComputeAsync(
        IReadOnlyList<CommunityNode> nodes,
        IReadOnlyList<CommunityEdge> edges,
        CommunityLayoutOptions? options = null,
        CancellationToken cancellationToken = default);
}
