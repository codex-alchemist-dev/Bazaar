using System.Diagnostics;
using Bazaar.Constellation.Rendering;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Client.Constellation;

/// <summary>
/// Drives an <see cref="IConstellationRenderer"/> from a UI host: attaches it to a native window,
/// forwards scenes, layout frames and sizes, and draws on each <see cref="Tick"/> with a monotonic clock.
/// </summary>
public sealed class ConstellationRenderLoop : IDisposable
{
    private readonly IConstellationRenderer _renderer;
    private readonly Func<double> _clock;
    private readonly bool _ownsRenderer;
    private bool _disposed;

    /// <summary>
    /// Creates a loop around a renderer.
    /// </summary>
    /// <param name="renderer">The drawing backend to drive.</param>
    /// <param name="clock">Monotonic time source in seconds. Defaults to <see cref="Stopwatch.GetTimestamp"/> converted to seconds.</param>
    /// <param name="ownsRenderer">When true, <see cref="Dispose"/> also disposes the renderer. When false, the caller keeps ownership.</param>
    /// <exception cref="ArgumentNullException"><paramref name="renderer"/> is null.</exception>
    public ConstellationRenderLoop(IConstellationRenderer renderer, Func<double>? clock = null, bool ownsRenderer = false)
    {
        ArgumentNullException.ThrowIfNull(renderer);

        _renderer = renderer;
        _clock = clock ?? DefaultClock;
        _ownsRenderer = ownsRenderer;
    }

    /// <summary>True after a successful <see cref="AttachWindow"/> and until <see cref="Dispose"/>.</summary>
    public bool IsAttached { get; private set; }

    /// <summary>
    /// Binds the renderer to a Win32 window. Sets <see cref="IsAttached"/> from the result.
    /// </summary>
    /// <param name="platformWindow">The HWND of the window.</param>
    /// <param name="platformDisplay">The HINSTANCE of the window's module.</param>
    /// <param name="widthPixels">Surface width in pixels.</param>
    /// <param name="heightPixels">Surface height in pixels.</param>
    /// <returns>The attach outcome, including the reason when the renderer cannot draw.</returns>
    /// <exception cref="ObjectDisposedException">The loop has been disposed.</exception>
    public RenderAttachResult AttachWindow(nint platformWindow, nint platformDisplay, int widthPixels, int heightPixels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var surface = new RenderSurface(RenderSurfaceKind.Win32, platformDisplay, platformWindow, widthPixels, heightPixels);
        RenderAttachResult result = _renderer.Attach(surface);
        IsAttached = result.Attached;
        return result;
    }

    /// <summary>
    /// Replaces the topology shown by the renderer.
    /// </summary>
    /// <param name="scene">The new scene.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scene"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The loop has been disposed.</exception>
    public void SetScene(ConstellationScene scene)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(scene);

        _renderer.SetScene(scene);
    }

    /// <summary>
    /// Offers a layout frame to the renderer, timed with the loop clock.
    /// </summary>
    /// <param name="frame">The layout frame.</param>
    /// <returns>True when the renderer applied the frame; false when it is not newer than the last one applied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The loop has been disposed.</exception>
    public bool Submit(RenderFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        return _renderer.SubmitFrame(frame, _clock());
    }

    /// <summary>
    /// Forwards a new surface size to the renderer. Non-positive sizes are ignored and the renderer is not called.
    /// </summary>
    /// <param name="widthPixels">Surface width in pixels.</param>
    /// <param name="heightPixels">Surface height in pixels.</param>
    /// <exception cref="ObjectDisposedException">The loop has been disposed.</exception>
    public void Resize(int widthPixels, int heightPixels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (widthPixels <= 0 || heightPixels <= 0)
        {
            return;
        }

        _renderer.Resize(widthPixels, heightPixels);
    }

    /// <summary>
    /// Draws one frame at the current loop time. Intended to be called from a UI timer each frame.
    /// Does nothing until the renderer is attached.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The loop has been disposed.</exception>
    public void Tick()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsAttached)
        {
            _renderer.Draw(_clock());
        }
    }

    /// <summary>
    /// Marks the loop as detached and, when the loop owns the renderer, disposes it.
    /// Calling this more than once has no further effect.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsAttached = false;

        if (_ownsRenderer)
        {
            _renderer.Dispose();
        }
    }

    private static double DefaultClock()
    {
        return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    }
}
