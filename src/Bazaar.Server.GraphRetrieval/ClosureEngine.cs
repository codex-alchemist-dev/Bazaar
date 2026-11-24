namespace Bazaar.Server.GraphRetrieval;

internal static class ClosureEngine
{
    private struct Candidate
    {
        public int Dst;
        public int Pred;
        public byte Kind;
    }

    public static ClosureResult Run(
        GraphSnapshot g,
        int[] roots,
        EdgeDirection direction,
        EdgeKindSet kinds,
        int maxDepth,
        int maxNodes,
        NodeMask? mask,
        CpuGraphKernelOptions options,
        CancellationToken ct)
    {
        int n = g.NodeCount;
        var depth = new int[n];
        Array.Fill(depth, -1);
        var pred = new int[n];
        var predKind = new byte[n];
        var visited = new List<int>();
        var frontier = new List<int>();

        foreach (int r in roots)
        {
            if (visited.Count >= maxNodes)
            {
                break;
            }

            depth[r] = 0;
            pred[r] = -1;
            visited.Add(r);
            frontier.Add(r);
        }

        var bestOf = new Candidate[n];
        var touchedFlag = new bool[n];

        for (int d = 1; d <= maxDepth && frontier.Count > 0 && visited.Count < maxNodes; d++)
        {
            ct.ThrowIfCancellationRequested();
            int[] front = [.. frontier];
            var produced = new List<Candidate>[ParallelChunks.ChunkCount(front.Length, options.ChunkSize)];

            ParallelChunks.Run(front.Length, options.ChunkSize, options.MaxDegreeOfParallelism, (chunk, start, end) =>
            {
                var local = new List<Candidate>();
                var buffer = new List<Neighbour>();
                for (int i = start; i < end; i++)
                {
                    int u = front[i];
                    buffer.Clear();
                    Neighbours.Gather(g, u, direction, 1.0, kinds, mask, requirePositiveWeight: false, buffer);
                    foreach (Neighbour nb in buffer)
                    {
                        if (depth[nb.Node] < 0)
                        {
                            local.Add(new Candidate { Dst = nb.Node, Pred = u, Kind = nb.Kind });
                        }
                    }
                }

                produced[chunk] = local;
            }, ct);

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
                    else if (c.Pred < bestOf[c.Dst].Pred || (c.Pred == bestOf[c.Dst].Pred && c.Kind < bestOf[c.Dst].Kind))
                    {
                        bestOf[c.Dst] = c;
                    }
                }
            }

            touched.Sort();
            int room = maxNodes - visited.Count;
            var next = new List<int>();
            foreach (int v in touched)
            {
                touchedFlag[v] = false;
                if (next.Count < room)
                {
                    depth[v] = d;
                    pred[v] = bestOf[v].Pred;
                    predKind[v] = bestOf[v].Kind;
                    next.Add(v);
                    visited.Add(v);
                }
            }

            frontier = next;
        }

        var nodes = visited
            .OrderBy(v => depth[v])
            .ThenBy(v => v)
            .Select(v => new ClosureNode(v, depth[v], pred[v], (GraphEdgeKind)predKind[v]))
            .ToArray();

        return new ClosureResult(nodes, FindCycleNodes(g, visited, depth, direction, kinds, mask));
    }

    /// <summary>Nodes of the closure that lie on a cycle (a non-trivial strongly connected component, or a self loop), ascending.</summary>
    private static int[] FindCycleNodes(GraphSnapshot g, List<int> visited, int[] depth, EdgeDirection direction, EdgeKindSet kinds, NodeMask? mask)
    {
        // Iterative Tarjan over the subgraph induced by the visited nodes.
        var index = new Dictionary<int, int>();
        var low = new Dictionary<int, int>();
        var onStack = new HashSet<int>();
        var stack = new Stack<int>();
        var cycle = new SortedSet<int>();
        int counter = 0;
        var buffer = new List<Neighbour>();

        List<int> Succ(int u)
        {
            buffer.Clear();
            Neighbours.Gather(g, u, direction, 1.0, kinds, mask, requirePositiveWeight: false, buffer);
            var list = new List<int>(buffer.Count);
            foreach (Neighbour nb in buffer)
            {
                if (depth[nb.Node] >= 0)
                {
                    list.Add(nb.Node);
                }
            }

            list.Sort();
            return list;
        }

        // Self loops are skipped by Gather, so detect them directly.
        ReadOnlySpan<int> row = g.OutRowOffsets.Span;
        ReadOnlySpan<int> col = g.OutTargets.Span;
        ReadOnlySpan<byte> kd = g.EdgeKinds.Span;
        foreach (int u in visited)
        {
            for (int e = row[u]; e < row[u + 1]; e++)
            {
                if (col[e] == u && kinds.Contains(kd[e]))
                {
                    cycle.Add(u);
                }
            }
        }

        foreach (int root in visited.Order())
        {
            if (index.ContainsKey(root))
            {
                continue;
            }

            var work = new Stack<(int Node, List<int> Succ, int Next)>();
            index[root] = low[root] = counter++;
            stack.Push(root);
            onStack.Add(root);
            work.Push((root, Succ(root), 0));

            while (work.Count > 0)
            {
                (int u, List<int> succ, int next) = work.Pop();
                if (next < succ.Count)
                {
                    work.Push((u, succ, next + 1));
                    int v = succ[next];
                    if (!index.TryGetValue(v, out int vIndex))
                    {
                        index[v] = low[v] = counter++;
                        stack.Push(v);
                        onStack.Add(v);
                        work.Push((v, Succ(v), 0));
                    }
                    else if (onStack.Contains(v))
                    {
                        low[u] = Math.Min(low[u], vIndex);
                    }

                    continue;
                }

                if (low[u] == index[u])
                {
                    var comp = new List<int>();
                    int w;
                    do
                    {
                        w = stack.Pop();
                        onStack.Remove(w);
                        comp.Add(w);
                    }
                    while (w != u);

                    if (comp.Count > 1)
                    {
                        foreach (int c in comp)
                        {
                            cycle.Add(c);
                        }
                    }
                }

                if (work.Count > 0)
                {
                    int parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[u]);
                }
            }
        }

        return [.. cycle];
    }
}
