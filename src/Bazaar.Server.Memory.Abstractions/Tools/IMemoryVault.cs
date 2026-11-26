using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Retrieval;
using Bazaar.Server.Memory.Write;

namespace Bazaar.Server.Memory.Tools;

/// <summary>
/// The Vault seam the memory tools need (docs/design/08-memory-system.md, sections 2 and 8). The real writer and
/// index implement it later. Reads here are raw: callers apply <see cref="IVaultAccess"/> before showing anything.
/// Writes arrive through <see cref="IMemoryBatchSink"/>, the same sink the write queue flushes to.
/// </summary>
public interface IMemoryVault : IMemoryBatchSink
{
    /// <summary>The full text of a note, or null when no file exists at <paramref name="path"/>.</summary>
    Task<string?> ReadNoteAsync(VaultPath path, CancellationToken ct);

    /// <summary>Every file path under <paramref name="areaPrefix"/> (which ends with '/'), in ordinal order.</summary>
    Task<IReadOnlyList<VaultPath>> ListAsync(string areaPrefix, CancellationToken ct);

    /// <summary>
    /// Keyword candidates under <paramref name="areaPrefix"/>, best first. Each <see cref="RankedHit.Id"/> is the
    /// note's vault path. At most <paramref name="maxCandidates"/> are returned.
    /// </summary>
    Task<IReadOnlyList<RankedHit>> SearchLexicalAsync(string queryText, string areaPrefix, int maxCandidates, CancellationToken ct);

    /// <summary>The path of the committed note with this id, or null when none exists.</summary>
    Task<VaultPath?> FindNoteByIdAsync(string noteId, CancellationToken ct);
}
