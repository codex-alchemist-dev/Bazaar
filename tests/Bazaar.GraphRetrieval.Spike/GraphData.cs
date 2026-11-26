using System.Collections.Concurrent;

namespace Bazaar.GraphRetrieval.Spike;

/// <summary>
/// A synthetic CSR graph with typed, weighted, directed edges. Kinds: 0 link (weight 1.0), 1 derived_from (0.8), 2 supersedes (0.5).
/// Kinds 1 and 2 point to a node a short distance below the source (a DAG with long chains); kind 0 targets are skewed to low ids (hubs).
/// Out-degree: 90 percent of nodes about half the mean, 9 percent twice, 1 percent thirty times.
/// </summary>
internal sealed class GraphData
{
    public static readonly float[] KindWeights = [1.0f, 0.8f, 0.5f];

    public GraphData(long edgeTarget, int meanDegree = 10, int seed = 7)
    {
        Nodes = (int)Math.Max(16, edgeTarget / meanDegree);
        Offsets = new uint[Nodes + 1];
        uint[] degree = new uint[Nodes];
        long total = 0;
        for (int u = 0; u < Nodes; u++)
        {
            ulong h = Hash((ulong)u, (ulong)seed);
            int bucket = (int)(h % 100);
            int deg = bucket < 90 ? meanDegree / 2 : bucket < 99 ? meanDegree * 2 : meanDegree * 30;
            degree[u] = (uint)Math.Max(1, deg);
            Offsets[u] = (uint)total;
            total += degree[u];
        }

        Offsets[Nodes] = (uint)total;
        Edges = total;
        Cols = new uint[total];
        uint[] kindWords = new uint[(total + 3) / 4];
        Kinds = kindWords;
        int blocks = (Nodes + 8191) / 8192;
        Parallel.For(0, blocks, b =>
        {
            int first = b * 8192;
            int last = Math.Min(Nodes, first + 8192);
            for (int u = first; u < last; u++)
            {
                uint start = Offsets[u];
                for (uint i = 0; i < degree[u]; i++)
                {
                    ulong h = Hash(((ulong)u << 20) ^ i, (ulong)seed + 99);
                    int pick = (int)(h % 100);
                    int kind = pick < 70 ? 0 : pick < 90 ? 1 : 2;
                    uint target;
                    if (kind == 0)
                    {
                        double r = ((h >> 8) & 0xFFFFFF) / 16777216.0;
                        target = (uint)Math.Min(Nodes - 1, (long)(Nodes * r * r * r));
                    }
                    else
                    {
                        long below = 1 + (int)((h >> 40) % 64);
                        target = (uint)Math.Max(0, u - below);
                    }

                    long edge = start + i;
                    Cols[edge] = target;
                    // Four kinds share one word; blocks never share a word because a block starts at a node boundary, so use an atomic or.
                    Interlocked.Or(ref kindWords[edge >> 2], (uint)kind << (int)((edge & 3) * 8));
                }
            }
        });
    }

    public int Nodes { get; }

    public long Edges { get; }

    public uint[] Offsets { get; }

    public uint[] Cols { get; }

    public uint[] Kinds { get; }

    public int Kind(long edge) => (int)((Kinds[edge >> 2] >> (int)((edge & 3) * 8)) & 0xFF);

    public static ulong Hash(ulong x, ulong salt)
    {
        ulong z = (x + 0x9E3779B97F4A7C15UL) ^ (salt * 0xD1B54A32D192ED03UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Distinct seed nodes for query <paramref name="query"/>.</summary>
    public uint[] Seeds(int query, int count)
    {
        var seeds = new List<uint>(count);
        var seen = new HashSet<uint>();
        for (ulong i = 0; seeds.Count < count; i++)
        {
            uint s = (uint)(Hash((ulong)query * 1000 + i, 5) % (ulong)Nodes);
            if (seen.Add(s))
            {
                seeds.Add(s);
            }
        }

        return [.. seeds];
    }

    /// <summary>Transposed CSR for the pull form of personalised PageRank: in-edge offsets, sources and the coefficient weight(kind) / out-weight(source).</summary>
    public (uint[] InOffsets, uint[] InSources, float[] InCoefficients) Transposed()
    {
        var inOffsets = new uint[Nodes + 1];
        for (long e = 0; e < Edges; e++)
        {
            inOffsets[Cols[e] + 1]++;
        }

        for (int v = 0; v < Nodes; v++)
        {
            inOffsets[v + 1] += inOffsets[v];
        }

        var cursor = new uint[Nodes];
        var sources = new uint[Edges];
        var coefficients = new float[Edges];
        for (int u = 0; u < Nodes; u++)
        {
            double outWeight = 0;
            for (uint e = Offsets[u]; e < Offsets[u + 1]; e++)
            {
                outWeight += KindWeights[Kind(e)];
            }

            for (uint e = Offsets[u]; e < Offsets[u + 1]; e++)
            {
                uint v = Cols[e];
                uint slot = inOffsets[v] + cursor[v]++;
                sources[slot] = (uint)u;
                coefficients[slot] = (float)(KindWeights[Kind(e)] / outWeight);
            }
        }

        return (inOffsets, sources, coefficients);
    }
}

/// <summary>Per-level count, sum and xor of reached node ids: equal for two engines exactly when they reached the same set at each level.</summary>
internal readonly record struct LevelDigest(uint Count, uint Sum, uint Xor);
