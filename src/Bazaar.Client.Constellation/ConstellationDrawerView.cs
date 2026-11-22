using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Bazaar.Client.Constellation;

/// <summary>
/// The drawer's body: a status line, one button per listed node, and a detail line for the opened node. It has no editing
/// controls. Node buttons are plain buttons, so the list works for screen readers and keyboard users without a template.
/// </summary>
public sealed class ConstellationDrawerView : UserControl
{
    private readonly ConstellationDrawerViewModel _model;
    private readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "drawer-status" } };
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly Border _detail;
    private readonly TextBlock _detailText = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "drawer-detail" } };

    public ConstellationDrawerView(ConstellationDrawerViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;

        var close = new Button { Content = "Close", Classes = { "drawer-close" } };
        AutomationProperties.SetName(close, "Close node details");
        close.Click += (_, _) => _model.Close();

        _detail = new Border
        {
            Padding = new Avalonia.Thickness(8),
            IsVisible = false,
            Child = new StackPanel { Spacing = 6, Children = { _detailText, close } },
        };

        Content = new ScrollViewer
        {
            Content = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(8), Children = { _status, _detail, _list } },
        };

        _model.PropertyChanged += OnModelChanged;
        Rebuild();
    }

    /// <summary>The status line text, for tests and screen readers.</summary>
    public string StatusText => _status.Text ?? string.Empty;

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Rebuild();
        }
        else
        {
            Dispatcher.UIThread.Post(Rebuild);
        }
    }

    private void Rebuild()
    {
        _status.Text = StatusFor(_model.Status, _model.Nodes.Count);

        _list.Children.Clear();
        if (_model.Status == DrawerStatus.Ready)
        {
            foreach (var node in _model.Nodes)
            {
                var id = node.Id;
                var button = new Button
                {
                    Content = $"{id}  ({node.X:0.#}, {node.Y:0.#})",
                    Tag = id,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Classes = { "drawer-node" },
                };
                AutomationProperties.SetName(button, $"Open node {id}");
                button.Click += (_, _) => _model.OpenNode(id);
                _list.Children.Add(button);
            }
        }

        var opened = _model.OpenedNode;
        _detail.IsVisible = opened is not null;
        if (opened is not null)
        {
            _detailText.Text = $"Node {opened.Id} at x {opened.X:0.##}, y {opened.Y:0.##} (graph coordinates)";
        }
    }

    private static string StatusFor(DrawerStatus status, int count) => status switch
    {
        DrawerStatus.NoGraph => "Waiting for the graph from the server.",
        DrawerStatus.ClustersOnly => "The graph is above the node cap. Nodes are not listed here.",
        DrawerStatus.Empty => "No nodes in this area.",
        _ => count == 1 ? "1 node in this area." : $"{count} nodes in this area.",
    };
}
