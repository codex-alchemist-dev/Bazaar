using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Bazaar.Client.Tui.TerminalGui;

/// <summary>
/// The screen under the installer's question dialogs (doc 58 work package 3): a title row, a progress line, a scrolling log of the
/// wizard's messages and the report, and a status row. Everything on it is text the caller passed in, so a secret can never appear:
/// the secret widget draws only into its own masked dialog. Not thread-safe; <see cref="InstallerScreen"/> calls it on the loop thread.
/// </summary>
internal sealed class InstallerBackdrop : Runnable
{
    private const int _maxLogLines = 500;
    private const int _fallbackWidth = 76;
    private const int _fallbackRows = 14;

    private readonly Label _title;
    private readonly Label _progress;
    private readonly Label _log;
    private readonly Label _status;
    private readonly List<string> _lines = [];

    private readonly IApplication _application;
    private bool _closable;

    public InstallerBackdrop(IApplication application, string title)
    {
        _application = application;
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        SetScheme(FixedLook.Body);

        _title = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, Text = " " + title };
        _title.SetScheme(FixedLook.Bar);
        _progress = new Label { X = 0, Y = 1, Width = Dim.Fill(), Height = 1, Text = "" };
        _log = new Label { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(1), Text = "" };
        _status = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, Text = " Answer each question. Esc or Ctrl+C stops before anything is written." };
        _status.SetScheme(FixedLook.Bar);
        Add(_title, _progress, _log, _status);
        KeyDown += OnKeyDown;
    }

    /// <summary>The log as drawn: the tail that fits, wrapped to the width. For tests and for the plain-text view of the screen.</summary>
    public string LogText => _log.Text;

    public string ProgressLine => _progress.Text;

    public string StatusText => _status.Text;

    public void Append(string line, TuiMessageKind kind)
    {
        // The kind is a word, so colour is never the only signal.
        var text = kind switch
        {
            TuiMessageKind.Error => "Error: " + line,
            TuiMessageKind.Warning => "Warning: " + line,
            _ => line,
        };
        _lines.Add(text);
        if (_lines.Count > _maxLogLines)
        {
            _lines.RemoveRange(0, _lines.Count - _maxLogLines);
        }

        RefreshLog();
    }

    public void SetProgress(TuiProgress progress)
    {
        if (progress.Total <= 0)
        {
            return;
        }

        _progress.Text = " " + ProgressText.Line(progress, 24);
        SetNeedsDraw();
    }

    /// <summary>Marks the work finished: the status row says so, and Enter, Esc or Ctrl+C now close the screen.</summary>
    public void Finish(string status)
    {
        _closable = true;
        _status.Text = " " + status;
        SetNeedsDraw();
    }

    private void RefreshLog()
    {
        var width = _log.Viewport.Width > 0 ? _log.Viewport.Width : _fallbackWidth;
        var rows = _log.Viewport.Height > 0 ? _log.Viewport.Height : _fallbackRows;
        var wrapped = new List<string>();
        foreach (var line in _lines)
        {
            Wrap(line, width, wrapped);
        }

        _log.Text = string.Join('\n', wrapped.Skip(Math.Max(0, wrapped.Count - rows)));
        SetNeedsDraw();
    }

    private static void Wrap(string line, int width, List<string> into)
    {
        if (line.Length <= width)
        {
            into.Add(line);
            return;
        }

        for (var start = 0; start < line.Length; start += width)
        {
            into.Add(line.Substring(start, Math.Min(width, line.Length - start)));
        }
    }

    private void OnKeyDown(object? sender, Key key)
    {
        if (!_closable)
        {
            return;
        }

        if (KeyTranslator.Translate(key) is { Kind: TerminalKeyKind.Enter or TerminalKeyKind.Escape or TerminalKeyKind.Cancel })
        {
            key.Handled = true;
            _application.RequestStop(this);
        }
    }
}
