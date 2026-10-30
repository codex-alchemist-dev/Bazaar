using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Graph;

/// <summary>
/// One node of the knowledge graph (docs/design/19-constellation-and-git-solar-system.md, data and interfaces).
/// <see cref="Kind"/> is a string checked against <see cref="Known.NodeKinds"/>.
/// </summary>
/// <param name="Id">Stable key: the Vault document ID, the PR key, the task ID, the GC ID, the asset ID, or the sha or ref.</param>
/// <param name="Kind">Node kind; see <see cref="Known.NodeKinds"/>.</param>
/// <param name="Label">Text shown for the node.</param>
/// <param name="ClusterId">Cluster the node belongs to, or null before clustering.</param>
public sealed record GraphNode(string Id, string Kind, string Label, string? ClusterId);

/// <summary>
/// One edge of the knowledge graph. <see cref="Kind"/> is checked against <see cref="Known.EdgeKinds"/>.
/// </summary>
/// <param name="FromId">Source node ID.</param>
/// <param name="ToId">Target node ID.</param>
/// <param name="Kind">Edge kind; see <see cref="Known.EdgeKinds"/>.</param>
public sealed record GraphEdge(string FromId, string ToId, string Kind);

/// <summary>A full projection of the graph at one version. Later frames refer to <see cref="GraphVersion"/>.</summary>
/// <param name="GraphVersion">Version of the projection; rises on every change.</param>
/// <param name="Nodes">Nodes visible to the viewer.</param>
/// <param name="Edges">Edges between visible nodes.</param>
public sealed record GraphSnapshot(long GraphVersion, IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);

/// <summary>
/// Layout positions for one focus. The client drops any frame older than the newest it applied, comparing
/// <see cref="GraphVersion"/> first and then <see cref="LayoutSeq"/>.
/// </summary>
/// <param name="GraphVersion">Graph version the positions were computed for.</param>
/// <param name="LayoutSeq">Layout sequence number, rising within a graph version.</param>
/// <param name="FocusId">Node pinned at the origin.</param>
/// <param name="Xy">
/// Positions packed node by node: node <c>i</c> starts at <c>i * Dimensions</c>, so 2D frames hold x,y pairs and 3D frames
/// hold x,y,z triples. The length is a multiple of <see cref="Dimensions"/>.
/// </param>
/// <param name="Dimensions">2 for the flat view (the default) or 3 for a later 3D space mode.</param>
public sealed record LayoutFrame(
    long GraphVersion,
    long LayoutSeq,
    string FocusId,
    [property: JsonConverter(typeof(PackedFloatsJsonConverter))] ReadOnlyMemory<float> Xy,
    int Dimensions = 2);

/// <summary>A client's request to focus a node. The server answers with new <see cref="LayoutFrame"/>s.</summary>
/// <param name="ViewId">The view asking.</param>
/// <param name="NodeId">The node to focus.</param>
public sealed record FocusRequest(string ViewId, string NodeId);
