using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Bazaar.Client.Constellation;

/// <summary>
/// Puts the Constellation drawer under the phone's content as a bottom drawer (doc 29, Layout, Unfolded). The head sets
/// <see cref="IsDrawerAvailable"/> from the posture: it is false when folded, which hides the drawer entirely. When available,
/// the drawer starts collapsed and a handle button toggles it. The host never refreshes the list; the head does that.
/// </summary>
public sealed class ConstellationDrawerHost : Grid
{
    private readonly ConstellationDrawerView _view;
    private readonly Button _handle;
    private readonly StackPanel _drawer;
    private bool _available;
    private bool _expanded;

    /// <param name="content">The phone shell or any other content that stays on screen above the drawer.</param>
    /// <param name="model">The drawer's view model. It is shared with the head, which calls its refresh.</param>
    public ConstellationDrawerHost(Control content, ConstellationDrawerViewModel model)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(model);

        _view = new ConstellationDrawerView(model) { MaxHeight = 360 };
        _handle = new Button { Content = "Constellation", HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "drawer-handle" } };
        AutomationProperties.SetName(_handle, "Show or hide the Constellation drawer");
        _handle.Click += (_, _) => IsExpanded = !IsExpanded;

        _drawer = new StackPanel { Children = { _handle, _view } };
        RowDefinitions = new RowDefinitions("*,Auto");
        Children.Add(content);
        Children.Add(_drawer);
        SetRow(content, 0);
        SetRow(_drawer, 1);

        ApplyVisibility();
    }

    /// <summary>True when the layout shows the drawer (unfolded). False hides the drawer and its handle.</summary>
    public bool IsDrawerAvailable
    {
        get => _available;
        set
        {
            _available = value;
            ApplyVisibility();
        }
    }

    /// <summary>True when the drawer body is open. It only has an effect while the drawer is available.</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            ApplyVisibility();
        }
    }

    private void ApplyVisibility()
    {
        _drawer.IsVisible = _available;
        _view.IsVisible = _available && _expanded;
        _handle.Content = _expanded ? "Hide Constellation" : "Constellation";
    }
}
