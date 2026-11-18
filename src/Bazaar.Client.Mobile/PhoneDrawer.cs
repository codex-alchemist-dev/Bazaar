using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Mobile;

/// <summary>Which pane the phone shell shows in its bottom drawer, and the title on the drawer's handle.</summary>
/// <param name="Kind">The pane kind the shell asks its pane-body factory for.</param>
/// <param name="Title">The handle's text, also its accessible name.</param>
/// <param name="Body">
/// Builds the drawer's body. Null asks the shell's pane-body factory for <paramref name="Kind"/> instead, and a null
/// result there shows the kind-name placeholder.
/// </param>
public sealed record PhoneDrawerSpec(string Kind, string Title, Func<Control?>? Body = null);

/// <summary>
/// The bottom drawer of the unfolded layout (doc 29, layout table). It holds one pane body, read-only first: it
/// offers no sketch or edit action, only a handle that opens and closes it. The drawer keeps its open state while
/// the shell rebuilds around it, for example across a fold change.
/// </summary>
public sealed class PhoneDrawer : UserControl
{
    /// <summary>Height of the open drawer body, in device-independent pixels.</summary>
    public const double OpenHeightDp = 240;

    private readonly Button _handle;
    private readonly Border _body;

    public PhoneDrawer(PhoneDrawerSpec spec, Control body)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(body);
        Kind = spec.Kind;
        Title = spec.Title;

        _handle = new Button
        {
            Content = Title,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(8, 4),
        };
        AutomationProperties.SetName(_handle, Title);
        _handle.Click += (_, _) => Toggle();

        _body = new Border
        {
            Height = OpenHeightDp,
            Margin = new Thickness(8, 0, 8, 8),
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            CornerRadius = new CornerRadius(4),
            Child = body,
            IsVisible = false,
        };
        AutomationProperties.SetName(_body, Title);

        var panel = new DockPanel();
        DockPanel.SetDock(_handle, Dock.Top);
        panel.Children.Add(_handle);
        panel.Children.Add(_body);
        Content = panel;
    }

    /// <summary>The pane kind this drawer shows.</summary>
    public string Kind { get; }

    /// <summary>The handle's text.</summary>
    public string Title { get; }

    /// <summary>Always true for now: the drawer shows its pane and offers no edit action (doc 29, read-only first).</summary>
    public bool IsReadOnly { get; } = true;

    /// <summary>True while the body is showing.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The pane body the drawer holds.</summary>
    public Control Body => _body.Child!;

    public void Open() => SetOpen(true);

    public void Close() => SetOpen(false);

    public void Toggle() => SetOpen(!IsOpen);

    private void SetOpen(bool open)
    {
        IsOpen = open;
        _body.IsVisible = open;
    }
}
