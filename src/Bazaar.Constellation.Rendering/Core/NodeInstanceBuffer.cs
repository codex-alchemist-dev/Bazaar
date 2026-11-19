using System.Runtime.InteropServices;

namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// Packs node positions and styles into one flat instance array for a GPU upload. Storage is reused between
/// fills and only grows.
/// </summary>
public sealed class NodeInstanceBuffer
{
    /// <summary>Floats per instance, in order: x, y, radius, red, green, blue, alpha. z is never stored.</summary>
    public const int FloatsPerInstance = 7;

    private float[] _floats = [];
    private int _instanceCount;

    /// <summary>Number of nodes packed by the last <see cref="Fill"/>; 0 before the first fill or after <see cref="Clear"/>.</summary>
    public int InstanceCount => _instanceCount;

    /// <summary>The packed instances: exactly <see cref="InstanceCount"/> times <see cref="FloatsPerInstance"/> floats.</summary>
    public ReadOnlySpan<float> Floats => _floats.AsSpan(0, _instanceCount * FloatsPerInstance);

    /// <summary>The packed instances as raw bytes, for a vertex buffer upload.</summary>
    public ReadOnlySpan<byte> Bytes => MemoryMarshal.AsBytes(Floats);

    /// <summary>
    /// Packs one instance per node. Only x and y are read from <paramref name="positions"/> (z is ignored for
    /// 3D input). Nothing changes when the arguments are rejected.
    /// </summary>
    public void Fill(ReadOnlySpan<float> positions, int dimensions, NodeStyleTable styles)
    {
        ArgumentNullException.ThrowIfNull(styles);
        if (dimensions is not (2 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be 2 or 3.");
        }

        int nodeCount = styles.NodeCount;
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
            NodeStyle style = styles.Get(node);
            _floats[target] = positions[source];
            _floats[target + 1] = positions[source + 1];
            _floats[target + 2] = style.Radius;
            _floats[target + 3] = style.Red;
            _floats[target + 4] = style.Green;
            _floats[target + 5] = style.Blue;
            _floats[target + 6] = style.Alpha;
        }

        _instanceCount = nodeCount;
    }

    /// <summary>Forgets the packed instances. The storage is kept for reuse.</summary>
    public void Clear() => _instanceCount = 0;
}
