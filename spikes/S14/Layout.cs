using System.Threading.Tasks;

namespace S14;

/// <summary>
/// ForceAtlas2-style layout with Barnes-Hut repulsion, adaptive global speed, and an optional
/// reverse-gravity mode (pinned focus at the origin, ring targets by hop distance).
/// Each call to <see cref="Tick"/> is one fixed-cost iteration over all nodes.
/// Positions and forces are packed float arrays, interleaved x,y per node index.
/// </summary>
public sealed class Layout
{
    // Force constants. Chosen so the layout is stable at 1k to 10k; they do not change the cost model.
    public const float Kr = 1.0f;          // repulsion scale
    public const float Kg = 0.1f;          // strong gravity toward origin (scaled by mass and distance)
    public const float KRing = 0.2f;       // reverse gravity ring spring
    public const float Theta = 1.2f;       // Barnes-Hut opening angle (ForceAtlas2 default)
    public const float JitterTolerance = 1.0f;
    public const float MaxRise = 0.5f;
    public const float MaxDisplacement = 100f;

    private readonly Graph _g;
    private readonly int _n;
    private readonly bool _parallel;
    private readonly QuadTree _tree;
    private readonly int[][] _stacks;
    private readonly int _chunks;
    private readonly Action<int> _chunkBody;

    public Layout(Graph g, bool parallel)
    {
        _g = g;
        _n = g.NodeCount;
        _parallel = parallel;
        Pos = new float[2 * _n];
        Force = new float[2 * _n];
        PrevForce = new float[2 * _n];
        Swing = new float[_n];
        Mass = new float[_n];
        for (int i = 0; i < _n; i++) Mass[i] = g.Degree[i] + 1f;
        _tree = new QuadTree(_n);
        _chunks = parallel ? Math.Min(4 * Environment.ProcessorCount, 128) : 1;
        _stacks = new int[_chunks][];
        for (int c = 0; c < _chunks; c++) _stacks[c] = new int[4 * QuadTree.MaxDepth + 16];
        _chunkBody = ChunkBody;
        Pinned = -1;
    }

    public float[] Pos { get; }
    public float[] Force { get; }
    public float[] PrevForce { get; }
    public float[] Swing { get; }
    public float[] Mass { get; }
    public float Speed { get; set; } = 1f;
    public int Pinned { get; private set; }
    public int[]? Hop { get; private set; }
    public float RingSpacing { get; private set; }
    public float LastMeanDisplacement { get; private set; }
    public int Count => _n;
    public int TreeCells => _tree.CellCount;
    public int GrowCount => _tree.GrowCount;

    /// <summary>Uniform random start in a square of half-width <paramref name="half"/>.</summary>
    public void InitRandom(Rng rng, float half)
    {
        for (int i = 0; i < 2 * _n; i++) Pos[i] = (rng.NextFloat() * 2f - 1f) * half;
        Array.Clear(PrevForce);
        Speed = 1f;
    }

    /// <summary>Reverse gravity focus change: pin the focus node at the origin, ring targets by hop distance.</summary>
    public void SetFocus(int focus, float ringSpacing)
    {
        var hops = _g.HopsFrom(focus);
        int maxHop = 0;
        foreach (int h in hops) if (h > maxHop) maxHop = h;
        Hop = new int[_n];
        for (int i = 0; i < _n; i++) Hop[i] = hops[i] < 0 ? maxHop + 1 : hops[i];
        float fx = Pos[2 * focus], fy = Pos[2 * focus + 1];
        for (int i = 0; i < _n; i++)
        {
            Pos[2 * i] -= fx;
            Pos[2 * i + 1] -= fy;
        }
        Pinned = focus;
        RingSpacing = ringSpacing;
    }

    /// <summary>Mean absolute error between each node radius and its ring target (reverse gravity only).</summary>
    public double RingError()
    {
        if (Hop is null) return double.NaN;
        double sum = 0; int count = 0;
        for (int i = 0; i < _n; i++)
        {
            if (Hop[i] <= 0) continue;
            float r = MathF.Sqrt(Pos[2 * i] * Pos[2 * i] + Pos[2 * i + 1] * Pos[2 * i + 1]);
            sum += Math.Abs(r - RingSpacing * Hop[i]);
            count++;
        }
        return count == 0 ? 0 : sum / count;
    }

