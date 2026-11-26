using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.TimeMachine;

/// <summary>
/// The scrubber body. The control names are public so heads and tests can find them. Builds the body. Controls are created once and updated on <see cref="ReplayScrubberViewModel.Changed"/>, so a
/// drag on the slider is never interrupted by a rebuild. Colours come from theme tokens only.
/// </summary>
public static class ReplayScrubberView
{
    /// <summary>Control names the Headless tests look up.</summary>
    public const string BannerName = "ReplayBanner";
    public const string SliderName = "ReplaySlider";
    public const string CursorTextName = "ReplayCursorText";
    public const string MessagesName = "ReplayMessages";
    public const string PartsName = "ReplayParts";
    public const string BackName = "ReplayBack";
    public const string ForwardName = "ReplayForward";
    public const string PlayName = "ReplayPlay";
    public const string ForkName = "ReplayFork";
    public const string ModeName = "ReplayMode";
    public const string ErrorName = "ReplayError";
    public const string NoticeName = "ReplayNotice";
    public const string PointsName = "ReplayPoints";
    public const string MorePointsName = "ReplayMorePoints";

    private const string _borderToken = "Bazaar.brush.color.border.default";
    private const string _secondaryTextToken = "Bazaar.brush.color.text.secondary";
    private const string _surfaceToken = "Bazaar.brush.color.background.surface";
    private const string _accentTextToken = "Bazaar.brush.color.text.accent";
    private static readonly TimeSpan _playInterval = TimeSpan.FromMilliseconds(600);

