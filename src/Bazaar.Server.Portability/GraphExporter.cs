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
/// <c>graph/nodes.jsonl</c>, <c>graph/edges.jsonl</c> and <c>graph/graph.graphml</c>. Node and edge order is canonical (ordinal
/// by reference, then by kind), so the bytes do not depend on the snapshot's order. An edge whose endpoint is not a node of the
/// snapshot is dropped and counted. Layout positions are not exported: doc 44 section 7 excludes them as recomputed data.
/// </remarks>
public sealed class GraphExporter(IGraphExportSource source, PortabilityExportOptions options)
{
    /// <summary>The schema version of <c>graph/derived.json</c> (doc 44 section 4.1).</summary>
    public const string SchemaVersion = "1.0.0";

    public const string DerivedPath = "graph/derived.json";
    public const string NodesPath = "graph/nodes.jsonl";
    public const string EdgesPath = "graph/edges.jsonl";
    public const string GraphMlPath = "graph/graph.graphml";

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

        var files = new List<string>();
        await WriteDerivedMarkerAsync(sink, files, snapshot.GraphVersion, nodes.Count, edges.Count, dropped, ct);
        await WriteNodesAsync(sink, files, nodes, ct);
        await WriteEdgesAsync(sink, files, edges, ct);
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
