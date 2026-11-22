using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Bazaar.Client.Devices;

/// <summary>
/// Renders the Devices pane: providers with their probe result, capability flags and load, then the running guests.
/// With no enabled provider the view is empty and not visible. Rebuilds when the view model reports a new list.
/// </summary>
public sealed class DevicesPaneView : UserControl
{
    private readonly DevicesPaneViewModel _pane;

    public DevicesPaneView(DevicesPaneViewModel pane)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        Rebuild();
    }

    public DevicesPaneViewModel Pane => _pane;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _pane.PropertyChanged += OnPanePropertyChanged;
        Rebuild();
        _pane.StartRefresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pane.PropertyChanged -= OnPanePropertyChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold };

    private static StackPanel Row(string name, params string[] details)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, MinWidth = 120 });
        foreach (var detail in details)
        {
            row.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap });
        }

        return row;
    }

    // A refresh finishes on a background thread, so the rebuild is posted to the UI thread.
    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Rebuild();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        }
    }

    private void Rebuild()
    {
        IsVisible = _pane.IsOffered;
        if (!_pane.IsOffered)
        {
            Content = null;
            return;
        }

        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(new TextBlock { Text = _pane.Title, FontWeight = FontWeight.SemiBold, FontSize = 16 });
        root.Children.Add(Heading("Providers"));
        foreach (var provider in _pane.Providers)
        {
            root.Children.Add(Row(provider.Name, provider.Status, provider.Capabilities, provider.Load));
            if (provider.RestartNote is { } note)
            {
                root.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyle.Italic, Margin = new Thickness(128, 0, 0, 0) });
            }
        }

        root.Children.Add(Heading("Running guests"));
        if (_pane.Guests.Count == 0)
        {
            root.Children.Add(new TextBlock { Text = "No running guests", Opacity = 0.7 });
        }

        foreach (var guest in _pane.Guests)
        {
            root.Children.Add(Row(guest.Name, guest.State, guest.Id));
        }

        Content = root;
    }
}
