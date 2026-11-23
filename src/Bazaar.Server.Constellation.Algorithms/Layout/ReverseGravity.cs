namespace Bazaar.Server.Constellation.Layout;

/// <summary>
/// Pure reverse-gravity math for the Constellation (docs/design/19, Reverse gravity). On a focus change the focused node
/// sits at the origin, every other node gets a target ring by hop distance, and new or unplaced nodes are warm-started
/// at a neighbour's position so the change animates. No I/O, no randomness: the same input gives the same output.
/// Node indices are plain ints so this does not depend on Contracts.
/// </summary>
public static class ReverseGravity
{
    /// <summary>Hop value reported for nodes that cannot reach the focus.</summary>
    public const int Unreachable = -1;

    /// <summary>Lowest allowed <c>constellation.ring_depth</c>.</summary>
    public const int MinRingDepth = 1;

    /// <summary>Highest allowed <c>constellation.ring_depth</c>.</summary>
    public const int MaxRingDepth = 6;

    /// <summary>
    /// Hop distance from <paramref name="focus"/> to every node over an undirected graph, by breadth-first search.
    /// Returns <see cref="Unreachable"/> (-1) for nodes in other components. The focus gets 0.
    /// </summary>
    public static int[] HopDistances(int nodeCount, ReadOnlySpan<(int From, int To)> edges, int focus)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nodeCount);
        ValidateNode(focus, nodeCount, nameof(focus));

        var (offsets, neighbours) = BuildAdjacency(nodeCount, edges);

        var hops = new int[nodeCount];
        Array.Fill(hops, Unreachable);
        var queue = new int[nodeCount];
        var head = 0;
        var tail = 0;

        hops[focus] = 0;
        queue[tail++] = focus;
        while (head < tail)
        {
            var node = queue[head++];
            for (var k = offsets[node]; k < offsets[node + 1]; k++)
            {
                var next = neighbours[k];
                if (hops[next] == Unreachable)
                {
                    hops[next] = hops[node] + 1;
                    queue[tail++] = next;
                }
            }
        }

        return hops;
    }

    /// <summary>
    /// Target ring radius for each node from its hop value. Hop 0 (the focus) is radius 0. Hops beyond
    /// <paramref name="ringDepth"/> clamp to the outermost ring. Unreachable nodes (-1) go one ring beyond it.
    /// </summary>
    public static float[] RingTargetRadii(ReadOnlySpan<int> hops, int ringDepth, float baseRadius)
    {
        ValidateRingParameters(ringDepth, baseRadius);

        var radii = new float[hops.Length];
        for (var i = 0; i < hops.Length; i++)
        {
            var hop = hops[i];
            if (hop < Unreachable)
            {
                throw new ArgumentOutOfRangeException(nameof(hops), hop, "A hop value must be -1 (unreachable) or 0 or more.");
            }

            radii[i] = RingRadius(hop, ringDepth, baseRadius);
        }

        return radii;
    }

    /// <summary>
    /// Radius of one hop ring: <c>baseRadius * min(hop, ringDepth)</c>, or <c>baseRadius * (ringDepth + 1)</c> for an
    /// unreachable node (<see cref="Unreachable"/>). Monotonic in hop.
    /// </summary>
    public static float RingRadius(int hop, int ringDepth, float baseRadius)
    {
        ValidateRingParameters(ringDepth, baseRadius);
        ArgumentOutOfRangeException.ThrowIfLessThan(hop, Unreachable);

        if (hop == Unreachable)
        {
            return baseRadius * (ringDepth + 1);
        }

        return baseRadius * Math.Min(hop, ringDepth);
    }

    /// <summary>
    /// Warm start, in place. <paramref name="positions"/> is packed with a stride of <paramref name="dimensions"/>
    /// (x,y or x,y,z per node). A node whose coordinates are all NaN has no previous position. Placed nodes are
    /// never moved. Unplaced nodes are visited in index order and take the mean of their already-placed neighbours
    /// (including ones seeded earlier in this pass). With no placed neighbour they take the focus position.
    /// An unplaced focus is seeded at the origin first. Placed coordinates must be finite.
    /// </summary>
    public static void WarmStart(
        Span<float> positions,
        ReadOnlySpan<(int From, int To)> edges,
        int focus,
        int dimensions = 2)
    {
        if (dimensions is not (2 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be 2 or 3.");
        }

        if (positions.Length % dimensions != 0)
        {
            throw new ArgumentException($"Positions length must be a multiple of {dimensions}.", nameof(positions));
        }

        var nodeCount = positions.Length / dimensions;
        ValidateNode(focus, nodeCount, nameof(focus));
        ValidatePositions(positions, dimensions);

        var (offsets, neighbours) = BuildAdjacency(nodeCount, edges);

        if (!IsPlaced(positions, focus, dimensions))
        {
            positions.Slice(focus * dimensions, dimensions).Clear();
        }

        var focusBase = focus * dimensions;
        Span<double> sums = stackalloc double[3];
        sums = sums[..dimensions];
        for (var node = 0; node < nodeCount; node++)
        {
            if (IsPlaced(positions, node, dimensions))
            {
                continue;
            }

            var nodeBase = node * dimensions;
            sums.Clear();
            var count = 0;
            for (var k = offsets[node]; k < offsets[node + 1]; k++)
            {
                var neighbour = neighbours[k];
                if (!IsPlaced(positions, neighbour, dimensions))
                {
                    continue;
                }

                var neighbourBase = neighbour * dimensions;
                for (var d = 0; d < dimensions; d++)
                {
                    sums[d] += positions[neighbourBase + d];
                }

                count++;
            }

            for (var d = 0; d < dimensions; d++)
            {
                positions[nodeBase + d] = count > 0
                    ? (float)(sums[d] / count)
                    : positions[focusBase + d];
            }
        }
    }

    private static void ValidateRingParameters(int ringDepth, float baseRadius)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ringDepth, MinRingDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ringDepth, MaxRingDepth);

        if (!(baseRadius > 0f) || !float.IsFinite(baseRadius * (ringDepth + 1)))
        {
            throw new ArgumentOutOfRangeException(nameof(baseRadius), baseRadius, "Base radius must be finite and greater than zero.");
        }
    }

    private static void ValidateNode(int index, int nodeCount, string paramName)
    {
        if ((uint)index >= (uint)nodeCount)
        {
            throw new ArgumentOutOfRangeException(paramName, index, $"Node index must be in 0 to {nodeCount - 1}.");
        }
    }

    private static void ValidatePositions(ReadOnlySpan<float> positions, int dimensions)
    {
        for (var node = 0; node < positions.Length / dimensions; node++)
        {
            var nodeBase = node * dimensions;
            var unplaced = float.IsNaN(positions[nodeBase]);
            for (var d = 0; d < dimensions; d++)
            {
                var value = positions[nodeBase + d];
                if (float.IsNaN(value) != unplaced)
                {
                    throw new ArgumentException($"Node {node} is partly placed: either all of its coordinates are NaN or none are.", nameof(positions));
                }

                if (!unplaced && !float.IsFinite(value))
                {
                    throw new ArgumentException($"Node {node} has a non-finite coordinate.", nameof(positions));
                }
            }
        }
    }

    private static bool IsPlaced(ReadOnlySpan<float> positions, int node, int dimensions) =>
        !float.IsNaN(positions[node * dimensions]);

    /// <summary>Undirected compressed adjacency: neighbours of node i are neighbours[offsets[i]..offsets[i+1]).</summary>
    private static (int[] Offsets, int[] Neighbours) BuildAdjacency(int nodeCount, ReadOnlySpan<(int From, int To)> edges)
    {
        var offsets = new int[nodeCount + 1];
        foreach (var (from, to) in edges)
        {
            ValidateNode(from, nodeCount, nameof(edges));
            ValidateNode(to, nodeCount, nameof(edges));
            offsets[from + 1]++;
            offsets[to + 1]++;
        }

        for (var i = 1; i <= nodeCount; i++)
        {
            offsets[i] += offsets[i - 1];
        }

        var neighbours = new int[offsets[nodeCount]];
        var cursor = (int[])offsets.Clone();
        foreach (var (from, to) in edges)
        {
            neighbours[cursor[from]++] = to;
            neighbours[cursor[to]++] = from;
        }

        return (offsets, neighbours);
    }
}
