using Bazaar.Server.Memory.Retrieval;

namespace Bazaar.Server.Memory.GitVault;

/// <summary>The lexical index when no Vault id is configured: no candidates, so retrieval has nothing to fuse and says so.</summary>
public sealed class NullMemoryLexicalIndex : IMemoryLexicalIndex
{
    /// <inheritdoc />
    public Task<IReadOnlyList<RankedHit>> SearchAsync(string queryText, string areaPrefix, int maxCandidates, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RankedHit>>([]);
}
