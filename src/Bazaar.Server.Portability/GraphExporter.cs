using System.Globalization;
using System.Text.Json;
using System.Xml;
using Bazaar.Contracts.Graph;

namespace Bazaar.Server.Portability;

/// <summary>The graph the caller may read, as the live Constellation projection shows it (doc 19).</summary>
public interface IGraphExportSource
{
    /// <summary>
    /// The viewer-filtered snapshot. The host applies the same visibility rules as the live projection (doc 19, doc 18 access);
    /// the exporter writes what it is given and adds nothing.
    /// </summary>
    Task<GraphSnapshot> GetSnapshotAsync(CancellationToken ct);
}

/// <summary>
/// The layout positions the layout engine computed for one graph version (doc 19). The shape mirrors <c>LayoutFrame</c>: <see cref="Xyz"/>
/// is packed x, y, z per node (stride 3), in the node order of the snapshot with the same <see cref="GraphVersion"/>. 2D layouts keep z at 0.
/// </summary>
public sealed record GraphLayoutPositions(long GraphVersion, ReadOnlyMemory<float> Xyz);

/// <summary>
/// The layout the host holds, read by the graph exporter (doc 44 section 4.5). The host must compute the positions from the same
/// viewer-filtered snapshot the exporter reads, so no node the viewer cannot read gets a position.
/// </summary>
public interface IGraphLayoutSource
{
    /// <summary>The positions computed for <paramref name="graphVersion"/>, or null when none is held for that version.</summary>
    Task<GraphLayoutPositions?> GetPositionsAsync(long graphVersion, CancellationToken ct);
}

/// <summary>What a graph export wrote. <see cref="Skipped"/> is true when the derived export is off, and nothing was read.</summary>
public sealed record GraphExportResult(
    bool Skipped,
    int NodeCount,
    int EdgeCount,
    int DroppedEdgeCount,
    IReadOnlyList<string> Files);

/// <summary>
/// Constellation graph exporter (doc 44 sections 4.5 and 12). The graph is derived from the Vault, GC, Kanban and PR data, so it is
/// a view for other tools and never authoritative. It runs only when <see cref="PortabilityExportOptions.IncludeDerived"/> is true.
/// </summary>
/// <remarks>
/// Files: <c>graph/derived.json</c> (the marker: derived true, authoritative false, the graph version and counts),
/// <c>graph/nodes.jsonl</c>, <c>graph/edges.jsonl</c>, <c>graph/graph.graphml</c> and, when a layout source is given and holds the
/// snapshot's graph version, <c>graph/layout.jsonl</c> (doc 44 section 4.5). Node and edge order is canonical (ordinal
/// by reference, then by kind), so the bytes do not depend on the snapshot's order. An edge whose endpoint is not a node of the
/// snapshot is dropped and counted. Layout positions are derived: they sit under <c>graph/</c>, which the marker covers, and they
/// are written only when derived exports are on. A layout held for another graph version is skipped, not exported.
/// </remarks>
public sealed class GraphExporter(IGraphExportSource source, PortabilityExportOptions options, IGraphLayoutSource? layout = null)
{
    /// <summary>The schema version of <c>graph/derived.json</c> (doc 44 section 4.1).</summary>
    public const string SchemaVersion = "1.0.0";

    public const string DerivedPath = "graph/derived.json";
    public const string NodesPath = "graph/nodes.jsonl";
    public const string EdgesPath = "graph/edges.jsonl";
    public const string GraphMlPath = "graph/graph.graphml";
    public const string LayoutPath = "graph/layout.jsonl";

    private const string _graphMlNamespace = "http://graphml.graphdrawing.org/xmlns";
    private const string _derivedNote =
        "Derived view of the Constellation graph. The primary copy is the Vault, GC, Kanban and PR data. This file can be rebuilt and is never imported as authoritative.";

    /// <summary>Exports the graph into <paramref name="sink"/>, or does nothing when derived exports are off.</summary>
    public async Task<GraphExportResult> ExportAsync(IExportFileSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!options.IncludeDerived)
        {
            return new GraphExportResult(true, 0, 0, 0, []);
        }

        var snapshot = await source.GetSnapshotAsync(ct);
        var nodes = Canonical(snapshot);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
        {
            index[nodes[i].Id] = i;
        }

        var edges = snapshot.Edges
            .Where(e => index.ContainsKey(e.From) && index.ContainsKey(e.To))
            .OrderBy(e => e.From, StringComparer.Ordinal)
            .ThenBy(e => e.To, StringComparer.Ordinal)
            .ThenBy(e => EdgeKindText(e.Kind), StringComparer.Ordinal)
            .ToList();
        var dropped = snapshot.Edges.Count - edges.Count;

        // Positions are checked before any file is opened, so a malformed layout leaves nothing half-written.
        var positions = layout is null ? null : await layout.GetPositionsAsync(snapshot.GraphVersion, ct);
        var xyz = CanonicalPositions(snapshot, nodes, positions);

