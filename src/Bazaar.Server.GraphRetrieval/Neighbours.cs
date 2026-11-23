namespace Bazaar.Server.GraphRetrieval;

internal readonly record struct Neighbour(int Node, byte Kind, double Weight);

internal static class Neighbours
{
    /// <summary>
    /// Appends the eligible neighbours of <paramref name="u"/> to <paramref name="buffer"/>. Outgoing edges come first
    /// (in CSR order), then incoming edges. Self loops and masked targets are skipped.
    /// </summary>
    /// <param name="requirePositiveWeight">Skip zero-weight edges (expansion and PageRank); closure is structural and keeps them.</param>
    public static void Gather(
        GraphSnapshot g,
        int u,
        EdgeDirection direction,
        double inFactor,
        EdgeKindSet kinds,
        NodeMask? mask,
        bool requirePositiveWeight,
        List<Neighbour> buffer)
    {
        ReadOnlySpan<int> outRow = g.OutRowOffsets.Span;
        ReadOnlySpan<int> outCol = g.OutTargets.Span;
        ReadOnlySpan<byte> kind = g.EdgeKinds.Span;
        ReadOnlySpan<float> weight = g.EdgeWeights.Span;

        if (direction != EdgeDirection.Incoming)
        {
            for (int e = outRow[u]; e < outRow[u + 1]; e++)
            {
                int v = outCol[e];
                double w = weight[e];
                if (v == u || !kinds.Contains(kind[e]) || (requirePositiveWeight && w <= 0) || (mask is not null && !mask.Contains(v)))
                {
                    continue;
                }

                buffer.Add(new Neighbour(v, kind[e], w));
            }
        }

        if (direction != EdgeDirection.Outgoing)
        {
            double factor = direction == EdgeDirection.Both ? inFactor : 1.0;
            ReadOnlySpan<int> inRow = g.InRowOffsets.Span;
            ReadOnlySpan<int> inSrc = g.InSources.Span;
            ReadOnlySpan<int> inEdge = g.InEdgeIndex.Span;
            for (int s = inRow[u]; s < inRow[u + 1]; s++)
            {
                int v = inSrc[s];
                int e = inEdge[s];
                double w = weight[e] * factor;
                if (v == u || !kinds.Contains(kind[e]) || (requirePositiveWeight && w <= 0) || (mask is not null && !mask.Contains(v)))
                {
                    continue;
                }

                buffer.Add(new Neighbour(v, kind[e], w));
            }
        }
    }

    /// <summary>Keeps the <paramref name="cap"/> best neighbours by weight (descending), node, kind.</summary>
    public static void Cap(List<Neighbour> buffer, int start, int cap)
    {
        int count = buffer.Count - start;
        if (count <= cap)
        {
            return;
        }

        buffer.Sort(start, count, Comparer<Neighbour>.Create(static (a, b) =>
        {
            int c = b.Weight.CompareTo(a.Weight);
            if (c != 0)
            {
                return c;
            }

            c = a.Node.CompareTo(b.Node);
            return c != 0 ? c : a.Kind.CompareTo(b.Kind);
        }));
        buffer.RemoveRange(start + cap, count - cap);
    }
}
