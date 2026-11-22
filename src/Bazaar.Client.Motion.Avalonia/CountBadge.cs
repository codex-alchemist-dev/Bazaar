using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Bazaar.Client.Motion.Avalonia;

/// <summary>
/// An unread-count badge (doc 93 "Unread badge and counts"). It shows the count as text, so meaning never rests on colour, hides at
/// zero, and plays <see cref="BadgeMotion"/> while it is in a window: a pop when the count appears, a bump when it grows.
/// </summary>
public sealed class CountBadge : Border
{
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };
    private BadgeMotion? _motion;

    /// <summary>The count to show. Zero or less hides the badge.</summary>
    public static readonly StyledProperty<int> CountProperty = AvaloniaProperty.Register<CountBadge, int>(nameof(Count));

    public CountBadge()
    {
        Child = _label;
        IsVisible = false;
        HorizontalAlignment = HorizontalAlignment.Left;
        ThemeBinding.BindBrush(this, BackgroundProperty, "color.badge.owner.background");
        ThemeBinding.BindBrush(_label, TextBlock.ForegroundProperty, "color.badge.owner.text");
        ThemeBinding.BindRadius(this, "radius.md");
        ThemeBinding.BindPadding(this, "space.sm", "space.xs");
    }

    public int Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    /// <summary>The badge's count text, empty at zero.</summary>
    public string? Text => _label.Text;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The text at attach time is the baseline, so a badge that loads with a count does not pop.
        _motion ??= BadgeMotion.Attach(this, _label);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _motion?.Dispose();
        _motion = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != CountProperty) return;
        var count = Count;
        _label.Text = count > 0 ? count.ToString(CultureInfo.CurrentCulture) : string.Empty;
        IsVisible = count > 0;
    }
}
