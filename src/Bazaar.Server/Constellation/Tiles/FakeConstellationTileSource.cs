using Bazaar.Contracts.Graph;

namespace Bazaar.Server.Constellation.Tiles;

/// <summary>
/// An in-memory <see cref="IConstellationTileSource"/> over a fixed node list. It returns the nodes inside the request box, in
/// the order given, up to <see cref="ViewportRequest.MaxNodes"/>. It reports its own graph version, so the handler can detect
/// a stale request. It never returns clusters. Pure: no store, no I/O.
/// </summary>
public sealed class FakeConstellationTileSource(long graphVersion, IReadOnlyList<FakeTileNode> nodes) : IConstellationTileSource
{
    private readonly long _graphVersion = graphVersion;
    private readonly IReadOnlyList<FakeTileNode> _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));

    public Task<TileFrame?> GetFrameAsync(ViewportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var inside = new List<FakeTileNode>();
        foreach (var node in _nodes)
        {
            if (inside.Count >= request.MaxNodes)
                break;
            if (node.X < request.MinX || node.X > request.MaxX
                || node.Y < request.MinY || node.Y > request.MaxY
                || node.Z < request.MinZ || node.Z > request.MaxZ)
                continue;
            inside.Add(node);
        }

        var xyz = new float[inside.Count * 3];
        for (var i = 0; i < inside.Count; i++)
        {
            xyz[3 * i] = inside[i].X;
            xyz[3 * i + 1] = inside[i].Y;
            xyz[3 * i + 2] = inside[i].Z;
        }

        var frame = new TileFrame(
            request.ViewId,
            _graphVersion,
            layoutSeq: 1,
            new TileKey(0, 0, 0),
            request.Tier,
            dimensions: 2,
            inside.Select(n => n.Id).ToArray(),
            xyz,
            Array.Empty<ClusterGlyph>());

        return Task.FromResult<TileFrame?>(frame);
    }
}

/// <summary>A node with a position in layout space, for <see cref="FakeConstellationTileSource"/>.</summary>
public sealed record FakeTileNode(string Id, float X, float Y, float Z);
