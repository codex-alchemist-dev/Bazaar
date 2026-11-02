using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Bazaar.Client.Core.Panes;

/// <summary>Builds a plugin-specific body for one engine pane kind. Keyed by the pane kind.</summary>
public delegate Control EnginePaneRenderer(EnginePaneViewModel pane);

/// <summary>
/// Renders an engine pane as a titled panel. A kind with a registered renderer uses it; any other kind,
/// including an unknown or missing plugin renderer, gets the generic read-only field list.
/// Rebuilds when the view model reports a replaced snapshot while the view is attached.
/// </summary>
public sealed class EnginePaneView : UserControl
{
    private readonly EnginePaneViewModel _pane;
    private readonly IReadOnlyDictionary<string, EnginePaneRenderer> _renderers;

    public EnginePaneView(EnginePaneViewModel pane, IReadOnlyDictionary<string, EnginePaneRenderer>? renderers = null)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _renderers = renderers ?? new Dictionary<string, EnginePaneRenderer>();
        Rebuild();
    }

    public EnginePaneViewModel Pane => _pane;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _pane.PropertyChanged += OnPanePropertyChanged;
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pane.PropertyChanged -= OnPanePropertyChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        var body = _renderers.TryGetValue(_pane.Kind, out var renderer) ? renderer(_pane) : GenericBody(_pane);

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = _pane.Title, FontWeight = FontWeight.SemiBold, FontSize = 16 },
                body,
            },
        };
    }

    /// <summary>The generic fallback: one "name value" row per field, read-only.</summary>
    private static StackPanel GenericBody(EnginePaneViewModel pane)
    {
        var rows = new StackPanel { Spacing = 4 };
        if (pane.Fields.Count == 0)
        {
            rows.Children.Add(new TextBlock { Text = "No fields", Opacity = 0.7 });
        }

        foreach (var field in pane.Fields)
        {
            rows.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = field.Name, FontWeight = FontWeight.SemiBold, MinWidth = 120 },
                    new TextBlock { Text = field.Value, TextWrapping = TextWrapping.Wrap },
                },
            });
        }

        return rows;
    }
}
