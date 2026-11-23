namespace Bazaar.Server.Constellation.Clustering;

/// <summary>
/// Louvain community detection over an undirected weighted graph (Blondel et al., 2008), pure in-memory.
/// Determinism: nodes are processed in ordinal-sorted id order, ties prefer the current community and then the
/// lowest community index, and no shuffling is used, so the same input always gives the same map. No seed is needed.
/// </summary>
public static class LouvainClustering
{
    /// <summary>Gains at or below this are not moves, so floating-point noise cannot cause endless passes.</summary>
    private const double _minGain = 1e-12;

    /// <summary>
    /// Detects communities. <paramref name="resolution"/> is the modularity resolution gamma (1.0 is standard modularity);
    /// larger values give more, smaller communities.
    /// </summary>
    public static CommunityResult Detect(IEnumerable<string> nodeIds, IEnumerable<WeightedEdge> edges, double resolution = 1.0)
    {
        var (ids, original) = BuildGraph(nodeIds, edges, resolution);
        var n = ids.Length;

        // Map each original node to its node in the current aggregated level.
        var levelOf = Enumerable.Range(0, n).ToArray();
        var level = original;
        while (true)
        {
            var comm = LocalMove(level, resolution);
            var newId = Renumber(comm, out var count);
            for (var i = 0; i < n; i++)
            {
                levelOf[i] = newId[levelOf[i]];
            }

            if (count == level.Size)
            {
                break;
            }

            level = Aggregate(level, newId, count);
        }

        var finalIds = Renumber(levelOf, out _);
        var byNode = new Dictionary<string, int>(n, StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            byNode[ids[i]] = finalIds[i];
        }

        return new CommunityResult(byNode, ScorePartition(original, finalIds, resolution));
    }

    /// <summary>
    /// Modularity of a given partition on the input graph, with the same validation and resolution as
    /// <see cref="Detect"/>. Every node id in the graph must appear in <paramref name="communityByNode"/>.
    /// </summary>
    public static double Modularity(
        IEnumerable<string> nodeIds,
        IEnumerable<WeightedEdge> edges,
        IReadOnlyDictionary<string, int> communityByNode,
        double resolution = 1.0)
    {
        ArgumentNullException.ThrowIfNull(communityByNode);
        var (ids, original) = BuildGraph(nodeIds, edges, resolution);
        // Caller community labels can be any ints, so remap them to 0..k-1 before scoring.
        var remap = new Dictionary<int, int>();
        var partition = new int[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            if (!communityByNode.TryGetValue(ids[i], out var community))
            {
                throw new ArgumentException($"Partition has no community for node '{ids[i]}'.", nameof(communityByNode));
            }

            if (!remap.TryGetValue(community, out var dense))
            {
                dense = remap.Count;
                remap[community] = dense;
            }

            partition[i] = dense;
        }

        return ScorePartition(original, partition, resolution);
    }

    private static (string[] Ids, Level Graph) BuildGraph(IEnumerable<string> nodeIds, IEnumerable<WeightedEdge> edges, double resolution)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(edges);
        if (!double.IsFinite(resolution) || resolution <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Resolution must be a positive, finite number.");
        }

        var ids = nodeIds
            .Select(id => string.IsNullOrEmpty(id) ? throw new ArgumentException("Node ids must be non-empty.", nameof(nodeIds)) : id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var index = new Dictionary<string, int>(ids.Length, StringComparer.Ordinal);
        for (var i = 0; i < ids.Length; i++)
        {
            index[ids[i]] = i;
        }

        var selfLoop = new double[ids.Length];
        var pairs = new Dictionary<(int, int), double>();
        foreach (var edge in edges)
        {
            if (!index.TryGetValue(edge.From, out var a))
            {
                throw new ArgumentException($"Edge references node '{edge.From}', which is not in the node set.", nameof(edges));
            }

            if (!index.TryGetValue(edge.To, out var b))
            {
                throw new ArgumentException($"Edge references node '{edge.To}', which is not in the node set.", nameof(edges));
            }

            if (!double.IsFinite(edge.Weight) || edge.Weight < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(edges), edge.Weight, $"Edge '{edge.From}' to '{edge.To}' needs a finite, non-negative weight.");
            }

            if (edge.Weight == 0)
            {
                continue;
            }

            if (a == b)
            {
                selfLoop[a] += edge.Weight;
                continue;
            }

            var key = a < b ? (a, b) : (b, a);
            pairs[key] = pairs.GetValueOrDefault(key) + edge.Weight;
        }

