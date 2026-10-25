using System.Runtime.CompilerServices;

namespace S14;

/// <summary>
/// Barnes-Hut quadtree in flat arrays (cells and bodies are indices). Rebuilt every tick,
/// no per-tick allocation once capacity is reached. Body positions are read from the packed
/// interleaved float array (x at 2i, y at 2i+1), the same layout as LayoutFrame.
/// </summary>
public sealed class QuadTree
{
    public const int MaxDepth = 40;

    private int _cap;
    private int[] _child = Array.Empty<int>();   // 4 per cell; child[c*4] < 0 means leaf
    private int[] _head = Array.Empty<int>();    // leaf body list head (-1 empty)
    private float[] _gx = Array.Empty<float>();  // geometric cell centre
    private float[] _gy = Array.Empty<float>();
    private float[] _hw = Array.Empty<float>();  // geometric half width
    private float[] _mass = Array.Empty<float>();
    private float[] _cx = Array.Empty<float>();  // mass-weighted centre of mass (sums, then divided)
    private float[] _cy = Array.Empty<float>();
    private int[] _next = Array.Empty<int>();    // per body: next body in leaf list

    private float[] _pos = Array.Empty<float>();
    private float[] _bodyMass = Array.Empty<float>();

    public int CellCount { get; private set; }
    public int GrowCount { get; private set; }

    public QuadTree(int bodyCount)
    {
        _next = new int[bodyCount];
        Reserve(8 * bodyCount + 64);
    }

    private void Reserve(int cap)
    {
        if (cap <= _cap) return;
        if (_cap > 0) GrowCount++;
        _cap = cap;
        Array.Resize(ref _child, cap * 4);
        Array.Resize(ref _head, cap);
        Array.Resize(ref _gx, cap);
        Array.Resize(ref _gy, cap);
        Array.Resize(ref _hw, cap);
        Array.Resize(ref _mass, cap);
        Array.Resize(ref _cx, cap);
        Array.Resize(ref _cy, cap);
    }

    private int AllocCell(float gx, float gy, float hw)
    {
        if (CellCount == _cap) Reserve(_cap * 2);
        int idx = CellCount++;
        int c4 = idx * 4;
        _child[c4] = _child[c4 + 1] = _child[c4 + 2] = _child[c4 + 3] = -1;
        _head[idx] = -1;
        _gx[idx] = gx;
        _gy[idx] = gy;
        _hw[idx] = hw;
        _mass[idx] = 0f;
        _cx[idx] = 0f;
        _cy[idx] = 0f;
        return idx;
    }

    /// <summary>Rebuilds the tree over bodies 0..n-1 at <paramref name="pos"/> with the given masses.</summary>
    public void Build(float[] pos, float[] mass, int n)
    {
        _pos = pos;
        _bodyMass = mass;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            float x = pos[2 * i], y = pos[2 * i + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        float half = MathF.Max(maxX - minX, maxY - minY) * 0.5f * 1.001f + 1e-3f;
        CellCount = 0;
        AllocCell((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, half);
        for (int b = 0; b < n; b++)
        {
            _next[b] = -1;
            Insert(0, b, 0);
        }
        for (int c = 0; c < CellCount; c++)
        {
            float m = _mass[c];
            if (m > 0f)
            {
                _cx[c] /= m;
                _cy[c] /= m;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Quad(int c, float x, float y) =>
        (x >= _gx[c] ? 1 : 0) | (y >= _gy[c] ? 2 : 0);

    private void Insert(int c, int b, int depth)
    {
        float bx = _pos[2 * b], by = _pos[2 * b + 1], m = _bodyMass[b];
        _mass[c] += m;
        _cx[c] += bx * m;
        _cy[c] += by * m;
        int c4 = c * 4;
        if (_child[c4] >= 0)
        {
            Insert(_child[c4 + Quad(c, bx, by)], b, depth + 1);
            return;
        }
        if (_head[c] < 0)
        {
            _head[c] = b;
            _next[b] = -1;
            return;
        }
        if (depth >= MaxDepth)
        {
            // Coincident bodies: keep them in one leaf list (treated as a single mass by the traversal).
            _next[b] = _head[c];
            _head[c] = b;
            return;
        }
        Subdivide(c);
        int e = _head[c];
        _head[c] = -1;
        while (e >= 0)
        {
            int nx = _next[e];
            float ex = _pos[2 * e], ey = _pos[2 * e + 1];
            Insert(_child[c4 + Quad(c, ex, ey)], e, depth + 1);
            e = nx;
        }
        Insert(_child[c4 + Quad(c, bx, by)], b, depth + 1);
    }

    private void Subdivide(int c)
    {
        float h = _hw[c] * 0.5f;
        float gx = _gx[c], gy = _gy[c];
        for (int q = 0; q < 4; q++)
        {
            float ox = (q & 1) != 0 ? h : -h;
            float oy = (q & 2) != 0 ? h : -h;
            int child = AllocCell(gx + ox, gy + oy, h);
            _child[c * 4 + q] = child;
        }
    }

    /// <summary>
    /// Barnes-Hut repulsion on body <paramref name="i"/>: sums kr * mi * mj * (xi - xj) / d^2 over
    /// bodies j, approximating a cell by its centre of mass when cellWidth / distance is below theta
    /// and the cell does not contain body i. <paramref name="stack"/> must hold at least 4*MaxDepth+16 ints.
    /// </summary>
    public void Repel(int i, float xi, float yi, float mi, float kr, float theta, int[] stack, ref float fx, ref float fy)
    {
        float theta2 = theta * theta;
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            int c = stack[--sp];
            float m = _mass[c];
            if (m <= 0f) continue;
            int c4 = c * 4;
            if (_child[c4] < 0)
            {
                for (int b = _head[c]; b >= 0; b = _next[b])
                {
                    if (b == i) continue;
                    float dx = xi - _pos[2 * b], dy = yi - _pos[2 * b + 1];
                    float d2 = dx * dx + dy * dy + 0.01f;
                    float s = kr * mi * _bodyMass[b] / d2;
                    fx += s * dx;
                    fy += s * dy;
                }
                continue;
            }
            float hw = _hw[c];
            float cmx = _cx[c], cmy = _cy[c];
            float dxc = xi - cmx, dyc = yi - cmy;
            float d2c = dxc * dxc + dyc * dyc + 0.01f;
            float w = 2f * hw;
            bool contains = MathF.Abs(xi - _gx[c]) <= hw && MathF.Abs(yi - _gy[c]) <= hw;
            if (!contains && w * w < theta2 * d2c)
            {
                float s = kr * mi * m / d2c;
                fx += s * dxc;
                fy += s * dyc;
                continue;
            }
            for (int q = 0; q < 4; q++)
            {
                int ch = _child[c4 + q];
                if (ch >= 0 && _mass[ch] > 0f) stack[sp++] = ch;
            }
        }
    }
}
