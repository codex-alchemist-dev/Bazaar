using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Bazaar.Client.Constellation;
using Bazaar.Client.GitSolarSystem.Abstractions;

namespace Bazaar.Client.GitSolarSystem.Panes;

/// <summary>
/// The Git Solar System pane body. The accessible list mirror is the working surface: Up and Down step through it, Home and
/// End jump to its ends and Enter opens the selected commit. The mnemonic keys n and p resolve through
/// <see cref="ConstellationKeymap"/>. No key here is a Ctrl chord. The drawn solar system (<see cref="GitSolarCanvas"/>) sits in <c>PART_Canvas</c>; a click on a commit there selects it, and the list follows.
/// </summary>
public sealed class GitSolarPaneView : UserControl
{
    // Theme tokens (one theme file drives every head). Views bind to these, never to a fixed colour.
    private const string _secondaryTextToken = "Bazaar.brush.color.text.secondary";

    private readonly GitSolarPaneViewModel _pane;
    private readonly TextBlock _status;
    private readonly TextBlock _empty;
    private readonly ListBox _list;
    private readonly GitSolarCanvas _canvas;
    private IReadOnlyList<GitSolarListItem>? _rendered;
    private bool _rendering;

    public GitSolarPaneView(GitSolarPaneViewModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        _pane = pane;

        _status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        SetThemeBrush(_status, TextBlock.ForegroundProperty, _secondaryTextToken);

        _empty = new TextBlock { Text = "No commits to show yet.", TextWrapping = TextWrapping.Wrap };
        SetThemeBrush(_empty, TextBlock.ForegroundProperty, _secondaryTextToken);

        _list = new ListBox();
        AutomationProperties.SetName(_list, "Commits, newest first");
        _list.SelectionChanged += OnSelectionChanged;

        _canvas = new GitSolarCanvas { Height = 360 };
        _canvas.NodeClicked += sha => _pane.Handle(GitSolarCommands.FocusCommit, sha);
        var canvasHost = new ContentControl { Name = "PART_Canvas", Content = _canvas };
        Content = new StackPanel
        {
            Spacing = 8,
            Children = { _status, _empty, _list, canvasHost },
        };

        // Tunnelling runs before the list's own key handling, so a handled key moves only the pane's selection.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _pane.PropertyChanged += OnPanePropertyChanged;
        Render();
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The feed raises changes on its own thread, so the rebuild runs on the UI thread.
        if (Dispatcher.UIThread.CheckAccess())
        {
            Render();
        }
        else
        {
            Dispatcher.UIThread.Post(Render);
        }
    }

    /// <summary>Brings the controls in line with the view model. Rebuilds the rows only when the commit list changed, so focus stays on the list while stepping.</summary>
    private void Render()
    {
        _rendering = true;
        try
        {
            _status.Text = _pane.StatusText;

            var items = _pane.Items;
            if (!ReferenceEquals(items, _rendered))
            {
                _rendered = items;
                _list.Items.Clear();
                foreach (var item in items)
                {
                    var row = new ListBoxItem
                    {
                        Content = new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap },
                    };
                    AutomationProperties.SetName(row, item.Text);
                    _list.Items.Add(row);
                }
            }

            _empty.IsVisible = items.Count == 0;
            _list.IsVisible = items.Count > 0;
            _list.SelectedIndex = _pane.SelectedIndex;
            _canvas.Load(_pane.Scene, _pane.LatestFrame, _pane.SelectedSha, _pane.IsStale);
        }
        finally
        {
            _rendering = false;
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_rendering)
        {
            return;
        }

        _pane.SelectIndex(_list.SelectedIndex);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (HandleKey(e))
        {
            e.Handled = true;
        }
    }

    private bool HandleKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _pane.MoveNext();
                return true;
            case Key.Up:
                _pane.MovePrevious();
                return true;
            case Key.Home:
                _pane.MoveFirst();
                return true;
            case Key.End:
                _pane.MoveLast();
                return true;
            case Key.Enter:
                _pane.OpenSelected();
                return true;
        }

        if (e.KeySymbol is { Length: 1 } symbol && ConstellationKeymap.TryResolve(symbol, out var command))
        {
            switch (command)
            {
                case ConstellationCommandKind.NextNeighbour:
                    _pane.MoveNext();
                    return true;
                case ConstellationCommandKind.PreviousNeighbour:
                    _pane.MovePrevious();
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sets a brush property from a theme token. A token the theme does not define falls back to transparent,
    /// so the view still renders.
    /// </summary>
    private static void SetThemeBrush(AvaloniaObject target, AvaloniaProperty property, string token)
    {
        var app = Application.Current;
        var brush = app is not null && app.TryGetResource(token, app.ActualThemeVariant, out var value) && value is IBrush found
            ? found
            : Brushes.Transparent;
        target.SetValue(property, brush);
    }
}