    public void Tick()
    {
        _tree.Build(Pos, Mass, _n);

        if (_parallel) Parallel.For(0, _chunks, _chunkBody);
        else ForceRange(0, _n, _stacks[0]);

        double totalSwing = 0, totalTraction = 0;
        for (int i = 0; i < _n; i++)
        {
            if (i == Pinned) { Swing[i] = 0f; continue; }
            float fx = Force[2 * i], fy = Force[2 * i + 1];
            float px = PrevForce[2 * i], py = PrevForce[2 * i + 1];
            float dx = fx - px, dy = fy - py;
            float sx = fx + px, sy = fy + py;
            float swing = Mass[i] * MathF.Sqrt(dx * dx + dy * dy);
            float traction = Mass[i] * 0.5f * MathF.Sqrt(sx * sx + sy * sy);
            Swing[i] = swing;
            totalSwing += swing;
            totalTraction += traction;
        }
        double target = totalSwing > 0 ? JitterTolerance * totalTraction / totalSwing : Speed;
        if (target > Speed) Speed += (float)Math.Min(target - Speed, MaxRise * Speed);
        else Speed -= (float)Math.Min(Speed - target, MaxRise * Speed);

        double sumDisp = 0;
        float speed = Speed;
        for (int i = 0; i < _n; i++)
        {
            if (i == Pinned)
            {
                Pos[2 * i] = 0f; Pos[2 * i + 1] = 0f;
                Force[2 * i] = 0f; Force[2 * i + 1] = 0f;
                continue;
            }
            float nodeSpeed = speed / (1f + MathF.Sqrt(speed * Swing[i]));
            float dx = Force[2 * i] * nodeSpeed, dy = Force[2 * i + 1] * nodeSpeed;
            float mag = MathF.Sqrt(dx * dx + dy * dy);
            if (mag > MaxDisplacement)
            {
                float s = MaxDisplacement / mag;
                dx *= s; dy *= s; mag = MaxDisplacement;
            }
            Pos[2 * i] += dx;
            Pos[2 * i + 1] += dy;
            PrevForce[2 * i] = Force[2 * i];
            PrevForce[2 * i + 1] = Force[2 * i + 1];
            sumDisp += mag;
        }
        LastMeanDisplacement = _n == 0 ? 0 : (float)(sumDisp / _n);
    }

    private void ChunkBody(int chunk)
    {
        int start = (int)((long)_n * chunk / _chunks);
        int end = (int)((long)_n * (chunk + 1) / _chunks);
        ForceRange(start, end, _stacks[chunk]);
    }

    private void ForceRange(int start, int end, int[] stack)
    {
        bool ring = Hop is not null;
        for (int i = start; i < end; i++)
        {
            if (i == Pinned)
            {
                Force[2 * i] = 0f; Force[2 * i + 1] = 0f;
                continue;
            }
            float xi = Pos[2 * i], yi = Pos[2 * i + 1], mi = Mass[i];
            float fx = 0f, fy = 0f;
            _tree.Repel(i, xi, yi, mi, Kr, Theta, stack, ref fx, ref fy);
            for (int k = _g.AdjStart[i]; k < _g.AdjStart[i + 1]; k++)
            {
                int j = _g.Adj[k];
                fx += Pos[2 * j] - xi;
                fy += Pos[2 * j + 1] - yi;
            }
            fx -= Kg * mi * xi;
            fy -= Kg * mi * yi;
            if (ring && Hop![i] > 0)
            {
                float r = MathF.Sqrt(xi * xi + yi * yi) + 1e-3f;
                float coef = -KRing * (r - RingSpacing * Hop[i]) / r;
                fx += coef * xi;
                fy += coef * yi;
            }
            Force[2 * i] = fx;
            Force[2 * i + 1] = fy;
        }
    }

    /// <summary>Copies of the state needed to warm-start a layout.</summary>
    public sealed record State(float[] Pos, float[] PrevForce, float Speed);

    public State Save() => new((float[])Pos.Clone(), (float[])PrevForce.Clone(), Speed);

    public void Load(State s)
    {
        Array.Copy(s.Pos, Pos, Pos.Length);
        Array.Copy(s.PrevForce, PrevForce, PrevForce.Length);
        Array.Clear(Force);
        Array.Clear(Swing);
        Speed = s.Speed;
        Pinned = -1;
        Hop = null;
    }
}
