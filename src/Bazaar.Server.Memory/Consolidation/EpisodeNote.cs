using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Memory.Consolidation;

/// <summary>
/// One episodic note read as consolidation input. <see cref="Trust"/> is the tier the note already holds and
/// <see cref="SourceTrust"/> its recorded provenance; both decide whether the run's output is tainted.
/// </summary>
/// <param name="Id">Episode note id (ULID), recorded in each output's <c>derived_from</c>.</param>
/// <param name="Path">Vault-relative path of the episode file.</param>
/// <param name="Text">The full note text: front matter and body.</param>
/// <param name="Trust">The episode's current promotion tier.</param>
/// <param name="SourceTrust">The episode's recorded provenance.</param>
public sealed record EpisodeNote(
    string Id,
    string Path,
    string Text,
    MemoryTier Trust,
    ContentTrust SourceTrust);
