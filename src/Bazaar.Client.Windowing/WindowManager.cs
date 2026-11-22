namespace Bazaar.Client.Windowing;

/// <summary>
/// Tracks the top-level windows of the one client process (doc 28 process model, doc 27 detached window manager).
/// All windows share one connection and one Monaco bundle, so the manager owns no per-window session. It opens, closes,
/// detaches and re-docks panes, and asks <see cref="IWindowCloseGuard"/> before a window with running work closes.
/// Calls are serialised by an internal gate, so the UI thread and the launcher pipe thread may both call it.
/// </summary>
public sealed class WindowManager : IDisposable
{
    /// <summary>Most top-level windows the manager keeps open. A launch that would exceed it is refused.</summary>
    public const int MaxWindows = 32;

    private readonly IWindowHost _host;
    private readonly IWindowCloseGuard _guard;
    private readonly IMonitorSource _monitors;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<WindowId, Entry> _windows = [];
    private int _nextId;
    private int _cascade;

    /// <summary>Creates a manager over a head-supplied host, close guard and monitor list.</summary>
    public WindowManager(IWindowHost host, IWindowCloseGuard guard, IMonitorSource monitors)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(monitors);
        _host = host;
        _guard = guard;
        _monitors = monitors;
    }

    /// <summary>Raised after the set of windows or any window's layout changes. Handlers must not call back into the manager synchronously.</summary>
    public event EventHandler? Changed;

    /// <summary>A snapshot of the open windows, oldest first.</summary>
    public IReadOnlyList<WindowRecord> Windows
    {
        get
        {
            _gate.Wait();
            try
            {
                return _windows.Values.OrderBy(e => e.Order).Select(e => e.Record).ToList();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Opens a window. A second <see cref="WindowKind.Main"/> is refused, so the shell stays unique. Returns null when
    /// the window limit is reached or the main window already exists.
    /// </summary>
    public async Task<WindowRecord?> OpenAsync(WindowSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await OpenLockedAsync(spec, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Detaches a pane from <paramref name="sourceId"/> into a new <see cref="WindowKind.Detached"/> window. The source
    /// window keeps the rest of its layout. Returns null when the pane is not in that window or the limit is reached.
    /// </summary>
    public async Task<WindowRecord?> DetachAsync(WindowId sourceId, string paneId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_windows.TryGetValue(sourceId, out var source) || _windows.Count >= MaxWindows)
            {
                return null;
            }

            var result = DockOps.Detach(source.Record.Layout, paneId);
            if (result.Pane is null)
            {
                return null;
            }

            var spec = new WindowSpec(
                WindowKind.Detached,
                result.Pane.PaneId,
                Layout: new DockTabs([result.Pane], 0));
            var created = await OpenLockedAsync(spec, ct).ConfigureAwait(false);
            if (created is null)
            {
                return null;
            }

            source.Record = source.Record with { Layout = result.Remaining };
            RaiseChanged();
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Moves every pane of a detached window back into <paramref name="targetId"/> next to <paramref name="targetPaneId"/>
    /// and closes the detached window. Returns false when either window or the target pane is missing.
    /// </summary>
    public async Task<bool> RedockAsync(WindowId detachedId, WindowId targetId, string targetPaneId, DockEdge edge, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (detachedId == targetId
                || !_windows.TryGetValue(detachedId, out var detached)
                || detached.Record.Kind != WindowKind.Detached
                || !_windows.TryGetValue(targetId, out var target)
                || DockOps.Find(target.Record.Layout, targetPaneId) is null)
            {
                return false;
            }

            var layout = target.Record.Layout;
            foreach (var pane in DockOps.Panes(detached.Record.Layout))
            {
                layout = DockOps.Dock(layout, pane, targetPaneId, edge);
            }

            target.Record = target.Record with { Layout = layout };
            _windows.Remove(detachedId);
            await detached.Handle.CloseAsync().ConfigureAwait(false);
            RaiseChanged();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Closes a window after the close guard agrees. Closing the main window is refused here: the head ends the
    /// process instead, after closing the others. Returns true when the window closed.
    /// </summary>
    public async Task<bool> CloseAsync(WindowId id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_windows.TryGetValue(id, out var entry) || entry.Record.Kind == WindowKind.Main)
            {
                return false;
            }

            if (!await _guard.ConfirmCloseAsync(entry.Record, ct).ConfigureAwait(false))
            {
                return false;
            }

            _windows.Remove(id);
            entry.Detach();
            await entry.Handle.CloseAsync().ConfigureAwait(false);
            RaiseChanged();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Brings a window to the front. Returns false for an unknown id.</summary>
    public async Task<bool> ActivateAsync(WindowId id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_windows.TryGetValue(id, out var entry))
            {
                return false;
            }

            await entry.Handle.ActivateAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }

    private async Task<WindowRecord?> OpenLockedAsync(WindowSpec spec, CancellationToken ct)
    {
        if (_windows.Count >= MaxWindows
            || (spec.Kind == WindowKind.Main && _windows.Values.Any(e => e.Record.Kind == WindowKind.Main)))
        {
            return null;
        }

        var id = new WindowId($"w{Interlocked.Increment(ref _nextId)}");
        var bounds = MonitorFit(spec.Bounds ?? NextCascade());
        var record = new WindowRecord(id, spec.Kind, spec.Title, bounds, WindowState.Normal, MonitorIdFor(bounds), spec.Path, spec.Layout);
        var handle = await _host.OpenAsync(record, ct).ConfigureAwait(false);
        var entry = new Entry(record, handle, _windows.Count);
        entry.Attach(
            (_, b) => OnBounds(id, b),
            (_, _) => _ = CloseAsync(id, CancellationToken.None));
        _windows[id] = entry;
        RaiseChanged();
        return record;
    }

    private void OnBounds(WindowId id, WindowBounds bounds)
    {
        // Raised by the host on the UI thread; a missed update is harmless, so do not wait on the gate.
        if (_gate.Wait(0))
        {
            try
            {
                if (_windows.TryGetValue(id, out var entry))
                {
                    entry.Record = entry.Record with { Bounds = bounds, MonitorId = MonitorIdFor(bounds) };
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private WindowBounds NextCascade()
    {
        var step = (_cascade++ % 8) * 32;
        return new WindowBounds(80 + step, 80 + step, 1000, 700);
    }

    private WindowBounds MonitorFit(WindowBounds wanted) => MonitorClamp.Clamp(wanted, null, _monitors.Monitors);

    private string? MonitorIdFor(WindowBounds bounds)
    {
        MonitorInfo? best = null;
        long bestArea = 0;
        foreach (var monitor in _monitors.Monitors)
        {
            var area = Overlap(bounds, monitor.WorkArea);
            if (area > bestArea)
            {
                best = monitor;
                bestArea = area;
            }
        }

        return best?.Id ?? _monitors.Monitors.FirstOrDefault(m => m.IsPrimary)?.Id;
    }

    private static long Overlap(WindowBounds a, WindowBounds b)
    {
        long w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        long h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed class Entry(WindowRecord record, IWindowHandle handle, int order)
    {
        private EventHandler<WindowBounds>? _bounds;
        private EventHandler? _close;

        public WindowRecord Record { get; set; } = record;

        public IWindowHandle Handle { get; } = handle;

        public int Order { get; } = order;

        public void Attach(EventHandler<WindowBounds> bounds, EventHandler close)
        {
            _bounds = bounds;
            _close = close;
            Handle.BoundsChanged += bounds;
            Handle.CloseRequested += close;
        }

        public void Detach()
        {
            if (_bounds is not null)
            {
                Handle.BoundsChanged -= _bounds;
            }

            if (_close is not null)
            {
                Handle.CloseRequested -= _close;
            }
        }
    }
}
