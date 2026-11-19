using Bazaar.Server.Memory.Retrieval;

namespace Bazaar.Server.Memory.GitVault;

/// <summary>
/// Keyword candidates for the memory tools (docs/design/08-memory-system.md, section 11). Each hit's id is the note's vault
/// path. Results are raw: the memory tools apply the access layer afterwards.
/// </summary>
public interface IMemoryLexicalIndex
{
    /// <summary>Candidates under <paramref name="areaPrefix"/> (which ends with '/'), best first, at most <paramref name="maxCandidates"/>.</summary>
    Task<IReadOnlyList<RankedHit>> SearchAsync(string queryText, string areaPrefix, int maxCandidates, CancellationToken ct);
}
