namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// CPU reference for the Hi-Z occlusion pyramid (design: constellation-scale-and-lod.md section 11). Depth is reversed-Z:
/// 1 is nearest, 0 is farthest (0 also means "nothing drawn here"). Each coarser texel keeps the MIN of its 2x2 block,
/// the farthest depth in that area, so a texel value is a conservative bound: every covered pixel is at least that near.
/// Level 0 is the source depth image. This is the same rule the GPU pass uses (hiz_downsample.comp.glsl, owner-thread
/// validation on the Arc B580, 2026-11-19), so the two agree texel for texel: level 1 has ceil(size / 2) texels per axis
/// and takes the 2x2 block; every later level has max(1, floor(size / 2)) texels and, when its source size is odd, the
/// last texel of that axis also covers the one extra source texel (a 3-wide footprint). No source texel is dropped,
/// so every level keeps the global minimum.
/// </summary>
public sealed class HiZPyramid
{
    private readonly float[][] _levels;
    private readonly int[] _widths;
    private readonly int[] _heights;

    public HiZPyramid(ReadOnlySpan<float> depth, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (depth.Length != checked(width * height))
        {
            throw new ArgumentException("Depth image length must equal width * height.", nameof(depth));
        }

        int count = 1;
        for (int w = width, h = height, step = 1; w > 1 || h > 1; step++)
        {
            w = NextSize(w, step);
            h = NextSize(h, step);
            count++;
        }

        _levels = new float[count][];
        _widths = new int[count];
        _heights = new int[count];
        _levels[0] = depth.ToArray();
        _widths[0] = width;
        _heights[0] = height;
        for (int l = 1; l < count; l++)
        {
            int pw = _widths[l - 1];
            int ph = _heights[l - 1];
            int w = NextSize(pw, l);
            int h = NextSize(ph, l);
            float[] src = _levels[l - 1];
            float[] dst = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                int y0 = y * 2;
                int y1 = Math.Min(y0 + ((ph & 1) == 1 && y == h - 1 && l > 1 ? 2 : 1), ph - 1);
                y0 = Math.Min(y0, ph - 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = x * 2;
                    int x1 = Math.Min(x0 + ((pw & 1) == 1 && x == w - 1 && l > 1 ? 2 : 1), pw - 1);
                    x0 = Math.Min(x0, pw - 1);
                    float m = float.PositiveInfinity;
                    for (int yy = y0; yy <= y1; yy++)
                    {
                        for (int xx = x0; xx <= x1; xx++)
                        {
                            m = Math.Min(m, src[(yy * pw) + xx]);
                        }
                    }

                    dst[(y * w) + x] = m;
                }
            }

            _levels[l] = dst;
            _widths[l] = w;
            _heights[l] = h;
        }
    }

    public int LevelCount => _levels.Length;

    public int Width => _widths[0];

    public int Height => _heights[0];

    public int LevelWidth(int level) => _widths[level];

    public int LevelHeight(int level) => _heights[level];

    /// <summary>The stored (farthest-in-block) depth of one texel.</summary>
    public float Get(int level, int x, int y) => _levels[level][(y * _widths[level]) + x];

    private static int NextSize(int n, int step) => step == 1 ? Math.Max(1, (n + 1) / 2) : Math.Max(1, n / 2);
}
