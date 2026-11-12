namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// One layout result: packed positions for every node, in scene index order.
/// Dimensions is 2 for the current view and 3 when a 3D layout is served.
/// Frames are ordered by (Epoch, Sequence): Epoch changes with the graph, Sequence with each layout inside it.
/// </summary>
public sealed class RenderFrame
{
    public RenderFrame(long epoch, long sequence, int dimensions, ReadOnlyMemory<float> positions)
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

        Epoch = epoch;
        Sequence = sequence;
        Dimensions = dimensions;
        Positions = positions;
    }

    /// <summary>Outer ordering key: the epoch the frame belongs to.</summary>
    public long Epoch { get; }

    /// <summary>Inner ordering key: the position of the frame within its epoch.</summary>
    public long Sequence { get; }

    public int Dimensions { get; }

    public ReadOnlyMemory<float> Positions { get; }

    public int NodeCount => Positions.Length / Dimensions;
}
