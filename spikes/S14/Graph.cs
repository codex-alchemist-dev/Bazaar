namespace S14;

/// <summary>Undirected graph in edge-list and CSR form.</summary>
public sealed class Graph
{
    public Graph(int nodeCount, int[] edgeA, int[] edgeB)
    {
        NodeCount = nodeCount;
        EdgeA = edgeA;
        EdgeB = edgeB;
        Degree = new int[nodeCount];
        for (int e = 0; e < edgeA.Length; e++)
        {
            Degree[edgeA[e]]++;
            Degree[edgeB[e]]++;
        }
        AdjStart = new int[nodeCount + 1];
        for (int i = 0; i < nodeCount; i++)
        {
            AdjStart[i + 1] = AdjStart[i] + Degree[i];
        }
        Adj = new int[AdjStart[nodeCount]];
        var fill = new int[nodeCount];
        for (int e = 0; e < edgeA.Length; e++)
        {
            int a = edgeA[e], b = edgeB[e];
            Adj[AdjStart[a] + fill[a]++] = b;
            Adj[AdjStart[b] + fill[b]++] = a;
        }
    }

    public int NodeCount { get; }
    public int[] EdgeA { get; }
    public int[] EdgeB { get; }
    public int[] Degree { get; }
    public int[] AdjStart { get; }
    public int[] Adj { get; }

    /// <summary>
    /// Preferential attachment (Barabasi-Albert style). Each new node links to <paramref name="m"/>
    /// distinct earlier nodes chosen proportionally to degree. Average degree is about 2m.
    /// Node k only ever links to nodes below k, so Prefix(n) of a larger graph is the graph
    /// that would have been generated with n nodes from the same seed.
    /// </summary>
    public static Graph PreferentialAttachment(int nodeCount, int m, ulong seed)
    {
        var rng = new Rng(seed);
        var ea = new List<int>(nodeCount * m + 1);
        var eb = new List<int>(nodeCount * m + 1);
        var endpoints = new List<int>(nodeCount * m * 2 + 2);
        ea.Add(0); eb.Add(1);
        endpoints.Add(0); endpoints.Add(1);
        var chosen = new int[m];
        for (int v = 2; v < nodeCount; v++)
        {
            int got = 0;
            int tries = 0;
            while (got < m && tries < 1000)
            {
                tries++;
                int t = endpoints[rng.NextInt(endpoints.Count)];
                bool dup = false;
                for (int k = 0; k < got; k++)
                {
                    if (chosen[k] == t) { dup = true; break; }
                }
                if (!dup) chosen[got++] = t;
            }
            for (int k = 0; k < got; k++)
            {
                ea.Add(v); eb.Add(chosen[k]);
                endpoints.Add(v); endpoints.Add(chosen[k]);
            }
        }
        return new Graph(nodeCount, ea.ToArray(), eb.ToArray());
    }

    /// <summary>Induced subgraph on nodes 0..n-1 (valid because of the prefix property).</summary>
    public Graph Prefix(int n)
    {
        var ea = new List<int>();
        var eb = new List<int>();
        for (int e = 0; e < EdgeA.Length; e++)
        {
            if (EdgeA[e] < n && EdgeB[e] < n) { ea.Add(EdgeA[e]); eb.Add(EdgeB[e]); }
        }
        return new Graph(n, ea.ToArray(), eb.ToArray());
    }

    /// <summary>BFS hop distance from <paramref name="source"/>; unreachable nodes get -1.</summary>
    public int[] HopsFrom(int source)
    {
        var dist = new int[NodeCount];
        Array.Fill(dist, -1);
        var queue = new int[NodeCount];
        int head = 0, tail = 0;
        dist[source] = 0;
        queue[tail++] = source;
        while (head < tail)
        {
            int u = queue[head++];
            for (int k = AdjStart[u]; k < AdjStart[u + 1]; k++)
            {
                int w = Adj[k];
                if (dist[w] < 0)
                {
                    dist[w] = dist[u] + 1;
                    queue[tail++] = w;
                }
            }
        }
        return dist;
    }
}
