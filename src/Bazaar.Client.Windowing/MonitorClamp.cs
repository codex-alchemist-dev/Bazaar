namespace Bazaar.Client.Windowing;

/// <summary>
/// Fits a saved window rectangle onto a live monitor work area so a restored window is always reachable.
/// Pure and total: no exceptions for any input.
/// </summary>
public static class MonitorClamp
{
    /// <summary>Smallest width a restored window may have, in device-independent pixels.</summary>
    public const int MinWidth = 320;

    /// <summary>Smallest height a restored window may have, in device-independent pixels.</summary>
    public const int MinHeight = 200;

    /// <summary>
    /// Returns <paramref name="wanted"/> placed on one monitor's work area. The monitor is the one with
    /// <paramref name="monitorId"/> when present; otherwise the one overlapping <paramref name="wanted"/> the most;
    /// otherwise the primary (or the first). Size is at least 320x200 but never larger than the work area, so the
    /// whole window fits. Scale is ignored. With no monitors, <paramref name="wanted"/> is returned unchanged.
    /// </summary>
    public static WindowBounds Clamp(WindowBounds wanted, string? monitorId, IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors is null || monitors.Count == 0)
        {
            return wanted;
        }

        var target = Pick(wanted, monitorId, monitors);
        return Fit(wanted, target.WorkArea);
    }

    private static MonitorInfo Pick(WindowBounds wanted, string? monitorId, IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitorId is not null)
        {
            foreach (var monitor in monitors)
            {
                if (monitor.Id == monitorId)
                {
                    return monitor;
                }
            }
        }

        MonitorInfo? best = null;
        long bestArea = 0;
        foreach (var monitor in monitors)
        {
            var area = Overlap(wanted, monitor.WorkArea);
            if (area > bestArea)
            {
                best = monitor;
                bestArea = area;
            }
        }

        if (best is not null)
        {
            return best;
        }

        foreach (var monitor in monitors)
        {
            if (monitor.IsPrimary)
            {
                return monitor;
            }
        }

        return monitors[0];
    }

    private static WindowBounds Fit(WindowBounds wanted, WindowBounds area)
    {
        var width = FitSize(wanted.Width, MinWidth, area.Width);
        var height = FitSize(wanted.Height, MinHeight, area.Height);
        var x = FitPosition(wanted.X, area.X, area.Width, width);
        var y = FitPosition(wanted.Y, area.Y, area.Height, height);
        return new WindowBounds(x, y, width, height);
    }

    // The minimum applies first; the work area then wins, so a window never sticks out of a very small display.
    private static int FitSize(int wanted, int min, int available)
    {
        var size = Math.Max(wanted, min);
        return Math.Max(0, Math.Min(size, available));
    }

    private static int FitPosition(int wanted, int areaStart, int areaLength, int size)
    {
        long low = areaStart;
        long high = (long)areaStart + areaLength - size;
        if (high < low)
        {
            high = low;
        }

        return (int)Math.Clamp((long)wanted, low, high);
    }

    // Area of the intersection. Each side is bounded by the work area's int size, so the product fits in a long.
    private static long Overlap(WindowBounds window, WindowBounds area)
    {
        var left = Math.Max((long)window.X, area.X);
        var top = Math.Max((long)window.Y, area.Y);
        var right = Math.Min((long)window.X + window.Width, (long)area.X + area.Width);
        var bottom = Math.Min((long)window.Y + window.Height, (long)area.Y + area.Height);
        if (right <= left || bottom <= top)
        {
            return 0;
        }

        return (right - left) * (bottom - top);
    }
}
