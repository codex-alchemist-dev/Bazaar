using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;

namespace Bazaar.Client.Motion.Avalonia;

/// <summary>
/// Plays "Reaction chip added" (doc 93) on chips that join a panel after <see cref="Attach"/>; the chips already in the panel at
/// attach time, and chips a rebuilt panel gets back, are not animated.
/// </summary>
public sealed class ReactionChipsMotion : IDisposable
{
    private readonly Panel _chips;
    private readonly MotionSettings _settings;
    private readonly Func<Control, IFrameDriver> _driverFactory;

    private ReactionChipsMotion(Panel chips, MotionSettings settings, Func<Control, IFrameDriver> driverFactory)
    {
        _chips = chips;
        _settings = settings;
        _driverFactory = driverFactory;
        chips.Children.CollectionChanged += OnChanged;
    }

    public static ReactionChipsMotion Attach(Panel chips, MotionSettings? settings = null, Func<Control, IFrameDriver>? driverFactory = null)
    {
        ArgumentNullException.ThrowIfNull(chips);
        return new ReactionChipsMotion(chips, settings ?? MotionHost.Settings, driverFactory ?? MotionHost.DriverFor);
    }

    public void Dispose() => _chips.Children.CollectionChanged -= OnChanged;

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
        {
            return;
        }

        foreach (var chip in e.NewItems.OfType<Control>())
        {
            var animator = new ReactionPopAnimator(chip, _settings, _driverFactory(chip));
            animator.Completed += (_, _) => animator.Dispose();
            animator.Play(MotionHost.ReactionOriginDx, 0);
        }
    }
}

/// <summary>
/// Plays "Unread badge and counts" (doc 93) when the badge's count text changes: pop in when it goes from none (empty or "0") to a
/// count, a bump when it changes from one count to another. The text at attach time is the baseline and does not animate.
/// </summary>
public sealed class BadgeMotion : IDisposable
{
    private readonly TextBlock _label;
    private readonly BadgeBumpAnimator _animator;
    private string? _last;

    private BadgeMotion(Control badge, TextBlock label, MotionSettings settings, Func<Control, IFrameDriver> driverFactory)
    {
        _label = label;
        _animator = new BadgeBumpAnimator(badge, settings, driverFactory(badge));
        _last = label.Text;
        label.PropertyChanged += OnPropertyChanged;
    }

    public static BadgeMotion Attach(Control badge, TextBlock label, MotionSettings? settings = null, Func<Control, IFrameDriver>? driverFactory = null)
    {
        ArgumentNullException.ThrowIfNull(badge);
        ArgumentNullException.ThrowIfNull(label);
        return new BadgeMotion(badge, label, settings ?? MotionHost.Settings, driverFactory ?? MotionHost.DriverFor);
    }

    public void Dispose()
    {
        _label.PropertyChanged -= OnPropertyChanged;
        _animator.Dispose();
    }

    private static bool IsNone(string? text) => string.IsNullOrEmpty(text) || text == "0";

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TextBlock.TextProperty)
        {
            return;
        }

        var now = _label.Text;
        var before = _last;
        _last = now;
        if (IsNone(now) || now == before)
        {
            return;
        }

        if (IsNone(before))
        {
            _animator.Appear();
        }
        else
        {
            _animator.Bump();
        }
    }
}

/// <summary>
/// The three typing dots (doc 93 "Typing indicator"). They loop while the control is in a window and visible, and are still otherwise,
/// so nothing runs at rest. A view puts this control where the indicator goes and shows or hides it.
/// </summary>
public sealed class TypingDots : StackPanel
{
    private const double DotSize = 6;
    private const double Gap = 4;

    private readonly TypingIndicatorAnimator _animator;

    public TypingDots() : this(null, null)
    {
    }

    public TypingDots(MotionSettings? settings, Func<Control, IFrameDriver>? driverFactory)
    {
        Orientation = Orientation.Horizontal;
        Spacing = Gap;
        var dots = new List<Control>();
        for (var i = 0; i < TypingIndicatorPlan.DotCount; i++)
        {
            var dot = new Ellipse { Width = DotSize, Height = DotSize };
            dot.Bind(Shape.FillProperty, dot.GetResourceObservable("SystemControlForegroundBaseMediumBrush"));
            dots.Add(dot);
            Children.Add(dot);
        }

        _animator = new TypingIndicatorAnimator(dots, settings ?? MotionHost.Settings, (driverFactory ?? MotionHost.DriverFor)(this), DotSize + Gap);
    }

    /// <summary>True while the dots loop.</summary>
    public bool IsLooping => _animator.IsRunning;

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Sync();
    }

    protected override void OnDetachedFromVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _animator.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            Sync();
        }
    }

    private void Sync()
    {
        if (IsVisible && TopLevel.GetTopLevel(this) is not null)
        {
            _animator.Start();
        }
        else
        {
            _animator.Stop();
        }
    }
}
