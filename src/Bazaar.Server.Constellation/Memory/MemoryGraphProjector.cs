using System.Text.RegularExpressions;
using Bazaar.Server.Memory.Notes;

namespace Bazaar.Server.Constellation.Memory;

/// <summary>
/// Projects one memory note revision into a <see cref="MemoryGraphRevision"/> (docs/design/constellation-memory-graph.md,
/// "Projector" and "Convergence design"). Pure: no IO, no clock, no randomness. The same input always gives the same output.
/// </summary>
/// <remarks>
/// <see cref="ProjectIndexed"/> is the converged path: the note is its Vault doc node and its links are the ones the Vault index
/// resolved. <see cref="Project"/> is the path for a note the Vault index has not seen yet; it resolves <c>[[note id]]</c> and
/// <c>[[Title]]</c> links against the map it is given, and stores no doc id.
/// </remarks>
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

        return Finish(note, vaultPath, scopeAgentId, edges, docId: null);
    }

    /// <summary>
    /// Builds the graph revision for a note the Vault index holds as doc <paramref name="docId"/>. The Link edges are the doc's
    /// resolved Vault links (<c>vault_links.target_doc</c>), so title, alias and path links resolve by the Vault rules; targets
    /// are doc ids in "D" format. A link to the doc itself is dropped. DerivedFrom, Supersedes and provenance edges are as in
    /// <see cref="Project"/>.
    /// </summary>
    /// <param name="note">The note front matter.</param>
    /// <param name="vaultPath">The normalized vault path of the note.</param>
    /// <param name="scopeAgentId">The agent whose private area holds the note.</param>
    /// <param name="docId">The Vault doc id of the note.</param>
    /// <param name="resolvedLinkTargets">The doc ids the Vault index resolved for this doc's wiki-links.</param>
    public static MemoryGraphRevision ProjectIndexed(
        MemoryNote note,
        string vaultPath,
        string scopeAgentId,
        Guid docId,
        IReadOnlyCollection<Guid> resolvedLinkTargets)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeAgentId);
        ArgumentNullException.ThrowIfNull(resolvedLinkTargets);
        if (docId == Guid.Empty)
        {
            throw new ArgumentException("The doc id must not be empty.", nameof(docId));
        }

        var edges = new Dictionary<(string TargetId, MemoryEdgeKind Kind), MemoryGraphEdge>();
        foreach (var target in resolvedLinkTargets)
        {
            if (target != docId && target != Guid.Empty)
            {
                Add(edges, new MemoryGraphEdge(target.ToString("D"), MemoryEdgeKind.Link, _linkWeight));
            }
        }

        return Finish(note, vaultPath, scopeAgentId, edges, docId);
    }

    /// <summary>Adds the front matter and provenance edges, orders the edges and builds the revision.</summary>
    private static MemoryGraphRevision Finish(
        MemoryNote note,
        string vaultPath,
        string scopeAgentId,
        Dictionary<(string TargetId, MemoryEdgeKind Kind), MemoryGraphEdge> edges,
        Guid? docId)
    {
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
            Edges: ordered,
            DocId: docId);
    }

    /// <summary>Adds an edge unless one with the same target and kind is already present (the first weight wins).</summary>
    private static void Add(Dictionary<(string TargetId, MemoryEdgeKind Kind), MemoryGraphEdge> edges, MemoryGraphEdge edge) =>
        edges.TryAdd((edge.TargetId, edge.Kind), edge);
}
