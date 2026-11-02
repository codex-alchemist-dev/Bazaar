using Bazaar.Contracts.Soul;

namespace Bazaar.Client.Core.Souls;

/// <summary>
/// Supplies a Soul's version chain to the client. The implementation is a server call or a fixture; the
/// view model never calls it itself. Returns every version the caller may see, in any order.
/// </summary>
public interface IVersionHistorySource
{
    /// <summary>Gets the version chain for one Soul.</summary>
    /// <param name="soulId">The Soul to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The Soul's versions, in any order.</returns>
    Task<IReadOnlyList<SoulVersion>> GetHistoryAsync(Guid soulId, CancellationToken cancellationToken);
}
