namespace Bazaar.Constellation.Rendering.Core;

/// <summary>Per-node styles. Every node starts with the fallback style.</summary>
public sealed class NodeStyleTable
{
    private readonly NodeStyle[] _styles;

    public NodeStyleTable(int nodeCount, NodeStyle fallback)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nodeCount);
        _styles = new NodeStyle[nodeCount];
        Array.Fill(_styles, fallback);
    }

    public int NodeCount => _styles.Length;

    /// <summary>Replaces the style of one node.</summary>
    public void Set(int node, NodeStyle style)
    {
        ValidateNode(node);
        _styles[node] = style;
    }

    /// <summary>Returns the style of one node.</summary>
    public NodeStyle Get(int node)
    {
        ValidateNode(node);
        return _styles[node];
    }

    private void ValidateNode(int node)
    {
        if ((uint)node >= (uint)_styles.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(node), node, "Node index is outside the table.");
        }
    }
}
