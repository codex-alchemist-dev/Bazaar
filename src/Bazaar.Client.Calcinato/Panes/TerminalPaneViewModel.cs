using System.ComponentModel;
using System.Text;
using Bazaar.Client.Calcinato.Emulation;
using Bazaar.Client.Calcinato.Modal;
using Bazaar.Client.Calcinato.Session;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Calcinato.Panes;

/// <summary>
/// The terminal pane's data (doc 55, section 5.4). It follows the active session of a
/// <see cref="TerminalSessionManager"/>, publishes an immutable <see cref="Snapshot"/> for the renderer, and keeps an
/// accessible text model that updates at most <see cref="AccessibleUpdatesPerSecond"/> times a second. It has no
/// Avalonia types; the head draws it.
/// </summary>
/// <remarks>
/// Property changes are raised on the thread that changed the terminal (the output pump for output). The head marshals
/// them to its UI thread. Accessible-text updates that arrive too soon are published by a timer from
/// <see cref="TimeProvider.CreateTimer"/>, so the last state is never lost.
/// </remarks>
public sealed class TerminalPaneViewModel : IPaneViewModel, INotifyPropertyChanged, IDisposable
{
    /// <summary>The most accessible-text updates per second (doc 55, section 5.2).</summary>
    public const int AccessibleUpdatesPerSecond = 4;

    private static readonly TimeSpan _accessibleInterval = TimeSpan.FromMilliseconds(1000d / AccessibleUpdatesPerSecond);

    private readonly object _syncRoot = new();
    private readonly TerminalSessionManager? _manager;
    private readonly TimeProvider _timeProvider;
    private ITimer? _accessibleTimer;
    private long _lastPublish;
    private bool _published;
    private bool _disposed;

    /// <summary>Creates the pane data.</summary>
    /// <param name="title">Pane heading. Empty uses "Terminal".</param>
    /// <param name="manager">The session manager to follow. Null shows the empty state.</param>
    /// <param name="timeProvider">Clock for the accessible-text rate limit. Null uses the system clock.</param>
    public TerminalPaneViewModel(string title, TerminalSessionManager? manager, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        Title = title.Length == 0 ? "Terminal" : title;
        _manager = manager;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Snapshot = TerminalPaneSnapshot.Empty;
        Accessible = TerminalAccessibleText.Empty;
        if (_manager is not null)
        {
            _manager.Changed += OnManagerChanged;
        }

        Refresh();
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    public string Kind => PaneKinds.Terminal;

    /// <inheritdoc />
    public string Title { get; }

    /// <summary>The session manager this pane follows, or null for the empty state. The head's control sends input through it.</summary>
    public TerminalSessionManager? Manager => _manager;

    /// <summary>What the renderer draws. Replaced on every change, never mutated.</summary>
    public TerminalPaneSnapshot Snapshot { get; private set; }

    /// <summary>The screen-reader text model. Updated at most <see cref="AccessibleUpdatesPerSecond"/> times a second.</summary>
    public TerminalAccessibleText Accessible { get; private set; }

    /// <summary>Stops following the manager and cancels any pending accessible update.</summary>
    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _accessibleTimer?.Dispose();
            _accessibleTimer = null;
        }

