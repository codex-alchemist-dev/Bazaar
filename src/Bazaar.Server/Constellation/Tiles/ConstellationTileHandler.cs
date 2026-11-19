using System.Text;
using Bazaar.Contracts.Graph;

namespace Bazaar.Server.Constellation.Tiles;

/// <summary>An HTTP-shaped result for one tile request: status, content type and body.</summary>
public sealed record TileResponse(int StatusCode, string ContentType, byte[] Body);

/// <summary>
/// Handles one viewport request for the Constellation tile endpoint (doc 19). It clamps the node cap to the operator's
/// <c>constellation.node_cap</c>, asks the tile source for the frame, and refuses a frame from another graph version. The
/// body is the binary frame from <see cref="TileFrameBinaryCodec"/>. Host routing is wired elsewhere; this stays pure.
/// </summary>
public sealed class ConstellationTileHandler(IConstellationTileSource source, int nodeCap)
{
    /// <summary>The content type of a binary tile body.</summary>
    public const string BinaryContentType = "application/x-bazaar-tile";

    private readonly IConstellationTileSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly int _nodeCap = nodeCap > 0
        ? nodeCap
        : throw new ArgumentOutOfRangeException(nameof(nodeCap), nodeCap, "The node cap must be positive.");

    /// <summary>The owner's tile. Serve anyone else through the overload that takes a viewer.</summary>
    public Task<TileResponse> HandleAsync(ViewportRequest request, CancellationToken cancellationToken = default) =>
        HandleAsync(request, GraphViewer.Owner, cancellationToken);

    public async Task<TileResponse> HandleAsync(ViewportRequest request, GraphViewer viewer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(viewer);

        // The operator's cap wins over a larger request, so a client cannot ask for more nodes than the server allows.
        var capped = request with { MaxNodes = Math.Min(request.MaxNodes, _nodeCap) };

        TileFrame? frame = await _source.GetFrameAsync(capped, viewer, cancellationToken).ConfigureAwait(false);
        if (frame is null)
            return Text(404, "No tile for this view.");

        if (frame.GraphVersion != request.GraphVersion)
            return Text(409, "The graph changed since this view was requested. Request the viewport again.");

        if (frame.NodeIds.Count > capped.MaxNodes)
            throw new InvalidOperationException("The tile source returned more nodes than the request allowed.");

        return new TileResponse(200, BinaryContentType, TileFrameBinaryCodec.Encode(frame));
    }

    private static TileResponse Text(int statusCode, string message) =>
        new(statusCode, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(message));
}
