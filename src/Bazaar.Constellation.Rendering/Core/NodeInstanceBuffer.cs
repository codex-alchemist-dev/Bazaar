using System.Runtime.InteropServices;

namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// Packs node positions and style indices into one flat instance array for a GPU upload. The colour, radius and
/// ring width of each style live in <see cref="NodeStylePalette"/>, not here. Storage is reused between fills and
/// only grows.
/// </summary>
public sealed class NodeInstanceBuffer
{
    /// <summary>
    /// Floats per instance, in order: x, y, style index. z is never stored. The style index is an exact integer
    /// stored as a float; it is below <see cref="NodeStylePalette.MaxStyles"/>, so the conversion is exact.
    /// </summary>
    public const int FloatsPerInstance = 3;

    private float[] _floats = [];
    private int _instanceCount;

    /// <summary>Number of nodes packed by the last <see cref="Fill"/>; 0 before the first fill or after <see cref="Clear"/>.</summary>
    public int InstanceCount => _instanceCount;

    /// <summary>The packed instances: exactly <see cref="InstanceCount"/> times <see cref="FloatsPerInstance"/> floats.</summary>
    public ReadOnlySpan<float> Floats => _floats.AsSpan(0, _instanceCount * FloatsPerInstance);

    /// <summary>The packed instances as raw bytes, for a vertex buffer upload.</summary>
    public ReadOnlySpan<byte> Bytes => MemoryMarshal.AsBytes(Floats);

    /// <summary>
    /// Packs one instance per node: its x and y, and the index of its style in <paramref name="palette"/>.
    /// Only x and y are read from <paramref name="positions"/> (z is ignored for 3D input). Nothing changes when the
    /// arguments are rejected.
    /// </summary>
    public void Fill(ReadOnlySpan<float> positions, int dimensions, NodeStylePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (dimensions is not (2 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be 2 or 3.");
        }

        int nodeCount = palette.NodeCount;
        if (positions.Length != nodeCount * dimensions)
        {
            throw new ArgumentException("Positions must hold NodeCount times dimensions values.", nameof(positions));
        }

        int required = nodeCount * FloatsPerInstance;
        if (_floats.Length < required)
        {
            _floats = new float[required];
        }

        for (int node = 0; node < nodeCount; node++)
        {
            int source = node * dimensions;
            int target = node * FloatsPerInstance;
            _floats[target] = positions[source];
            _floats[target + 1] = positions[source + 1];
            _floats[target + 2] = palette.IndexOf(node);
        }

        _instanceCount = nodeCount;
    }

    /// <summary>Forgets the packed instances. The storage is kept for reuse.</summary>
    public void Clear() => _instanceCount = 0;
}