    internal static Control Build(ReplayScrubberViewModel vm)
    {
        var banner = new TextBlock { Name = BannerName, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        SetThemeBrush(banner, TextBlock.ForegroundProperty, _accentTextToken);
        var bannerBox = new Border
        {
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = banner,
        };
        SetThemeBrush(bannerBox, Border.BorderBrushProperty, _borderToken);
        SetThemeBrush(bannerBox, Border.BackgroundProperty, _surfaceToken);
        AutomationProperties.SetName(banner, "Read-only history banner");

        var cursorText = new TextBlock { Name = CursorTextName };
        var slider = new Slider { Name = SliderName, Minimum = 0, SmallChange = 1, LargeChange = 10, IsSnapToTickEnabled = true, TickFrequency = 1 };
        AutomationProperties.SetName(slider, "Replay position");

        var back = MakeButton(BackName, "Step back", "Step back one seq");
        var forward = MakeButton(ForwardName, "Step forward", "Step forward one seq");
        var play = MakeButton(PlayName, "Play", "Play history forward");
        var fork = MakeButton(ForkName, "Fork from here", "Fork a new project GC from this seq");
        var mode = MakeButton(ModeName, string.Empty, "Switch between the current and history view");
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { back, play, forward, fork, mode },
        };

        var error = new TextBlock { Name = ErrorName, TextWrapping = TextWrapping.Wrap };
        var notice = new TextBlock { Name = NoticeName, TextWrapping = TextWrapping.Wrap };
        SetThemeBrush(notice, TextBlock.ForegroundProperty, _secondaryTextToken);
        var messages = new StackPanel { Name = MessagesName, Spacing = 4 };
        var parts = new StackPanel { Name = PartsName, Spacing = 2 };
        var pointList = new StackPanel { Name = PointsName, Spacing = 2 };
        var morePoints = MakeButton(MorePointsName, "Load more points", "Load more points in time");
        var pointsHeading = SectionHeading("Points in time");

        var updating = false;

        void Render()
        {
            updating = true;
            try
            {
                bannerBox.IsVisible = vm.BannerText is not null;
                banner.Text = vm.BannerText;
                cursorText.Text = vm.HasClient
                    ? $"Seq {vm.CursorSeq:N0} of {vm.HeadSeq:N0}" + (vm.Frame is { Clamped: true } ? " (limited to what you may see)" : string.Empty)
                    : "Replay is not connected to a server.";
                slider.Maximum = Math.Max(1, vm.HeadSeq);
                slider.Value = vm.CursorSeq;
                slider.IsEnabled = vm.HasClient && !vm.IsLoading;

                back.IsEnabled = vm.CanStepBack;
                forward.IsEnabled = vm.CanStepForward;
                fork.IsEnabled = vm.CanFork;
                play.IsEnabled = vm.IsPlaying || vm.CanPlay;
                play.Content = vm.IsPlaying ? "Pause" : "Play";
                mode.IsEnabled = vm.HasClient;
                mode.Content = vm.Mode == ReplayViewMode.History ? "Show current" : "Show history";

                error.IsVisible = vm.ErrorText is not null;
                error.Text = vm.ErrorText;
                notice.IsVisible = vm.ForkNotice is not null;
                notice.Text = vm.ForkNotice;

                pointsHeading.IsVisible = vm.HasPoints;
                pointList.IsVisible = vm.HasPoints;
                morePoints.IsVisible = vm.HasPoints;
                morePoints.IsEnabled = vm.CanLoadMorePoints;
                pointList.Children.Clear();
                foreach (var point in vm.Points)
                {
                    var item = new Button { Content = point.Line, HorizontalContentAlignment = HorizontalAlignment.Left, Tag = point };
                    AutomationProperties.SetName(item, $"Jump to {point.Label}, seq {point.Seq}");
                    SetThemeBrush(item, TemplatedControl.ForegroundProperty, point.IsAudit ? _accentTextToken : _secondaryTextToken);
                    item.Click += (_, _) => _ = vm.JumpToAsync(point);
                    pointList.Children.Add(item);
                }

                messages.Children.Clear();
                parts.Children.Clear();
                if (vm.Frame is { } frame)
                {
                    if (frame.Messages.Count == 0)
                    {
                        messages.Children.Add(new TextBlock { Text = "No messages at this point.", Opacity = 0.7 });
                    }

                    foreach (var message in frame.Messages)
                    {
                        messages.Children.Add(new TextBlock { Text = message.Line, TextWrapping = TextWrapping.Wrap });
                    }

                    foreach (var part in frame.Parts)
                    {
                        var line = new TextBlock { Text = part.Line, TextWrapping = TextWrapping.Wrap };
                        if (!part.IsAvailable)
                        {
                            SetThemeBrush(line, TextBlock.ForegroundProperty, _secondaryTextToken);
                        }

                        parts.Children.Add(line);
                    }
                }
            }
            finally
            {
                updating = false;
            }
        }

        slider.PropertyChanged += (_, e) =>
        {
            if (updating || e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty)
            {
                return;
            }

            var target = (long)Math.Round(slider.Value);
            if (target != vm.CursorSeq)
            {
                _ = vm.SeekAsync(target);
            }
        };
        back.Click += (_, _) => _ = vm.StepAsync(-1);
        forward.Click += (_, _) => _ = vm.StepAsync(1);
        play.Click += (_, _) =>
        {
            if (vm.IsPlaying)
            {
                vm.Pause();
            }
            else
            {
                _ = vm.PlayAsync(_playInterval);
            }
        };
        fork.Click += (_, _) => _ = vm.ForkAsync();
        morePoints.Click += (_, _) => _ = vm.LoadPointsAsync();
        mode.Click += (_, _) =>
            vm.SetViewerOverride(vm.Mode == ReplayViewMode.History ? ReplayViewMode.Current : ReplayViewMode.History);
        vm.Changed += Render;
        Render();
        if (vm.HasClient && vm.Frame is null && !vm.IsLoading)
        {
            _ = vm.LoadAsync();
        }

        if (vm.HasPoints && vm.Points.Count == 0)
        {
            _ = vm.LoadPointsAsync();
        }

        return new StackPanel
        {
            Spacing = 8,
            Children = { bannerBox, cursorText, slider, controls, error, notice, SectionHeading("Messages"), messages, SectionHeading("State then"), parts, pointsHeading, pointList, morePoints },
        };
    }

    private static Button MakeButton(string name, string content, string automationName)
    {
        var button = new Button { Name = name, Content = content };
        AutomationProperties.SetName(button, automationName);
        return button;
    }

    private static TextBlock SectionHeading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold };

    /// <summary>Sets a brush from a theme token. A token the theme does not define leaves the control's default, so text never vanishes.</summary>
    private static void SetThemeBrush(AvaloniaObject target, AvaloniaProperty property, string token)
    {
        var app = Application.Current;
        if (app is not null && app.TryGetResource(token, app.ActualThemeVariant, out var value) && value is IBrush found)
        {
            target.SetValue(property, found);
        }
    }
}
