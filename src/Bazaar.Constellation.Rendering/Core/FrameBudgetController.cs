namespace Bazaar.Constellation.Rendering.Core;

/// <summary>Starting caps for one class of GPU. Picked at startup, then adapted live (doc 40-style setting; operators may tighten).</summary>
public sealed record RenderTier(string Name, long MaxInstances, int MaxEdges, int MaxTiles, float RefineAtPixels);

/// <summary>
/// Picks a starting tier from the startup micro-benchmark: the measured GPU milliseconds to draw one million reference instances.
/// Doc: docs/design/constellation-scale-and-lod.md section 11 (DECIDED, owner 2026-11-19).
/// </summary>
public static class RenderTiers
{
    public static readonly RenderTier Phone = new("phone", 300_000, 150_000, 1024, 128f);
    public static readonly RenderTier Mid = new("mid", 2_000_000, 1_000_000, 8192, 96f);
    public static readonly RenderTier High = new("high", 4_000_000, 2_000_000, 16384, 64f);

    /// <summary>Under 0.4 ms per million reference instances is high, under 2 ms is mid, otherwise phone. A failed benchmark gives the lowest tier.</summary>
    public static RenderTier Pick(double msPerMillionInstances) => msPerMillionInstances switch
    {
        <= 0 or double.NaN => Phone,
        < 0.4 => High,
        < 2.0 => Mid,
        _ => Phone,
    };
}

/// <summary>
/// Closed loop that holds a frame-time target by scaling detail. Drops fast when the rolling p95 is over target, recovers
/// slowly and only after a calm stretch, so the picture does not flicker. A frame that is too slow loses detail, not frames.
/// </summary>
public sealed class FrameBudgetController
{
    public const float MinDetail = 0.05f;

    /// <summary>Headroom is spent on quality first: detail may rise to twice the tier's starting caps.</summary>
    public const float MaxDetail = 2f;

    private const int _window = 32;
    private const int _calmFramesToRaise = 45;

    private readonly float[] _samples = new float[_window];
    private readonly float[] _scratch = new float[_window];
    private readonly RenderTier _tier;
    private int _count;
    private int _next;
    private int _calm;

    public FrameBudgetController(RenderTier tier, double targetFrameMs)
    {
        ArgumentNullException.ThrowIfNull(tier);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetFrameMs);
        _tier = tier;
        TargetFrameMs = targetFrameMs;
    }

    public double TargetFrameMs { get; }

    /// <summary>1 is the tier's starting caps; lower is coarser, higher spends headroom on quality. Kept within <see cref="MinDetail"/> and <see cref="MaxDetail"/>.</summary>
    public float Detail { get; private set; } = 1f;

    /// <summary>Target for a display refresh rate in Hz (the default target).</summary>
    public static double TargetForRefreshRate(double hertz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hertz);
        return 1000.0 / hertz;
    }

    /// <summary>Feeds one measured GPU frame time and returns the selector limits for the next frame.</summary>
    public ViewSelectorOptions Update(double frameMs)
    {
        _samples[_next] = (float)frameMs;
        _next = (_next + 1) % _window;
        _count = Math.Min(_count + 1, _window);

        float p95 = Percentile95();
        if (p95 > TargetFrameMs)
        {
            Detail = MathF.Max(MinDetail, Detail * 0.85f);
            _calm = 0;
        }
        else if (p95 < TargetFrameMs * 0.8)
        {
            if (++_calm >= _calmFramesToRaise)
            {
                Detail = MathF.Min(MaxDetail, Detail * 1.04f);
                _calm = 0;
            }
        }
        else
        {
            _calm = 0;
        }

        return Current();
    }

    public ViewSelectorOptions Current() => new(
        MaxInstances: Math.Max(1L, (long)(_tier.MaxInstances * Detail)),
        RefineAtPixels: _tier.RefineAtPixels / Detail,
        MaxTiles: Math.Max(1, (int)(_tier.MaxTiles * Detail)));

    public int EdgeBudget => Math.Max(0, (int)(_tier.MaxEdges * Detail));

    private float Percentile95()
    {
        Array.Copy(_samples, _scratch, _count);
        Array.Sort(_scratch, 0, _count);
        return _scratch[Math.Min(_count - 1, (int)MathF.Ceiling(_count * 0.95f) - 1)];
    }
}
