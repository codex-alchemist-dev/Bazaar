using System.Text.Json;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Wire;

namespace Bazaar.Contracts.Tests.Graph;

public class GraphContractTests
{
    private static readonly float[] _fourFloats = [1f, 2f, 3f, 4f];
    private static readonly float[] _twoFloats = [0f, 0f];

    private static GraphSnapshot Snapshot() => new(
        42,
        [new GraphNode("doc-1", "plan", "Plan", "cluster-a"), new GraphNode("pr-7", "pr", "owner/repo#7", null)],
        [new GraphEdge("doc-1", "pr-7", "implements")]);

    [Fact]
    public void Snapshot_round_trips_through_the_wire_context()
    {
        var snapshot = Snapshot();
        string json = JsonSerializer.Serialize(snapshot, WireJsonContext.Default.GraphSnapshot);
        var back = JsonSerializer.Deserialize(json, WireJsonContext.Default.GraphSnapshot);

        Assert.Equivalent(snapshot, back, strict: true);
    }

    [Fact]
    public void Focus_request_round_trips_through_the_wire_context()
    {
        var request = new FocusRequest("view-1", "doc-1");
        string json = JsonSerializer.Serialize(request, WireJsonContext.Default.FocusRequest);
        var back = JsonSerializer.Deserialize(json, WireJsonContext.Default.FocusRequest);

        Assert.Equal(request, back);
        Assert.Contains("\"viewId\":\"view-1\"", json);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Layout_frame_round_trips_with_packed_floats(int dimensions)
    {
        float[] packed = dimensions == 2 ? [0f, 0f, 1.5f, -2.25f] : [0f, 0f, 0f, 1.5f, -2.25f, 0.125f];
        var frame = new LayoutFrame(42, 7, "doc-1", packed, dimensions);

        string json = JsonSerializer.Serialize(frame, WireJsonContext.Default.LayoutFrame);
        var back = JsonSerializer.Deserialize(json, WireJsonContext.Default.LayoutFrame);

        Assert.NotNull(back);
        Assert.Equal(frame.GraphVersion, back.GraphVersion);
        Assert.Equal(frame.LayoutSeq, back.LayoutSeq);
        Assert.Equal(frame.FocusId, back.FocusId);
        Assert.Equal(frame.Dimensions, back.Dimensions);
        Assert.Equal(packed, back.Xy.ToArray());
    }

    [Fact]
    public void Layout_frame_xy_is_a_plain_json_number_array()
    {
        var frame = new LayoutFrame(1, 1, "doc-1", _fourFloats);
        string json = JsonSerializer.Serialize(frame, WireJsonContext.Default.LayoutFrame);

        Assert.Contains("\"xy\":[1,2,3,4]", json);
    }

    [Fact]
    public void Layout_frame_defaults_to_two_dimensions()
    {
        var frame = new LayoutFrame(1, 1, "doc-1", _twoFloats);

        Assert.Equal(2, frame.Dimensions);
    }

    [Fact]
    public void Layout_frame_deserialises_without_dimensions_as_two_dimensional()
    {
        const string json = "{\"graphVersion\":3,\"layoutSeq\":4,\"focusId\":\"doc-1\",\"xy\":[0,0,5,5]}";
        var frame = JsonSerializer.Deserialize(json, WireJsonContext.Default.LayoutFrame);

        Assert.NotNull(frame);
        Assert.Equal(2, frame.Dimensions);
        Assert.Equal(4, frame.Xy.Length);
    }

    [Fact]
    public void Node_kinds_hold_every_doc_19_value()
    {
        string[] expected =
        [
            "index", "plan", "deep-dive", "research", "constitution", "decision", "archive", "memory", "summary", "twin",
            "charter", "draft", "pr", "task", "gc", "screenshot", "commit", "branch",
        ];

        Assert.All(expected, kind => Assert.Contains(kind, Known.NodeKinds));
        Assert.Equal(expected.Length, Known.NodeKinds.Count);
        Assert.DoesNotContain("person", Known.NodeKinds);
    }

    [Fact]
    public void Edge_kinds_hold_every_doc_19_value()
    {
        string[] expected =
        [
            "links_to", "backlink", "decides", "implements", "mentions", "derived_from", "supersedes", "contradicts", "parent", "merges",
        ];

        Assert.All(expected, kind => Assert.Contains(kind, Known.EdgeKinds));
        Assert.Equal(expected.Length, Known.EdgeKinds.Count);
        Assert.DoesNotContain("links", Known.EdgeKinds);
    }
}