        return (ids, Level.Create(ids.Length, selfLoop, pairs));
    }

    /// <summary>Phase 1: greedily move single nodes to the neighbouring community with the best modularity gain.</summary>
    private static int[] LocalMove(Level graph, double resolution)
    {
        var comm = Enumerable.Range(0, graph.Size).ToArray();
        var twoM = graph.TotalDegree;
        if (twoM <= 0)
        {
            return comm;
        }

        var sigmaTot = (double[])graph.Degree.Clone();
        var weightTo = new double[graph.Size];
        var seen = new bool[graph.Size];
        var touched = new List<int>();

        void AddTouched(int c)
        {
            if (!seen[c])
            {
                seen[c] = true;
                touched.Add(c);
            }
        }

        var moved = true;
        while (moved)
        {
            moved = false;
            for (var i = 0; i < graph.Size; i++)
            {
                var current = comm[i];
                var ki = graph.Degree[i];

                // Weight from node i into each neighbouring community (self-loops excluded).
                touched.Clear();
                AddTouched(current);
                var neighbours = graph.Neighbours[i];
                var neighbourWeights = graph.NeighbourWeights[i];
                for (var k = 0; k < neighbours.Length; k++)
                {
                    AddTouched(comm[neighbours[k]]);
                    weightTo[comm[neighbours[k]]] += neighbourWeights[k];
                }

                // Remove i from its community before comparing gains.
                sigmaTot[current] -= ki;

                var best = current;
                var bestGain = weightTo[current] - resolution * sigmaTot[current] * ki / twoM;
                touched.Sort();
                foreach (var c in touched)
                {
                    var gain = weightTo[c] - resolution * sigmaTot[c] * ki / twoM;
                    if (gain > bestGain + _minGain)
                    {
                        best = c;
                        bestGain = gain;
                    }
                }

                sigmaTot[best] += ki;
                if (best != current)
                {
                    comm[i] = best;
                    moved = true;
                }

                foreach (var c in touched)
                {
                    weightTo[c] = 0;
                    seen[c] = false;
                }
            }
        }

        return comm;
    }

    /// <summary>Renumbers communities 0..count-1 in order of first appearance.</summary>
    private static int[] Renumber(int[] comm, out int count)
    {
        var map = new int[comm.Length];
        Array.Fill(map, -1);
        var next = 0;
        var result = new int[comm.Length];
        for (var i = 0; i < comm.Length; i++)
        {
            if (map[comm[i]] < 0)
            {
                map[comm[i]] = next++;
            }

            result[i] = map[comm[i]];
        }

        count = next;
        return result;
    }

    /// <summary>Phase 2: collapse each community into one node. Internal weight becomes a self-loop.</summary>
    private static Level Aggregate(Level graph, int[] newId, int count)
    {
        var selfLoop = new double[count];
        var pairs = new Dictionary<(int, int), double>();
        for (var i = 0; i < graph.Size; i++)
        {
            var c = newId[i];
            selfLoop[c] += graph.SelfLoop[i];
            var neighbours = graph.Neighbours[i];
            var weights = graph.NeighbourWeights[i];
            for (var k = 0; k < neighbours.Length; k++)
            {
                var j = neighbours[k];
                if (j <= i)
                {
                    continue; // each undirected edge once
                }

                var cj = newId[j];
                if (c == cj)
                {
                    selfLoop[c] += weights[k];
                }
                else
                {
                    var key = c < cj ? (c, cj) : (cj, c);
                    pairs[key] = pairs.GetValueOrDefault(key) + weights[k];
                }
            }
        }

        return Level.Create(count, selfLoop, pairs);
    }

    /// <summary>Newman modularity: sum over communities of internal weight over 2m, minus the squared degree share.</summary>
    private static double ScorePartition(Level graph, int[] partition, double resolution)
    {
        var twoM = graph.TotalDegree;
        if (twoM <= 0 || partition.Length == 0)
        {
            return 0;
        }

        var max = partition.Max() + 1;
        var internalWeight = new double[max];
        var totalDegree = new double[max];
        for (var i = 0; i < graph.Size; i++)
        {
            var c = partition[i];
            totalDegree[c] += graph.Degree[i];
            internalWeight[c] += 2 * graph.SelfLoop[i];
            var neighbours = graph.Neighbours[i];
            var weights = graph.NeighbourWeights[i];
            for (var k = 0; k < neighbours.Length; k++)
            {
                if (partition[neighbours[k]] == c)
                {
                    internalWeight[c] += weights[k];
                }
            }
        }

        var q = 0.0;
        for (var c = 0; c < max; c++)
        {
            var share = totalDegree[c] / twoM;
            q += internalWeight[c] / twoM - resolution * share * share;
        }

        return q;
    }

    /// <summary>Compact weighted graph: adjacency lists without self-loops, plus per-node self-loop weight.</summary>
    private sealed class Level
    {
        private Level(int size, double[] selfLoop, int[][] neighbours, double[][] neighbourWeights, double[] degree)
        {
            Size = size;
            SelfLoop = selfLoop;
            Neighbours = neighbours;
            NeighbourWeights = neighbourWeights;
            Degree = degree;
            TotalDegree = degree.Sum();
        }

        public int Size { get; }

        /// <summary>Weight of loops on each node, counted once per loop.</summary>
        public double[] SelfLoop { get; }

        public int[][] Neighbours { get; }

        public double[][] NeighbourWeights { get; }

        /// <summary>Weighted degree; a self-loop counts twice, as in the usual undirected convention.</summary>
        public double[] Degree { get; }

        /// <summary>Sum of all degrees, which is 2m.</summary>
        public double TotalDegree { get; }

        public static Level Create(int size, double[] selfLoop, Dictionary<(int, int), double> pairs)
        {
            var lists = new List<(int, double)>[size];
            for (var i = 0; i < size; i++)
            {
                lists[i] = [];
            }

            foreach (var ((a, b), w) in pairs.OrderBy(p => p.Key.Item1).ThenBy(p => p.Key.Item2))
            {
                lists[a].Add((b, w));
                lists[b].Add((a, w));
            }

            var neighbours = new int[size][];
            var weights = new double[size][];
            var degree = new double[size];
            for (var i = 0; i < size; i++)
            {
                neighbours[i] = lists[i].Select(e => e.Item1).ToArray();
                weights[i] = lists[i].Select(e => e.Item2).ToArray();
                degree[i] = weights[i].Sum() + 2 * selfLoop[i];
            }

            return new Level(size, selfLoop, neighbours, weights, degree);
        }
    }
}
