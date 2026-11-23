using Bazaar.Client.Constellation.Abstractions;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Constellation.Tests;

/// <summary>A tile source that records each request and answers with the function the test supplies.</summary>
public sealed class DrawerTileSource(Func<ViewportRequest, Task<TileFrame?>> answer) : IConstellationTileSource
{
    private readonly List<ViewportRequest> _requests = [];
    private readonly object _gate = new();

    public IReadOnlyList<ViewportRequest> Requests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    public Task<TileFrame?> GetFrameAsync(ViewportRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }

        return answer(request);
    }

    /// <summary>A tile source that answers with one frame for every request.</summary>
    public static DrawerTileSource Answering(TileFrame? frame) => new(_ => Task.FromResult(frame));
}

public static class DrawerTestData
{
    public static readonly ConstellationBox Box = new(0, 0, 100, 100);

    /// <summary>Refreshes with the test's cancellation token, so a hung test can be cancelled.</summary>
    public static Task Refresh(this ConstellationDrawerViewModel model, ConstellationBox box, DrawerGraphState? graph) =>
        model.RefreshAsync(box, graph, TestContext.Current.CancellationToken);

    public static readonly DrawerGraphState Graph = new(GraphVersion: 7, ClustersOnly: false);

    /// <summary>A tile frame for one view with the given node ids, placed at (i, 10 + i) in graph coordinates.</summary>
    public static TileFrame Frame(string[] ids, long graphVersion = 7, long layoutSeq = 1, string viewId = PaneKinds.Constellation)
    {
        var xyz = new float[ids.Length * 3];
        for (int i = 0; i < ids.Length; i++)
        {
            xyz[i * 3] = i;
            xyz[(i * 3) + 1] = 10 + i;
            xyz[(i * 3) + 2] = 0;
        }

        return new TileFrame(
            viewId,
            graphVersion,
            layoutSeq,
            new TileKey(0, 0, 0),
            Bazaar.Contracts.Graph.ZoomTier.Near,
            2,
            ids,
            xyz,
            []);
    }
}
