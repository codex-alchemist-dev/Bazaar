namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// CPU reference for the Hi-Z occlusion pyramid (design: constellation-scale-and-lod.md section 11). Depth is reversed-Z:
/// 1 is nearest, 0 is farthest (0 also means "nothing drawn here"). Each coarser texel keeps the MIN of its 2x2 block,
/// the farthest depth in that area, so a texel value is a conservative bound: every covered pixel is at least that near.
/// Level 0 is the source depth image; level n+1 has ceil(size / 2) texels per axis, down to 1x1. Texels on an odd edge
/// combine the 1 or 2 source texels that exist.
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
        for (int w = width, h = height; w > 1 || h > 1; w = Half(w), h = Half(h))
        {
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
            int w = Half(pw);
            int h = Half(ph);
            float[] src = _levels[l - 1];
            float[] dst = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                int y0 = y * 2;
                int y1 = Math.Min(y0 + 1, ph - 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = x * 2;
                    int x1 = Math.Min(x0 + 1, pw - 1);
                    dst[(y * w) + x] = Math.Min(
                        Math.Min(src[(y0 * pw) + x0], src[(y0 * pw) + x1]),
                        Math.Min(src[(y1 * pw) + x0], src[(y1 * pw) + x1]));
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

    private static int Half(int n) => (n + 1) / 2;
}
