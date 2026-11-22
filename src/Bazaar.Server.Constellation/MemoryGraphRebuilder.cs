using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Tools;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Constellation.Memory;

/// <summary>The outcome of one agent rebuild (docs/design/constellation-memory-graph.md, "Rebuild").</summary>
/// <param name="Scanned">Files read under the agent's private memory area.</param>
/// <param name="Stored">Revisions stored because the graph had no revision, or only an older one.</param>
/// <param name="UpToDate">Notes whose active revision was already at or past the Vault version.</param>
/// <param name="Failed">Files that did not parse, or whose store call was refused or threw.</param>
public sealed record MemoryGraphRebuildResult(int Scanned, int Stored, int UpToDate, int Failed);

/// <summary>
/// Rebuilds one agent's graph from the Vault, the source of truth. Safe to run any number of times: a note whose active
/// revision is already current is left alone, and nothing is deleted. Wiki-links resolve by note id or by a title that
/// is unique within the agent's own area; an ambiguous title produces no edge.
/// </summary>
public sealed partial class MemoryGraphRebuilder(IMemoryVault vault, IMemoryGraphStore store, ILogger<MemoryGraphRebuilder> logger)
{
    private readonly IMemoryVault _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    private readonly IMemoryGraphStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILogger<MemoryGraphRebuilder> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Rebuilds the graph for <paramref name="agentId"/> from <c>agents/&lt;agent&gt;/memory/</c>.</summary>
    public async Task<MemoryGraphRebuildResult> RebuildAgentAsync(string agentId, CancellationToken ct)
    {
        if (!VaultPath.IsValidIdentifier(agentId))
        {
            throw new ArgumentException("The agent id is not a valid identifier.", nameof(agentId));
        }

        var prefix = $"agents/{agentId}/memory/";
        var paths = await _vault.ListAsync(prefix, ct).ConfigureAwait(false);

        var notes = new List<(VaultPath Path, NoteDocument Document)>();
        var failed = 0;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var text = await _vault.ReadNoteAsync(path, ct).ConfigureAwait(false);
            var parsed = text is null ? null : NoteDocument.Parse(text);
            if (parsed is not { IsSuccess: true })
            {
                failed++;
                LogUnreadable(_logger, agentId);
                continue;
            }

            notes.Add((path, parsed.Document!));
        }

        var titles = UniqueTitles(notes.Select(n => n.Document.Note));
        var stored = 0;
        var upToDate = 0;
        foreach (var (path, document) in notes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var active = await _store.GetActiveAsync([document.Note.Id], ct).ConfigureAwait(false);
                if (active.Count > 0 && active[0].Version >= document.Note.Version)
                {
                    upToDate++;
                    continue;
                }

                var revision = MemoryGraphProjector.Project(document.Note, document.Body, path.Value, agentId, titles);
                await _store.StoreRevisionAsync(revision, ct).ConfigureAwait(false);
                stored++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogStoreFailed(_logger, agentId, ex);
            }
        }

        return new MemoryGraphRebuildResult(paths.Count, stored, upToDate, failed);
    }

    private static Dictionary<string, string> UniqueTitles(IEnumerable<MemoryNote> notes)
    {
        var byTitle = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in notes)
        {
            if (byTitle.TryGetValue(note.Title, out var existing) && existing != note.Id)
            {
                ambiguous.Add(note.Title);
            }
            else
            {
                byTitle[note.Title] = note.Id;
            }
        }

        foreach (var title in ambiguous)
        {
            byTitle.Remove(title);
        }

        return byTitle;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Memory graph rebuild for agent {AgentId} skipped a file that did not read or parse.")]
    private static partial void LogUnreadable(ILogger logger, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Memory graph rebuild for agent {AgentId} failed to store a note.")]
    private static partial void LogStoreFailed(ILogger logger, string agentId, Exception ex);
}
