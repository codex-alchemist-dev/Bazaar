using System.Text.Json;
using System.Xml.Linq;
using Bazaar.Contracts.Graph;

namespace Bazaar.Server.Portability.Tests;

public class GraphExporterTests
{
    private static readonly XNamespace _ns = "http://graphml.graphdrawing.org/xmlns";

    private static GraphSnapshot Sample(bool reverse = false)
    {
        var nodes = new List<GraphNode>
        {
            new("vault/zeta.md", GraphNodeKind.Vault),
            new("objective/alpha", GraphNodeKind.Objective),
            new("vault/beta.md", GraphNodeKind.Vault),
            new("phase/one", GraphNodeKind.Phase),
        };
        var edges = new List<GraphEdge>
        {
            new("vault/beta.md", "vault/zeta.md", GraphEdgeKind.WikiLink),
            new("objective/alpha", "phase/one", GraphEdgeKind.PhaseMembership),
            new("objective/alpha", "objective/alpha-2", GraphEdgeKind.DependsOn),
            new("vault/beta.md", "vault/zeta.md", GraphEdgeKind.DependsOn),
        };
        if (reverse)
        {
            nodes.Reverse();
            edges.Reverse();
        }

        return new GraphSnapshot(42, nodes, edges);
    }

    private static async Task<(GraphExportResult Result, RecordingExportSink Sink, FakeGraphSource Source)> RunAsync(
        GraphSnapshot snapshot,
        bool includeDerived = true)
    {
        var source = new FakeGraphSource(snapshot);
        var sink = new RecordingExportSink();
        var result = await new GraphExporter(source, new PortabilityExportOptions { IncludeDerived = includeDerived })
            .ExportAsync(sink, CancellationToken.None);
        return (result, sink, source);
    }

    [Fact]
    public async Task Derived_export_is_off_by_default_and_the_source_is_not_read()
    {
        var source = new FakeGraphSource(Sample());
        var sink = new RecordingExportSink();

        var result = await new GraphExporter(source, new PortabilityExportOptions()).ExportAsync(sink, CancellationToken.None);

        Assert.True(result.Skipped);
        Assert.Empty(result.Files);
        Assert.Equal(0, result.NodeCount);
        Assert.Equal(0, source.Reads);
        Assert.Empty(sink.Paths);
    }

