namespace Bazaar.Server.GraphRetrieval;

internal static class PageRankEngine
{
    public static NodeScores Run(
        GraphSnapshot g,
        SeedScore[] seeds,
        double alpha,
        int iterations,
        double tolerance,
        EdgeDirection direction,
        double inFactor,
        EdgeKindSet kinds,
        NodeMask? effectiveMask,
        int? limit,
        CpuGraphKernelOptions options,
        CancellationToken ct)
    {
        SeedScore[] live = [.. seeds.Where(static s => s.Score > 0)];
        double total = 0;
        foreach (SeedScore s in live)
        {
            total += s.Score;
        }

        if (live.Length == 0 || total <= 0)
        {
            return new NodeScores([], 0, true, 0);
        }

        // 1. Discover the reachable, allowed subgraph and cache each node's eligible neighbours.
        int n = g.NodeCount;
        var seen = new bool[n];
        var order = new List<int>();
        var cache = new Dictionary<int, Neighbour[]>();
        var queue = new Queue<int>();
        foreach (SeedScore s in live)
        {
            seen[s.Node] = true;
            queue.Enqueue(s.Node);
        }

        var buffer = new List<Neighbour>();
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            ct.ThrowIfCancellationRequested();
            order.Add(u);
            buffer.Clear();
            Neighbours.Gather(g, u, direction, inFactor, kinds, effectiveMask, requirePositiveWeight: true, buffer);
            cache[u] = [.. buffer];
            foreach (Neighbour nb in buffer)
            {
                if (!seen[nb.Node])
                {
                    seen[nb.Node] = true;
                    queue.Enqueue(nb.Node);
                }
            }
        }

        int[] nodes = [.. order.Order()];
        int m = nodes.Length;
        var localOf = new Dictionary<int, int>(m);
        for (int i = 0; i < m; i++)
        {
            localOf[nodes[i]] = i;
        }

        // 2. Normalised restart vector and transition coefficients (pull form: per target, its sources in ascending order).
        var restart = new double[m];
        foreach (SeedScore s in live)
        {
            restart[localOf[s.Node]] = s.Score / total;
        }

        var outSum = new double[m];
        var inCount = new int[m + 1];
        for (int i = 0; i < m; i++)
        {
            foreach (Neighbour nb in cache[nodes[i]])
            {
                outSum[i] += nb.Weight;
                inCount[localOf[nb.Node] + 1]++;
            }
        }

        for (int i = 0; i < m; i++)
        {
            inCount[i + 1] += inCount[i];
        }

        var inSrc = new int[inCount[m]];
        var inCoef = new double[inCount[m]];
        var cursor = new int[m];
        for (int i = 0; i < m; i++)
        {
            foreach (Neighbour nb in cache[nodes[i]])
            {
                int v = localOf[nb.Node];
                int slot = inCount[v] + cursor[v]++;
                inSrc[slot] = i;
                inCoef[slot] = nb.Weight / outSum[i];
            }
        }

        var dangling = new List<int>();
        for (int i = 0; i < m; i++)
        {
            if (outSum[i] <= 0)
            {
                dangling.Add(i);
            }
        }

        // 3. Power iteration. Per-target sums run in a fixed order, so the result is independent of the thread count.
        double[] x = [.. restart];
        var next = new double[m];
        int chunks = ParallelChunks.ChunkCount(m, options.ChunkSize);
        var partial = new double[chunks];
        int done = 0;
        bool converged = false;
        double delta = 0;
        while (done < iterations)
        {
            ct.ThrowIfCancellationRequested();
            double dangleMass = 0;
            foreach (int d in dangling)
            {
                dangleMass += x[d];
            }

            double[] cur = x;
            ParallelChunks.Run(m, options.ChunkSize, options.MaxDegreeOfParallelism, (chunk, start, end) =>
            {
                double part = 0;
                for (int v = start; v < end; v++)
                {
                    double acc = 0;
                    for (int s = inCount[v]; s < inCount[v + 1]; s++)
                    {
                        acc += cur[inSrc[s]] * inCoef[s];
                    }

                    double value = (alpha * restart[v]) + ((1 - alpha) * (acc + (dangleMass * restart[v])));
                    next[v] = value;
                    part += Math.Abs(value - cur[v]);
                }

                partial[chunk] = part;
            }, ct);

            delta = 0;
            for (int c = 0; c < chunks; c++)
            {
                delta += partial[c];
            }

            (x, next) = (next, x);
            done++;
            if (delta < tolerance)
            {
                converged = true;
                break;
            }
        }

        var scores = new List<NodeScore>(m);
        for (int i = 0; i < m; i++)
        {
            if (x[i] > 0)
            {
                scores.Add(new NodeScore(nodes[i], x[i]));
            }
        }

        scores.Sort(static (a, b) =>
        {
            int c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : a.Node.CompareTo(b.Node);
        });
        if (limit is { } l && scores.Count > l)
        {
            scores.RemoveRange(l, scores.Count - l);
        }

        return new NodeScores(scores, done, converged, delta);
    }
}
