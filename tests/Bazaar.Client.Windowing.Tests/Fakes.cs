namespace Bazaar.Client.Windowing.Tests;

/// <summary>In-memory state file. <see cref="Text"/> holds what was last written or seeded by a test.</summary>
internal sealed class FakeWindowStateFile : IWindowStateFile
{
    public string? Text { get; set; }

    public int WriteCount { get; private set; }

    /// <summary>When set, <see cref="Read"/> throws it instead of returning text.</summary>
    public Exception? ReadFailure { get; init; }

    public string? Read()
    {
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        return Text;
    }

    public void Write(string text)
    {
        Text = text;
        WriteCount++;
    }
}

/// <summary>Monitor source backed by a list the test controls.</summary>
internal sealed class FakeMonitorSource : IMonitorSource
{
    private readonly List<MonitorInfo> _monitors;

    public FakeMonitorSource(params MonitorInfo[] monitors)
    {
        _monitors = [.. monitors];
    }

    public IReadOnlyList<MonitorInfo> Monitors => _monitors;
}

/// <summary>Shared monitor and window builders for the windowing tests.</summary>
internal static class TestData
{
    /// <summary>Primary monitor: work area 1920x1040 at the origin.</summary>
    public static MonitorInfo Primary { get; } = new("primary", new WindowBounds(0, 0, 1920, 1040), 1.0, true);

    /// <summary>Secondary monitor to the right of the primary: work area 1280x1000 at x=1920.</summary>
    public static MonitorInfo Right { get; } = new("right", new WindowBounds(1920, 0, 1280, 1000), 1.0, false);

    /// <summary>Builds a window record with sensible defaults; tests override what they check.</summary>
    public static WindowRecord Window(
        string id,
        WindowKind kind = WindowKind.Detached,
        string title = "title",
        WindowBounds? bounds = null,
        WindowState state = WindowState.Normal,
        string? monitorId = "primary",
        string? path = null)
    {
        return new WindowRecord(
            new WindowId(id),
            kind,
            title,
            bounds ?? new WindowBounds(100, 100, 400, 300),
            state,
            monitorId,
            path,
            Layout: null);
    }
}
