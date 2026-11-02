namespace Bazaar.Constellation.Rendering;

/// <summary>
/// Eases node positions from the last drawn state to the newest frame.
/// The client runs no simulation: it only blends between frames the server sent.
/// </summary>
public sealed class FrameInterpolator
{
    private readonly double _durationSeconds;
    private float[] _from = [];
    private float[] _to = [];
    private int _dimensions;
    private double _startSeconds;

    public FrameInterpolator(double durationSeconds = 0.25)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationSeconds);
        _durationSeconds = durationSeconds;
    }

    public bool HasFrame => _to.Length > 0;

    public int NodeCount => _dimensions == 0 ? 0 : _to.Length / _dimensions;

    /// <summary>
    /// Starts blending toward <paramref name="frame"/>. Nodes that already existed continue from where they are
    /// drawn now; new nodes appear at their target, so nothing jumps across the view.
    /// </summary>
    public void Push(RenderFrame frame, double nowSeconds)
    {
        ArgumentNullException.ThrowIfNull(frame);
        float[] target = frame.Positions.ToArray();
        float[] start = new float[target.Length];
        if (HasFrame && _dimensions == frame.Dimensions)
        {
            float[] current = new float[_to.Length];
            Sample(nowSeconds, current);
            int shared = Math.Min(current.Length, target.Length);
            Array.Copy(current, start, shared);
            Array.Copy(target, shared, start, shared, target.Length - shared);
        }
        else
        {
            Array.Copy(target, start, target.Length);
        }

        _from = start;
        _to = target;
        _dimensions = frame.Dimensions;
        _startSeconds = nowSeconds;
    }

    /// <summary>Forgets the current frame. The next pushed frame is drawn at its own positions.</summary>
    public void Reset()
    {
        _from = [];
        _to = [];
        _dimensions = 0;
        _startSeconds = 0;
    }

    /// <summary>Writes the current blended positions. Returns false when no frame has been pushed.</summary>
    public bool Sample(double nowSeconds, Span<float> destination)
    {
        if (!HasFrame)
        {
            return false;
        }

        if (destination.Length < _to.Length)
        {
            throw new ArgumentException("Destination is smaller than the frame.", nameof(destination));
        }

        double elapsed = nowSeconds - _startSeconds;
        float t = (float)Math.Clamp(elapsed / _durationSeconds, 0d, 1d);
        float eased = t * t * (3f - 2f * t); // smoothstep: no velocity jump at either end
        for (int i = 0; i < _to.Length; i++)
        {
            destination[i] = _from[i] + ((_to[i] - _from[i]) * eased);
        }

        return true;
    }
}
