using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.Tests.Wire.Sync;

public class GraphLodSubscriptionTests
{
    private static readonly Guid _graph = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly int[] _expectedTileNodes = [0, 2];

    private static GraphLodFrame SampleFrame() => new(
        string.Empty,
        7,
        false,
        [new GraphLodGlyph(0, [1.5f, -2f], 3)],
        [new GraphLodEdge(0, 1, 4)],
        [new GraphLodTile(0, 0, [0, 2])]);

    private static SubscriptionEngine EngineWith(SyncHarness h, IGraphLodSource? graphLod) =>
        new(h.Log, h.Log, h.Dispatcher, h.Options, null, graphLod);

    private static async Task<SubscribeResult> SubscribeGraphAsync(SyncHarness.Connection c, string stream)
    {
        var outcome = await c.Session.SubscribeAsync(
            new Subscribe([new StreamCursor(stream, 0, 0)]), CancellationToken.None);
        await outcome.StartAsync();
        await c.SettleAsync();
        return outcome.Result;
    }

    [Fact]
    public async Task Subscribing_to_a_readable_graph_sends_one_lod_snapshot_with_the_stream_name()
    {
        using var h = new SyncHarness();
        var source = new FakeGraphLodSource(_graph, SyncHarness.Alice, SampleFrame());
        using var c = new SyncHarness.Connection(EngineWith(h, source), SyncHarness.Alice, 50, paused: false);

        var stream = WireStreams.Graph(_graph);
        var result = await SubscribeGraphAsync(c, stream);

        Assert.Equal(SubscribeStatuses.Ok, Assert.Single(result.Results).Status);
        var frame = Assert.Single(c.Of(WireTypes.GraphLod));
        Assert.Equal(stream, frame.S);

        var payload = c.Payload<GraphLodFrame>(frame);
        Assert.Equal(stream, payload.S);
        Assert.Equal(7, payload.GraphVersion);
        Assert.False(payload.ClustersOnly);
        Assert.Equal(4, payload.ClusterEdges[0].Weight);
        Assert.Equal(_expectedTileNodes, payload.Tiles[0].NodeIndices);
        Assert.Equal(new[] { 1.5f, -2f }, payload.Clusters[0].Centroid);
    }

    [Fact]
    public async Task A_graph_the_principal_cannot_read_is_denied_without_a_frame()
    {
        using var h = new SyncHarness();
        var source = new FakeGraphLodSource(_graph, SyncHarness.Alice, SampleFrame());
        var stranger = Guid.Parse("00000000-0000-0000-0000-0000000000d9");
        using var c = new SyncHarness.Connection(EngineWith(h, source), stranger, 50, paused: false);

        var result = await SubscribeGraphAsync(c, WireStreams.Graph(_graph));

        Assert.Equal(SubscribeStatuses.Denied, Assert.Single(result.Results).Status);
        Assert.Empty(c.Of(WireTypes.GraphLod));
    }

    [Fact]
    public async Task An_unknown_graph_is_denied_the_same_way_as_an_unreadable_one()
    {
        using var h = new SyncHarness();
        var source = new FakeGraphLodSource(_graph, SyncHarness.Alice, SampleFrame());
        using var c = new SyncHarness.Connection(EngineWith(h, source), SyncHarness.Alice, 50, paused: false);

        var result = await SubscribeGraphAsync(c, WireStreams.Graph(Guid.NewGuid()));

        Assert.Equal(SubscribeStatuses.Denied, Assert.Single(result.Results).Status);
        Assert.Empty(c.Of(WireTypes.GraphLod));
    }

    [Fact]
    public async Task Graph_streams_are_denied_when_no_graph_source_is_configured()
    {
        using var h = new SyncHarness();
        using var c = new SyncHarness.Connection(EngineWith(h, null), SyncHarness.Alice, 50, paused: false);

        var result = await SubscribeGraphAsync(c, WireStreams.Graph(_graph));

        Assert.Equal(SubscribeStatuses.Denied, Assert.Single(result.Results).Status);
    }

    [Fact]
    public async Task Unknown_stream_prefixes_are_still_denied()
    {
        using var h = new SyncHarness();
        var source = new FakeGraphLodSource(_graph, SyncHarness.Alice, SampleFrame());
        using var c = new SyncHarness.Connection(EngineWith(h, source), SyncHarness.Alice, 50, paused: false);

        var result = await SubscribeGraphAsync(c, "graph:not-a-guid");

        Assert.Equal(SubscribeStatuses.Denied, Assert.Single(result.Results).Status);
    }

    [Fact]
    public async Task Resubscribing_to_a_graph_sends_a_fresh_snapshot()
    {
        using var h = new SyncHarness();
        var source = new FakeGraphLodSource(_graph, SyncHarness.Alice, SampleFrame());
        using var c = new SyncHarness.Connection(EngineWith(h, source), SyncHarness.Alice, 50, paused: false);

        var stream = WireStreams.Graph(_graph);
        await SubscribeGraphAsync(c, stream);
        await SubscribeGraphAsync(c, stream);

        Assert.Equal(2, c.Of(WireTypes.GraphLod).Count);
    }

    /// <summary>Returns the frame only to the principals it was given, and to nobody for any other graph.</summary>
    private sealed class FakeGraphLodSource(Guid graphId, Guid reader, GraphLodFrame frame) : IGraphLodSource
    {
        public ValueTask<GraphLodFrame?> GetAsync(Guid principalId, Guid requestedGraph, CancellationToken ct) =>
            ValueTask.FromResult(requestedGraph == graphId && principalId == reader ? frame : null);
    }
}