        if (_manager is not null)
        {
            _manager.Changed -= OnManagerChanged;
        }
    }

    private void OnManagerChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        bool accessibleChanged;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            TerminalSession? session = _manager?.Active;
            Snapshot = session is null ? TerminalPaneSnapshot.Empty : BuildSnapshot(session);
            accessibleChanged = PublishAccessibleLocked(session);
        }

        Raise(nameof(Snapshot));
        if (accessibleChanged)
        {
            Raise(nameof(Accessible));
        }
    }

    private void OnAccessibleTimer(object? state)
    {
        bool published;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _accessibleTimer?.Dispose();
            _accessibleTimer = null;
            published = PublishAccessibleLocked(_manager?.Active);
        }

        if (published)
        {
            Raise(nameof(Accessible));
        }
    }

    // Caller holds _syncRoot. Publishes now when the interval has passed; otherwise one timer publishes the latest state later.
    private bool PublishAccessibleLocked(TerminalSession? session)
    {
        if (_published)
        {
            TimeSpan elapsed = _timeProvider.GetElapsedTime(_lastPublish);
            if (elapsed < _accessibleInterval)
            {
                _accessibleTimer ??= _timeProvider.CreateTimer(
                    OnAccessibleTimer,
                    null,
                    _accessibleInterval - elapsed,
                    Timeout.InfiniteTimeSpan);
                return false;
            }
        }

        Accessible = session is null ? TerminalAccessibleText.Empty : BuildAccessible(session);
        _lastPublish = _timeProvider.GetTimestamp();
        _published = true;
        return true;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static TerminalPaneSnapshot BuildSnapshot(TerminalSession session)
    {
        lock (session.SyncRoot)
        {
            ModalEngine engine = session.Engine;
            Emulator emulator = session.Emulator;
            int top = engine.TopRow;
            var rows = new List<TerminalRow>(emulator.Rows);
            for (int row = 0; row < emulator.Rows; row++)
            {
                rows.Add(BuildRow(emulator, top + row));
            }

            int cursorRow = engine.CursorRow - top;
            bool onScreen = cursorRow >= 0 && cursorRow < emulator.Rows;
            return new TerminalPaneSnapshot(
                HasSession: true,
                Rows: rows,
                CursorRow: onScreen ? cursorRow : -1,
                CursorColumn: engine.CursorColumn,
                LiveCursorRow: emulator.CursorRow,
                LiveCursorColumn: emulator.CursorCol,
                CursorVisible: emulator.CursorVisible,
                ModeName: ModeName(engine.Mode),
                CommandLine: engine.Mode == ModalMode.CommandLine ? engine.CommandPrompt + engine.CommandText : string.Empty,
                StatusLine: session.StatusText,
                Title: emulator.Title,
                IsReadOnly: session.IsReadOnly,
                IsReplaying: session.IsReplaying,
                ExitCode: session.ExitCode,
                SelectionAnchorRow: engine.AnchorRow - top,
                SelectionAnchorColumn: engine.AnchorColumn);
        }
    }

    private static TerminalAccessibleText BuildAccessible(TerminalSession session)
    {
        lock (session.SyncRoot)
        {
            ModalEngine engine = session.Engine;
            Emulator emulator = session.Emulator;
            int top = engine.TopRow;
            var lines = new List<string>(emulator.Rows);
            for (int row = 0; row < emulator.Rows; row++)
            {
                lines.Add(emulator.GetLineText(top + row));
            }

            int cursorRow = engine.CursorRow - top;
            bool onScreen = cursorRow >= 0 && cursorRow < emulator.Rows;
            return new TerminalAccessibleText(lines, onScreen ? cursorRow : -1, emulator.GetLineText(engine.CursorRow));
        }
    }

    private static TerminalRow BuildRow(Emulator emulator, int absoluteRow)
    {
        var runs = new List<TerminalCellRun>();
        var text = new StringBuilder();
        CellStyle? current = null;
        for (int col = 0; col < emulator.Cols; col++)
        {
            Cell cell = emulator.GetCell(absoluteRow, col);
            if (cell.IsTrailingHalf)
            {
                continue;
            }

            if (current is not null && current.Value != cell.Style)
            {
                runs.Add(new TerminalCellRun(text.ToString(), current.Value));
                text.Clear();
            }

            current = cell.Style;
            text.Append(cell.Text.Length == 0 ? " " : cell.Text);
        }

        if (current is not null)
        {
            runs.Add(new TerminalCellRun(text.ToString(), current.Value));
        }

        return new TerminalRow(runs);
    }

    private static string ModeName(ModalMode mode) => mode switch
    {
        ModalMode.Normal => "NORMAL",
        ModalMode.Insert => "INSERT",
        ModalMode.VisualChar => "VISUAL",
        ModalMode.VisualLine => "V-LINE",
        ModalMode.CommandLine => "COMMAND",
        _ => mode.ToString(),
    };
}
