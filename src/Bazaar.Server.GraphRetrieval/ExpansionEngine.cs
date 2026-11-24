namespace Bazaar.Server.GraphRetrieval;

internal static class ExpansionEngine
{
    private struct Candidate
    {
        public int Dst;
        public int Pred;
        public byte Kind;
        public double Score;
    }

    public static ExpansionResult Run(
        GraphSnapshot g,
        SeedScore[] seeds,
        int maxHops,
        double decay,
        EdgeDirection direction,
        double inFactor,
        EdgeKindSet kinds,
        NodeMask? mask,
        int maxEdgesPerNode,
        int maxReached,
        CpuGraphKernelOptions options,
        CancellationToken ct)
    {
        int n = g.NodeCount;
        var score = new double[n];
        var reached = new bool[n];
        var isSeed = new bool[n];
        var history = new List<ReachedNode>();

        var frontier = new List<int>();
        int reachedCount = 0;
        foreach (SeedScore s in seeds)
        {
            if (reachedCount >= maxReached)
            {
                break;
            }

            score[s.Node] = s.Score;
            reached[s.Node] = true;
            isSeed[s.Node] = true;
            reachedCount++;
            history.Add(new ReachedNode(s.Node, 0, s.Score, -1, default));
            frontier.Add(s.Node);
        }

        // Scratch for the per-round reduce: best candidate per destination.
        var bestOf = new Candidate[n];
        var touchedFlag = new bool[n];

        for (int hop = 1; hop <= maxHops && frontier.Count > 0; hop++)
        {
            ct.ThrowIfCancellationRequested();
            int chunks = ParallelChunks.ChunkCount(frontier.Count, options.ChunkSize);
            var produced = new List<Candidate>[chunks];
            int[] front = [.. frontier];

            ParallelChunks.Run(front.Length, options.ChunkSize, options.MaxDegreeOfParallelism, (chunk, start, end) =>
            {
                var local = new List<Candidate>();
                var buffer = new List<Neighbour>();
                for (int i = start; i < end; i++)
                {
                    int u = front[i];
                    buffer.Clear();
                    Neighbours.Gather(g, u, direction, inFactor, kinds, mask, requirePositiveWeight: true, buffer);
                    Neighbours.Cap(buffer, 0, maxEdgesPerNode);
                    double us = score[u];
                    foreach (Neighbour nb in buffer)
                    {
                        if (isSeed[nb.Node])
                        {
                            continue;
                        }

                        local.Add(new Candidate { Dst = nb.Node, Pred = u, Kind = nb.Kind, Score = us * nb.Weight * decay });
                    }
                }

                produced[chunk] = local;
            }, ct);

            // Reduce: best candidate per destination under a total order (score desc, pred asc, kind asc).
            var touched = new List<int>();
            foreach (List<Candidate> list in produced)
            {
                foreach (Candidate c in list)
                {
                    if (!touchedFlag[c.Dst])
                    {
                        touchedFlag[c.Dst] = true;
                        bestOf[c.Dst] = c;
                        touched.Add(c.Dst);
                    }
                    else if (Better(c, bestOf[c.Dst]))
                    {
                        bestOf[c.Dst] = c;
                    }
                }
            }

            touched.Sort();
            var improved = new List<int>();
            var fresh = new List<int>();
            foreach (int v in touched)
            {
                touchedFlag[v] = false;
                if (!reached[v])
                {
                    fresh.Add(v);
                }
                else if (bestOf[v].Score > score[v])
                {
                    improved.Add(v);
                }
            }

            int room = maxReached - reachedCount;
            if (fresh.Count > room)
            {
                fresh.Sort((a, b) =>
                {
                    int c = bestOf[b].Score.CompareTo(bestOf[a].Score);
                    return c != 0 ? c : a.CompareTo(b);
                });
                fresh.RemoveRange(Math.Max(room, 0), fresh.Count - Math.Max(room, 0));
                fresh.Sort();
            }

            var next = new List<int>(improved.Count + fresh.Count);
            foreach (int v in improved.Concat(fresh).Order())
            {
                Candidate c = bestOf[v];
                score[v] = c.Score;
                if (!reached[v])
                {
                    reached[v] = true;
                    reachedCount++;
                }

                history.Add(new ReachedNode(v, hop, c.Score, c.Pred, (GraphEdgeKind)c.Kind));
                next.Add(v);
            }

            frontier = next;
        }

        return new ExpansionResult(history);
    }

    private static bool Better(Candidate a, Candidate b)
    {
        int c = a.Score.CompareTo(b.Score);
        if (c != 0)
        {
            return c > 0;
        }

        if (a.Pred != b.Pred)
        {
            return a.Pred < b.Pred;
        }

        return a.Kind < b.Kind;
    }
}
