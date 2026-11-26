namespace Bazaar.Server.GraphRetrieval;

/// <summary>
/// An immutable set of node indices a query may touch (the visibility mask of doc 64 section 10, or a subgraph restriction).
/// A node outside the mask is never a seed, a hop target, a closure step or a walk transition.
/// </summary>
public sealed class NodeMask
{
    private readonly ulong[] _bits;

    private NodeMask(int length, ulong[] bits)
    {
        Length = length;
        _bits = bits;
    }

    /// <summary>The number of nodes the mask covers; it must equal the snapshot's node count.</summary>
    public int Length { get; }

    /// <summary>A mask that allows every node.</summary>
    public static NodeMask All(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var bits = new ulong[(length + 63) / 64];
        Array.Fill(bits, ulong.MaxValue);
        return new NodeMask(length, bits);
    }

    /// <summary>A mask that allows exactly the listed nodes.</summary>
    public static NodeMask FromNodes(int length, IEnumerable<int> nodes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentNullException.ThrowIfNull(nodes);
        var bits = new ulong[(length + 63) / 64];
        foreach (int n in nodes)
        {
            if ((uint)n >= (uint)length)
            {
                throw new ArgumentOutOfRangeException(nameof(nodes), "Node index outside the mask.");
            }

            bits[n >> 6] |= 1UL << (n & 63);
        }

        return new NodeMask(length, bits);
    }

    /// <summary>A mask that allows every node except the listed ones.</summary>
    public static NodeMask AllExcept(int length, IEnumerable<int> hidden)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        NodeMask all = All(length);
        foreach (int n in hidden)
        {
            if ((uint)n >= (uint)length)
            {
                throw new ArgumentOutOfRangeException(nameof(hidden), "Node index outside the mask.");
            }

            all._bits[n >> 6] &= ~(1UL << (n & 63));
        }

        return all;
    }

    /// <summary>The intersection of two masks of the same length.</summary>
    public NodeMask And(NodeMask other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Length != Length)
        {
            throw new ArgumentException("Masks must have the same length.", nameof(other));
        }

        var bits = new ulong[_bits.Length];
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = _bits[i] & other._bits[i];
        }

        return new NodeMask(Length, bits);
    }

    public bool Contains(int node) => (uint)node < (uint)Length && (_bits[node >> 6] & (1UL << (node & 63))) != 0;
}
