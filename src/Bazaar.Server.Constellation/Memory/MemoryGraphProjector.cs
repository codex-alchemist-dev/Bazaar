using System.Text.RegularExpressions;
using Bazaar.Server.Memory.Notes;

namespace Bazaar.Server.Constellation.Memory;

/// <summary>
/// Projects one memory note revision into a <see cref="MemoryGraphRevision"/> (docs/design/constellation-memory-graph.md,
/// "Projector"). Pure: no IO, no clock, no randomness. The same input always gives the same output.
/// </summary>
public static class MemoryGraphProjector
{
    /// <summary>A <c>[[target]]</c> token. Links do not nest, so the target is everything up to the closing brackets.</summary>
    private static readonly Regex _wikiLink = new(@"\[\[([^\[\]]*)\]\]", RegexOptions.CultureInvariant);

    /// <summary>Expansion weights (see the spec, "Records"). Provenance edges carry 0.</summary>
    private const double _linkWeight = 1.0;
    private const double _derivedFromWeight = 0.8;
    private const double _supersedesWeight = 0.5;
    private const double _provenanceWeight = 0.0;

    /// <summary>
    /// Builds the graph revision for one note. A wiki-link resolves when its target equals a note id (any value of
    /// <paramref name="noteIdsByTitle"/>) or a title (a key of it). Dangling targets and self-links produce no edge.
    /// </summary>
    /// <param name="note">The note front matter.</param>
    /// <param name="body">The note body, scanned for wiki-links. May be empty.</param>
    /// <param name="vaultPath">The normalized vault path of the note.</param>
    /// <param name="scopeAgentId">The agent whose private area holds the note.</param>
    /// <param name="noteIdsByTitle">Visible notes, keyed by title (ordinal, case-sensitive), valued by note id.</param>
    public static MemoryGraphRevision Project(
        MemoryNote note,
        string body,
        string vaultPath,
        string scopeAgentId,
        IReadOnlyDictionary<string, string> noteIdsByTitle)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeAgentId);
        ArgumentNullException.ThrowIfNull(noteIdsByTitle);

        var edges = new Dictionary<(string TargetId, MemoryEdgeKind Kind), MemoryGraphEdge>();
        var knownIds = new HashSet<string>(noteIdsByTitle.Values, StringComparer.Ordinal);

        foreach (Match match in _wikiLink.Matches(body))
        {
            var target = match.Groups[1].Value;
            var pipe = target.IndexOf('|', StringComparison.Ordinal);
            if (pipe >= 0)
            {
                target = target[..pipe];
            }

            target = target.Trim();
            if (target.Length == 0)
            {
                continue;
            }

            string? resolved = null;
            if (noteIdsByTitle.TryGetValue(target, out var idForTitle))
            {
                resolved = idForTitle;
            }
            else if (knownIds.Contains(target))
            {
                resolved = target;
            }

            // A self-link is not an edge, whichever way it resolved.
            if (resolved is null || resolved == note.Id)
            {
                continue;
            }

            Add(edges, new MemoryGraphEdge(resolved, MemoryEdgeKind.Link, _linkWeight));
        }

        foreach (var id in note.DerivedFrom)
        {
            Add(edges, new MemoryGraphEdge(id, MemoryEdgeKind.DerivedFrom, _derivedFromWeight));
        }

        foreach (var id in note.Supersedes)
        {
            Add(edges, new MemoryGraphEdge(id, MemoryEdgeKind.Supersedes, _supersedesWeight));
        }

        Add(edges, new MemoryGraphEdge("agent:" + scopeAgentId, MemoryEdgeKind.AuthoredBy, _provenanceWeight));

        if (!string.IsNullOrEmpty(note.Origin?.Gc))
        {
            Add(edges, new MemoryGraphEdge("gc:" + note.Origin.Gc, MemoryEdgeKind.FromGc, _provenanceWeight));
        }

        if (note.Origin is not null)
        {
            foreach (var messageId in note.Origin.Messages)
            {
                Add(edges, new MemoryGraphEdge("message:" + messageId, MemoryEdgeKind.FromMessage, _provenanceWeight));
            }
        }

        var ordered = edges.Values
            .OrderBy(e => (int)e.Kind)
            .ThenBy(e => e.TargetId, StringComparer.Ordinal)
            .ToList();

        return new MemoryGraphRevision(
            NoteId: note.Id,
            Version: note.Version,
            ScopeAgentId: scopeAgentId,
            VaultPath: vaultPath,
            Title: note.Title,
            Kind: note.Kind.ToString(),
            Status: note.Status.ToString(),
            Trust: note.Trust.ToString(),
            Edges: ordered);
    }

    /// <summary>Adds an edge unless one with the same target and kind is already present (the first weight wins).</summary>
    private static void Add(Dictionary<(string TargetId, MemoryEdgeKind Kind), MemoryGraphEdge> edges, MemoryGraphEdge edge) =>
        edges.TryAdd((edge.TargetId, edge.Kind), edge);
}
