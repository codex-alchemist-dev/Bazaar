namespace Bazaar.Server.GraphRetrieval.Tests;

/// <summary>Single-threaded, plainly written reference implementations (doc 64 section 14). Slow and obvious on purpose.</summary>
public static class Oracle
{
    /// <summary>Hop-limited best-path scores: the best score and the round of its last improvement per node.</summary>
    public static Dictionary<int, (double Score, int Level)> Expand(GraphSnapshot g, IEnumerable<SeedScore> seeds, int maxHops, double decay, EdgeDirection dir, double inFactor)
    {
        var best = new Dictionary<int, (double Score, int Level)>();
        var seedSet = new HashSet<int>();
        foreach (SeedScore s in seeds)
        {
            best[s.Node] = best.TryGetValue(s.Node, out var o) ? (Math.Max(o.Score, s.Score), 0) : (s.Score, 0);
            seedSet.Add(s.Node);
        }

        for (int h = 1; h <= maxHops; h++)
        {
            var snapshot = new Dictionary<int, (double Score, int Level)>(best);
            foreach ((int u, (double us, int _)) in snapshot)
            {
                foreach ((int v, double w) in Edges(g, u, dir, inFactor))
                {
                    if (seedSet.Contains(v))
                    {
                        continue;
                    }

                    double cand = us * w * decay;
                    if (!best.TryGetValue(v, out var cur) || cand > cur.Score)
                    {
                        best[v] = (cand, h);
                    }
                }
            }
        }

        return best;
    }

    public static double[] Ppr(GraphSnapshot g, IEnumerable<SeedScore> seeds, double alpha, int iterations, EdgeDirection dir, double inFactor)
    {
        int n = g.NodeCount;
        var r = new double[n];
        double total = seeds.Sum(s => s.Score);
        foreach (SeedScore s in seeds)
        {
            r[s.Node] += s.Score / total;
        }

        var outEdges = Enumerable.Range(0, n).Select(u => Edges(g, u, dir, inFactor).ToList()).ToArray();
        double[] x = (double[])r.Clone();
        for (int it = 0; it < iterations; it++)
        {
            var next = new double[n];
            double dangling = 0;
            for (int u = 0; u < n; u++)
            {
                double sum = outEdges[u].Sum(e => e.Weight);
                if (sum <= 0)
                {
                    dangling += x[u];
                    continue;
                }

                foreach ((int v, double w) in outEdges[u])
                {
                    next[v] += x[u] * w / sum;
                }
            }

            for (int v = 0; v < n; v++)
            {
                next[v] = (alpha * r[v]) + ((1 - alpha) * (next[v] + (dangling * r[v])));
            }

            x = next;
        }

        return x;
    }

    private static IEnumerable<(int Node, double Weight)> Edges(GraphSnapshot g, int u, EdgeDirection dir, double inFactor)
    {
        if (dir != EdgeDirection.Incoming)
        {
            for (int e = g.OutRowOffsets.Span[u]; e < g.OutRowOffsets.Span[u + 1]; e++)
            {
                int v = g.OutTargets.Span[e];
                double w = g.EdgeWeights.Span[e];
                if (v != u && w > 0)
                {
                    yield return (v, w);
                }
            }
        }

        if (dir != EdgeDirection.Outgoing)
        {
            for (int s = g.InRowOffsets.Span[u]; s < g.InRowOffsets.Span[u + 1]; s++)
            {
                int v = g.InSources.Span[s];
                double w = g.EdgeWeights.Span[g.InEdgeIndex.Span[s]] * (dir == EdgeDirection.Both ? inFactor : 1.0);
                if (v != u && w > 0)
                {
                    yield return (v, w);
                }
            }
        }
    }
}
