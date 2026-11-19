namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// The distinct styles of a <see cref="NodeStyleTable"/>, with one style index per node. Nodes that share a style
/// share an index, so a renderer can keep one entry per style rather than one per node. A palette is a snapshot:
/// later changes to the table need a new palette.
/// </summary>
public sealed class NodeStylePalette
{
    /// <summary>Largest number of distinct styles. Indices are stored as floats in <see cref="NodeInstanceBuffer"/>, exact up to 2^24.</summary>
    public const int MaxStyles = 1 << 24;

    private readonly NodeStyle[] _styles;
    private readonly int[] _indices;

    private NodeStylePalette(NodeStyle[] styles, int[] indices)
    {
        _styles = styles;
        _indices = indices;
    }

    /// <summary>Number of nodes, one per node of the source table.</summary>
    public int NodeCount => _indices.Length;

    /// <summary>Number of distinct styles.</summary>
    public int StyleCount => _styles.Length;

    /// <summary>Builds the palette from a table. Equal styles get the same index, in order of first use.</summary>
    /// <exception cref="InvalidOperationException">The table has more than <see cref="MaxStyles"/> distinct styles.</exception>
    public static NodeStylePalette Build(NodeStyleTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var styles = new List<NodeStyle>();
        var lookup = new Dictionary<NodeStyle, int>();
        var indices = new int[table.NodeCount];
        for (int node = 0; node < indices.Length; node++)
        {
            NodeStyle style = table.Get(node);
            if (!lookup.TryGetValue(style, out int index))
            {
                if (styles.Count == MaxStyles)
                {
                    throw new InvalidOperationException($"A palette holds at most {MaxStyles} distinct styles.");
                }

                index = styles.Count;
                styles.Add(style);
                lookup.Add(style, index);
            }

            indices[node] = index;
        }

        return new NodeStylePalette(styles.ToArray(), indices);
    }

    /// <summary>Returns the style at one index.</summary>
    public NodeStyle GetStyle(int index)
    {
        if ((uint)index >= (uint)_styles.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Style index is outside the palette.");
        }

        return _styles[index];
    }

    /// <summary>Returns the style index of one node.</summary>
    public int IndexOf(int node)
    {
        if ((uint)node >= (uint)_indices.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(node), node, "Node index is outside the palette.");
        }

        return _indices[node];
    }
}