    [Fact]
    public async Task Derived_marker_says_derived_and_not_authoritative()
    {
        var (result, sink, _) = await RunAsync(Sample());

        Assert.False(result.Skipped);
        Assert.Equal(
            [GraphExporter.DerivedPath, GraphExporter.EdgesPath, GraphExporter.GraphMlPath, GraphExporter.NodesPath],
            sink.Paths);
        Assert.Equal(sink.Paths, result.Files.Order(StringComparer.Ordinal).ToList());

        var marker = JsonElement.Parse(sink.Text(GraphExporter.DerivedPath));
        Assert.Equal("1.0.0", marker.GetProperty("schemaVersion").GetString());
        Assert.True(marker.GetProperty("derived").GetBoolean());
        Assert.False(marker.GetProperty("authoritative").GetBoolean());
        Assert.Equal(42, marker.GetProperty("graphVersion").GetInt64());
        Assert.Equal(4, marker.GetProperty("nodeCount").GetInt32());
        Assert.Equal(3, marker.GetProperty("edgeCount").GetInt32());
        Assert.Equal(1, marker.GetProperty("droppedEdgeCount").GetInt32());
        Assert.Contains("never imported as authoritative", marker.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nodes_are_in_canonical_order_with_positional_indexes()
    {
        var (_, sink, _) = await RunAsync(Sample(reverse: true));

        var nodes = sink.Lines(GraphExporter.NodesPath).Select(l => JsonElement.Parse(l)).ToList();
        Assert.Equal(
            ["objective/alpha", "phase/one", "vault/beta.md", "vault/zeta.md"],
            nodes.Select(n => n.GetProperty("ref").GetString()!).ToArray());
        Assert.Equal([0, 1, 2, 3], nodes.Select(n => n.GetProperty("index").GetInt32()).ToArray());
    }

    [Theory]
    [InlineData(GraphNodeKind.Unspecified, "unspecified")]
    [InlineData(GraphNodeKind.Objective, "objective")]
    [InlineData(GraphNodeKind.Phase, "phase")]
    [InlineData(GraphNodeKind.Vault, "vault")]
    public async Task Node_kinds_have_stable_names(GraphNodeKind kind, string name)
    {
        var snapshot = new GraphSnapshot(1, [new GraphNode("n", kind)], []);

        var (_, sink, _) = await RunAsync(snapshot);

        var node = JsonElement.Parse(Assert.Single(sink.Lines(GraphExporter.NodesPath)));
        Assert.Equal(name, node.GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData(GraphEdgeKind.Unspecified, "unspecified")]
    [InlineData(GraphEdgeKind.DependsOn, "depends_on")]
    [InlineData(GraphEdgeKind.PhaseMembership, "phase_membership")]
    [InlineData(GraphEdgeKind.WikiLink, "wiki_link")]
    public async Task Edge_kinds_have_stable_names(GraphEdgeKind kind, string name)
    {
        var snapshot = new GraphSnapshot(1, [new GraphNode("a"), new GraphNode("b")], [new GraphEdge("a", "b", kind)]);

        var (_, sink, _) = await RunAsync(snapshot);

        var edge = JsonElement.Parse(Assert.Single(sink.Lines(GraphExporter.EdgesPath)));
        Assert.Equal(name, edge.GetProperty("kind").GetString());
        Assert.Equal("a", edge.GetProperty("from").GetString());
        Assert.Equal("b", edge.GetProperty("to").GetString());
    }

    [Fact]
    public async Task Edges_with_a_missing_endpoint_are_dropped_and_counted()
    {
        var snapshot = new GraphSnapshot(
            3,
            [new GraphNode("a"), new GraphNode("b")],
            [new GraphEdge("a", "b", GraphEdgeKind.WikiLink), new GraphEdge("a", "ghost", GraphEdgeKind.WikiLink), new GraphEdge("ghost", "b")]);

        var (result, sink, _) = await RunAsync(snapshot);

        Assert.Equal(1, result.EdgeCount);
        Assert.Equal(2, result.DroppedEdgeCount);
        Assert.Single(sink.Lines(GraphExporter.EdgesPath));
    }

    [Fact]
    public async Task Graphml_is_well_formed_marked_derived_and_keeps_the_kinds()
    {
        var (_, sink, _) = await RunAsync(Sample());

        var doc = XDocument.Parse(sink.Text(GraphExporter.GraphMlPath));
        var root = doc.Root!;
        Assert.Equal(_ns + "graphml", root.Name);

        var graph = root.Element(_ns + "graph")!;
        Assert.Equal("directed", graph.Attribute("edgedefault")!.Value);
        var derived = graph.Elements(_ns + "data").Single(d => d.Attribute("key")!.Value == "derived");
        Assert.Equal("true", derived.Value);

        var nodes = graph.Elements(_ns + "node").ToList();
        Assert.Equal(4, nodes.Count);
        Assert.Equal("n0", nodes[0].Attribute("id")!.Value);
        Assert.Equal("objective/alpha", nodes[0].Elements(_ns + "data").Single(d => d.Attribute("key")!.Value == "ref").Value);
        Assert.Equal("objective", nodes[0].Elements(_ns + "data").Single(d => d.Attribute("key")!.Value == "nodeKind").Value);

        var edges = graph.Elements(_ns + "edge").ToList();
        Assert.Equal(3, edges.Count);
        Assert.Equal("n0", edges[0].Attribute("source")!.Value);
        Assert.Equal("n1", edges[0].Attribute("target")!.Value);
        // Edges sort by source, then target, then kind: objective/alpha comes before vault/beta.md.
        Assert.Equal(
            ["phase_membership", "depends_on", "wiki_link"],
            edges.Select(e => e.Elements(_ns + "data").Single(d => d.Attribute("key")!.Value == "edgeKind").Value).ToArray());
    }

    [Fact]
    public async Task Output_bytes_are_identical_however_the_snapshot_orders_its_nodes_and_edges()
    {
        var (_, forward, _) = await RunAsync(Sample(reverse: false));
        var (_, reversed, _) = await RunAsync(Sample(reverse: true));

        Assert.Equal(forward.Paths, reversed.Paths);
        foreach (var path in forward.Paths)
        {
            Assert.True(forward.Bytes(path).AsSpan().SequenceEqual(reversed.Bytes(path)), "bytes differ for " + path);
        }
    }

    [Fact]
    public async Task Duplicate_node_ids_are_refused()
    {
        var snapshot = new GraphSnapshot(1, [new GraphNode("same"), new GraphNode("same")], []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(snapshot));
    }

    private sealed class FakeGraphSource(GraphSnapshot snapshot) : IGraphExportSource
    {
        public int Reads { get; private set; }

        public Task<GraphSnapshot> GetSnapshotAsync(CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(snapshot);
        }
    }
}
