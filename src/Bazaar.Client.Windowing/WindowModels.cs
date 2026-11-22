namespace Bazaar.Client.Windowing;

// Bazaar client module "bazaar.client.windowing" (doc 28 process model and launcher; doc 27 detached window manager;
// doc 41 sections 4.9 and 4.10). Pure model: no Avalonia, no OS calls. A head supplies IWindowHost (Avalonia) and
// IMonitorSource (OS). The dock library is undecided (spike S15), so nothing here depends on one.

/// <summary>What a top-level window is for.</summary>
public enum WindowKind
{
    /// <summary>The main shell window. There is exactly one.</summary>
    Main,

    /// <summary>A pane detached from a dock layout into its own window.</summary>
    Detached,

    /// <summary>Standalone editor window (doc 41 section 4.10). Waits on spike S12 to show Monaco; the model does not.</summary>
    Editor,

    /// <summary>Standalone local terminal window (doc 28 local terminal).</summary>
    Terminal,

    /// <summary>Diff or PR window.</summary>
    Diff,
}

public enum WindowState
{
    Normal,
    Maximized,
    Minimized,
}

/// <summary>Identifier of a window. Unique within a process.</summary>
public readonly record struct WindowId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>A rectangle in device-independent pixels, desktop coordinates.</summary>
public readonly record struct WindowBounds(int X, int Y, int Width, int Height);

/// <summary>One display. <see cref="WorkArea"/> excludes the taskbar.</summary>
public sealed record MonitorInfo(string Id, WindowBounds WorkArea, double Scale, bool IsPrimary);

/// <summary>What the caller asks to open. Null bounds let the manager cascade a default placement.</summary>
public sealed record WindowSpec(WindowKind Kind, string Title, string? Path = null, WindowBounds? Bounds = null, DockNode? Layout = null);

/// <summary>A window the manager tracks and the state file persists. Never holds message bodies or file contents.</summary>
public sealed record WindowRecord(
    WindowId Id,
    WindowKind Kind,
    string Title,
    WindowBounds Bounds,
    WindowState State,
    string? MonitorId,
    string? Path,
    DockNode? Layout);

/// <summary>Seam to the head. The Avalonia head implements it; tests use a fake.</summary>
public interface IWindowHost
{
    /// <summary>Creates and shows a top-level window for <paramref name="record"/>.</summary>
    ValueTask<IWindowHandle> OpenAsync(WindowRecord record, CancellationToken ct);
}

/// <summary>A live window as the host sees it.</summary>
public interface IWindowHandle
{
    WindowId Id { get; }

    /// <summary>Raised when the user moves or resizes the window.</summary>
    event EventHandler<WindowBounds>? BoundsChanged;

    /// <summary>Raised when the user asks to close the window (title bar button). The manager decides whether it closes.</summary>
    event EventHandler? CloseRequested;

    ValueTask ActivateAsync();

    ValueTask SetBoundsAsync(WindowBounds bounds);

    ValueTask CloseAsync();
}

/// <summary>Asked before a window closes, so a window with running processes can prompt first (doc 28 behaviour: terminal).</summary>
public interface IWindowCloseGuard
{
    /// <summary>Returns false to veto the close.</summary>
    ValueTask<bool> ConfirmCloseAsync(WindowRecord window, CancellationToken ct);
}

/// <summary>Current displays. A head backs it with the OS; tests with a list.</summary>
public interface IMonitorSource
{
    IReadOnlyList<MonitorInfo> Monitors { get; }
}

/// <summary>Where the window state lives. A head backs it with a file under the per-user data folder.</summary>
public interface IWindowStateFile
{
    /// <summary>The stored text, or null when there is none.</summary>
    string? Read();

    void Write(string text);
}
