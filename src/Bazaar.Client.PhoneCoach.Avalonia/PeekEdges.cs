using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.PhoneCoach.Avalonia;

/// <summary>
/// The peek affordance (doc 93): a few dp of the neighbouring page showing at the pager's left and right edge, so the swipe is visible before
/// the finger moves. It sits over the pager, ignores touches (the swipe layer under it owns them), and shows an edge only where a neighbour
/// exists. It is decoration, hidden from screen readers; the actions and the strip carry the meaning.
/// </summary>
public sealed class PeekEdges : Grid
{
    private readonly Border _left;
    private readonly Border _right;

    public PeekEdges(PhoneCoachOptions? options = null)
    {
        var o = options ?? PhoneCoachOptions.Default;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        _left = Edge(o, HorizontalAlignment.Left);
        _right = Edge(o, HorizontalAlignment.Right);
        Children.Add(_left);
        Children.Add(_right);
    }

    /// <summary>True when the left edge (a previous page) is shown.</summary>
    public bool LeftVisible => _left.IsVisible;

    /// <summary>True when the right edge (a next page) is shown.</summary>
    public bool RightVisible => _right.IsVisible;

    /// <summary>Shows an edge for each neighbour <paramref name="state"/> has.</summary>
    public void Update(ChatStripState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _left.IsVisible = state.Previous is not null;
        _right.IsVisible = state.Next is not null;
    }

    private static Border Edge(PhoneCoachOptions o, HorizontalAlignment side) => new()
    {
        Width = o.PeekWidthDp,
        HorizontalAlignment = side,
        Background = ThemeTokens.Brush(PhoneCoachOptions.AccentToken, Brushes.Gray),
        Opacity = o.MutedOpacity,
        CornerRadius = side == HorizontalAlignment.Left ? new(0, o.PeekWidthDp, o.PeekWidthDp, 0) : new(o.PeekWidthDp, 0, 0, o.PeekWidthDp),
        IsVisible = false,
    };
}
