using System.Text;
using Bazaar.Contracts.Graph;
using Bazaar.Server.Vault.Data;

namespace Bazaar.Server.Constellation.Tiles;

/// <summary>
/// The graph the Constellation lays out, read from one Vault's index (doc 19: the Vault and the Constellation are two views of one
/// knowledge graph). Nodes are the Vault documents, keyed by their stable <c>doc_id</c>. Edges are the resolved wiki-links between them.
/// Postgres only indexes the Vault, so this reads the index and never the Markdown files.
/// </summary>
/// <remarks>
/// This source is global to the Vault. It does not filter by viewer: per-viewer access belongs to the caller that wraps it, so the
/// snapshot stays the same for every reader and the layout stays deterministic.
/// </remarks>
public sealed class VaultGraphSource(VaultIndexRepository index, Guid vaultId) : IConstellationGraphSource
{
    /// <summary>Reads the Vault index and builds the graph. Reads all documents on each call; the layout cache keys on the version.</summary>
    public async Task<GraphSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var entries = await index.ListEntriesAsync(vaultId, cancellationToken).ConfigureAwait(false);
        var links = await index.ListResolvedLinksAsync(vaultId, cancellationToken).ConfigureAwait(false);
        return VaultGraphProjection.Build(entries.Select(entry => entry.DocId), links);
    }
}

/// <summary>
/// The pure part of the Vault graph: node and edge order and the graph version. No I/O, so it is tested without Postgres.
/// The layout indexes nodes by their position in the snapshot, so the order must be deterministic.
/// </summary>
public static class VaultGraphProjection
{
    /// <summary>
    /// Builds the snapshot. Nodes are sorted by id. Edges that point at a missing node, and self-links, are dropped. Duplicate links
    /// collapse into one edge. Edges are sorted by source, then target.
    /// </summary>
    public static GraphSnapshot Build(IEnumerable<Guid> docIds, IEnumerable<(Guid SrcDoc, Guid TargetDoc)> links)
    {
        ArgumentNullException.ThrowIfNull(docIds);
        ArgumentNullException.ThrowIfNull(links);

        var nodeIds = docIds.Select(id => id.ToString("D")).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var nodeSet = new HashSet<string>(nodeIds, StringComparer.Ordinal);

        var edges = links
            .Select(link => (From: link.SrcDoc.ToString("D"), To: link.TargetDoc.ToString("D")))
            .Where(edge => edge.From != edge.To && nodeSet.Contains(edge.From) && nodeSet.Contains(edge.To))
            .Distinct()
            .OrderBy(edge => edge.From, StringComparer.Ordinal)
            .ThenBy(edge => edge.To, StringComparer.Ordinal)
            .Select(edge => new GraphEdge(edge.From, edge.To))
            .ToArray();

        var nodes = nodeIds.Select(id => new GraphNode(id)).ToArray();
        return new GraphSnapshot(VersionOf(nodeIds, edges), nodes, edges);
    }

    /// <summary>
    /// A 64-bit FNV-1a hash of the sorted nodes and edges, kept non-negative. The same graph always gets the same version, and any
    /// change to a node or an edge changes it.
    /// </summary>
    public static long VersionOf(IReadOnlyList<string> nodeIds, IReadOnlyList<GraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(edges);

        var hash = 14695981039346656037UL;
        foreach (var node in nodeIds)
        {
            hash = Fold(hash, "n " + node + "\n");
        }

        foreach (var edge in edges)
        {
            hash = Fold(hash, "e " + edge.From + " " + edge.To + "\n");
        }

        return (long)(hash & 0x7FFF_FFFF_FFFF_FFFFUL);
    }

    private static ulong Fold(ulong hash, string text)
    {
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return hash;
    }
}
