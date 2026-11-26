using Bazaar.Server.Memory.Notes;

namespace Bazaar.Server.Memory.Tools;

/// <summary>
/// Install settings the memory tools read (docs/design/08-memory-system.md, Settings). Shipped defaults apply when
/// not overridden.
/// </summary>
/// <param name="MaxNoteBytes">The <c>memory.note_max_bytes</c> limit, UTF-8 bytes including front matter.</param>
/// <param name="QueryCandidates">The <c>memory.query_candidates</c> limit on lexical candidates per query.</param>
/// <param name="RrfK">The reciprocal rank fusion constant (<c>vault.search.rrf_k</c>).</param>
public sealed record MemoryToolOptions(
    int MaxNoteBytes = SoulGuard.DefaultMaxNoteBytes,
    int QueryCandidates = 2000,
    int RrfK = 60);
