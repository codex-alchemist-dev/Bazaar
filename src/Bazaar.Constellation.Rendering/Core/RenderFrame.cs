namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// One layout result: packed positions for every node, in scene index order.
/// Dimensions is 2 for the current view and 3 when a 3D layout is served (doc 19, octree-ready).
/// </summary>
public sealed class RenderFrame
{
    public RenderFrame(long graphVersion, long layoutSeq, int dimensions, ReadOnlyMemory<float> positions)
    {
        if (dimensions is not (2 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be 2 or 3.");
        }

        if (positions.Length % dimensions != 0)
        {
            throw new ArgumentException("Positions must hold a whole number of nodes.", nameof(positions));
        }

        foreach (float value in positions.Span)
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentException("Positions must be finite.", nameof(positions));
            }
        }

        GraphVersion = graphVersion;
        LayoutSeq = layoutSeq;
        Dimensions = dimensions;
        Positions = positions;
    }

    public long GraphVersion { get; }

    public long LayoutSeq { get; }

    public int Dimensions { get; }

    public ReadOnlyMemory<float> Positions { get; }

    public int NodeCount => Positions.Length / Dimensions;
}
