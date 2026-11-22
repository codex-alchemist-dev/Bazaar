using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Kodo;

/// <summary>
/// The Fleet view (doc 46 section 14.2, view 7): a sort selector and one focusable list row per device. It draws a
/// <see cref="KodoFleetPanel"/> and holds no logic. The owner calls <see cref="Refresh"/> after the device list or the clock moves.
/// </summary>
public sealed class KodoFleetView : UserControl
{
    private static readonly KodoFleetSort[] _sorts = [KodoFleetSort.Attention, KodoFleetSort.Name, KodoFleetSort.LastSeen, KodoFleetSort.CrashRate];

    private readonly KodoFleetPaneViewModel _model;
    private readonly ComboBox _sortBox = new() { ItemsSource = new[] { "Needs attention first", "Device name", "Last seen", "Crash rate" } };
    private readonly ListBox _list = new();
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap };
    private bool _refreshing;

    /// <summary>Builds the view and renders the current panel.</summary>
    public KodoFleetView(KodoFleetPaneViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        KodoViewParts.Name(_sortBox, "Sort fleet by");
        KodoViewParts.Name(_list, "Fleet devices");
        _sortBox.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _sortBox.SelectedIndex >= 0)
            {
                _model.Sort = _sorts[_sortBox.SelectedIndex];
                Refresh();
            }
        };

        var root = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        root.Children.Add(_sortBox);
        root.Children.Add(_empty);
        root.Children.Add(_list);
        Content = root;
        Refresh();
    }

    /// <summary>Redraws from the view model.</summary>
    public void Refresh()
    {
        _refreshing = true;
        try
        {
            var panel = _model.Build();
            _sortBox.IsEnabled = panel.Permitted;
            _sortBox.SelectedIndex = Array.IndexOf(_sorts, panel.Sort);
            _empty.Text = panel.EmptyText;
            _empty.IsVisible = panel.EmptyText.Length > 0;
            _list.IsVisible = panel.Rows.Count > 0;
            _list.ItemsSource = panel.Rows.Select(Row).ToList();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static ListBoxItem Row(KodoFleetRow row)
    {
        var secondary = KodoViewParts.Brush(KodoViewParts.SecondaryTextToken);
        var stack = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = row.DeviceName, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = row.VersionText, Foreground = secondary },
                new TextBlock { Text = $"{row.LastSeenText} · {row.CrashRateText}", Foreground = secondary },
                new TextBlock { Text = row.StreamText, Foreground = secondary },
            },
        };
        var item = new ListBoxItem { Content = stack, Focusable = true };
        KodoViewParts.Name(item, row.AccessibleName);
        return item;
    }
}