        var files = new List<string>();
        await WriteDerivedMarkerAsync(sink, files, snapshot.GraphVersion, nodes.Count, edges.Count, dropped, ct);
        await WriteNodesAsync(sink, files, nodes, ct);
        await WriteEdgesAsync(sink, files, edges, ct);
        if (xyz is not null)
        {
            await WriteLayoutAsync(sink, files, nodes, xyz, ct);
        }

        await WriteGraphMlAsync(sink, files, snapshot.GraphVersion, nodes, edges, ct);

        return new GraphExportResult(false, nodes.Count, edges.Count, dropped, files);
    }

    /// <summary>Sorts the nodes by reference and refuses a snapshot that names one node twice.</summary>
    private static List<GraphNode> Canonical(GraphSnapshot snapshot)
    {
        var nodes = snapshot.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        for (var i = 1; i < nodes.Count; i++)
        {
            if (nodes[i].Id == nodes[i - 1].Id)
            {
                throw new InvalidOperationException("Graph snapshot names node " + nodes[i].Id + " twice.");
            }
        }

        return nodes;
    }

    private static async Task WriteDerivedMarkerAsync(
        IExportFileSink sink,
        List<string> files,
        long graphVersion,
        int nodeCount,
        int edgeCount,
        int droppedEdgeCount,
        CancellationToken ct)
    {
        await using var stream = await sink.OpenAsync(DerivedPath, ct);
        files.Add(DerivedPath);
        using var json = new Utf8JsonWriter(stream);
        json.WriteStartObject();
        json.WriteString("schemaVersion", SchemaVersion);
        json.WriteBoolean("derived", true);
        json.WriteBoolean("authoritative", false);
        json.WriteNumber("graphVersion", graphVersion);
        json.WriteNumber("nodeCount", nodeCount);
        json.WriteNumber("edgeCount", edgeCount);
        json.WriteNumber("droppedEdgeCount", droppedEdgeCount);
        json.WriteStartArray("derivedFrom");
        json.WriteStringValue("vault");
        json.WriteStringValue("gc");
        json.WriteStringValue("kanban");
        json.WriteStringValue("pull_requests");
        json.WriteEndArray();
        json.WriteString("note", _derivedNote);
        json.WriteEndObject();
        await json.FlushAsync(ct);
    }

    private static async Task WriteNodesAsync(IExportFileSink sink, List<string> files, List<GraphNode> nodes, CancellationToken ct)
    {
        await using var stream = await sink.OpenAsync(NodesPath, ct);
        files.Add(NodesPath);
        var lines = new JsonLineWriter(stream);
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var index = i;
            await lines.WriteAsync(json =>
            {
                json.WriteStartObject();
                json.WriteNumber("index", index);
                json.WriteString("ref", node.Id);
                json.WriteString("kind", NodeKindText(node.Kind));
                json.WriteEndObject();
            }, ct);
        }
    }

    private static async Task WriteEdgesAsync(IExportFileSink sink, List<string> files, List<GraphEdge> edges, CancellationToken ct)
    {
        await using var stream = await sink.OpenAsync(EdgesPath, ct);
        files.Add(EdgesPath);
        var lines = new JsonLineWriter(stream);
        foreach (var edge in edges)
        {
            await lines.WriteAsync(json =>
            {
                json.WriteStartObject();
                json.WriteString("from", edge.From);
                json.WriteString("to", edge.To);
                json.WriteString("kind", EdgeKindText(edge.Kind));
                json.WriteEndObject();
            }, ct);
        }
    }

    /// <summary>
    /// The positions in canonical node order, packed x, y, z (stride 3), or null when the layout is absent or holds another graph
    /// version. A layout that does not match the snapshot's node count, or holds a non-finite value, is refused. Negative zero is
    /// written as zero, so the bytes are canonical.
    /// </summary>
    private static float[]? CanonicalPositions(GraphSnapshot snapshot, List<GraphNode> nodes, GraphLayoutPositions? positions)
    {
        if (positions is null || positions.GraphVersion != snapshot.GraphVersion)
        {
            return null;
        }

        var source = positions.Xyz.Span;
        if (source.Length != 3 * snapshot.Nodes.Count)
        {
            throw new InvalidOperationException(
                "Layout for graph version " + snapshot.GraphVersion.ToString(CultureInfo.InvariantCulture) + " holds " +
                (source.Length / 3).ToString(CultureInfo.InvariantCulture) + " positions for " +
                snapshot.Nodes.Count.ToString(CultureInfo.InvariantCulture) + " nodes.");
        }

        var original = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < snapshot.Nodes.Count; i++)
        {
            original[snapshot.Nodes[i].Id] = i;
        }

        var result = new float[3 * nodes.Count];
        for (var k = 0; k < nodes.Count; k++)
        {
            var from = 3 * original[nodes[k].Id];
            for (var c = 0; c < 3; c++)
            {
                var value = source[from + c];
                if (!float.IsFinite(value))
                {
                    throw new InvalidOperationException("Layout position for node " + nodes[k].Id + " is not a finite number.");
                }

                result[3 * k + c] = value == 0f ? 0f : value;
            }
        }

        return result;
    }

    /// <summary>One line per node, in the same index order as <c>graph/nodes.jsonl</c>, so a reader joins the two by index or ref.</summary>
    private static async Task WriteLayoutAsync(IExportFileSink sink, List<string> files, List<GraphNode> nodes, float[] xyz, CancellationToken ct)
    {
        await using var stream = await sink.OpenAsync(LayoutPath, ct);
        files.Add(LayoutPath);
        var lines = new JsonLineWriter(stream);
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var index = i;
            await lines.WriteAsync(json =>
            {
                json.WriteStartObject();
                json.WriteNumber("index", index);
                json.WriteString("ref", node.Id);
                json.WriteNumber("x", xyz[3 * index]);
                json.WriteNumber("y", xyz[3 * index + 1]);
                json.WriteNumber("z", xyz[3 * index + 2]);
                json.WriteEndObject();
            }, ct);
        }
    }

    /// <summary>GraphML for external tools. Nodes get positional ids (n0, n1, ...); the source reference is kept as data.</summary>
    private static async Task WriteGraphMlAsync(
        IExportFileSink sink,
        List<string> files,
        long graphVersion,
        List<GraphNode> nodes,
        List<GraphEdge> edges,
        CancellationToken ct)
    {
        await using var stream = await sink.OpenAsync(GraphMlPath, ct);
        files.Add(GraphMlPath);

        var settings = new XmlWriterSettings
        {
            Encoding = ExportText.Utf8,
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            CloseOutput = false,
        };
        using (var xml = XmlWriter.Create(stream, settings))
        {
            xml.WriteStartElement("graphml", _graphMlNamespace);
            WriteKey(xml, "ref", "node", "ref");
            WriteKey(xml, "nodeKind", "node", "kind");
            WriteKey(xml, "edgeKind", "edge", "kind");
            WriteKey(xml, "derived", "graph", "derived", "boolean");

            xml.WriteStartElement("graph", _graphMlNamespace);
            xml.WriteAttributeString("id", "constellation");
            xml.WriteAttributeString("edgedefault", "directed");
            xml.WriteStartElement("desc", _graphMlNamespace);
            xml.WriteString(_derivedNote);
            xml.WriteEndElement();
            WriteData(xml, "derived", "true");
            xml.WriteComment(" graphVersion " + graphVersion.ToString(CultureInfo.InvariantCulture) + " ");

            var positions = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < nodes.Count; i++)
            {
                var id = "n" + i.ToString(CultureInfo.InvariantCulture);
                positions[nodes[i].Id] = id;
                xml.WriteStartElement("node", _graphMlNamespace);
                xml.WriteAttributeString("id", id);
                WriteData(xml, "ref", nodes[i].Id);
                WriteData(xml, "nodeKind", NodeKindText(nodes[i].Kind));
                xml.WriteEndElement();
            }

            foreach (var edge in edges)
            {
                xml.WriteStartElement("edge", _graphMlNamespace);
                xml.WriteAttributeString("source", positions[edge.From]);
                xml.WriteAttributeString("target", positions[edge.To]);
                WriteData(xml, "edgeKind", EdgeKindText(edge.Kind));
                xml.WriteEndElement();
            }

            xml.WriteEndElement();
            xml.WriteEndElement();
        }
    }

    private static void WriteKey(XmlWriter xml, string id, string target, string name, string type = "string")
    {
        xml.WriteStartElement("key", _graphMlNamespace);
        xml.WriteAttributeString("id", id);
        xml.WriteAttributeString("for", target);
        xml.WriteAttributeString("attr.name", name);
        xml.WriteAttributeString("attr.type", type);
        xml.WriteEndElement();
    }

    private static void WriteData(XmlWriter xml, string key, string value)
    {
        xml.WriteStartElement("data", _graphMlNamespace);
        xml.WriteAttributeString("key", key);
        xml.WriteString(value);
        xml.WriteEndElement();
    }

    private static string NodeKindText(GraphNodeKind kind) => kind switch
    {
        GraphNodeKind.Unspecified => "unspecified",
        GraphNodeKind.Objective => "objective",
        GraphNodeKind.Phase => "phase",
        GraphNodeKind.Vault => "vault",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string EdgeKindText(GraphEdgeKind kind) => kind switch
    {
        GraphEdgeKind.Unspecified => "unspecified",
        GraphEdgeKind.DependsOn => "depends_on",
        GraphEdgeKind.PhaseMembership => "phase_membership",
        GraphEdgeKind.WikiLink => "wiki_link",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
