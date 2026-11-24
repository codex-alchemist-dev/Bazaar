using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Bazaar.Client.Motion.Avalonia;

namespace Bazaar.Client.PhoneCoach.Avalonia;

/// <summary>
/// The first-run coach (doc 93): a dimmed overlay with one short card per gesture (swipe, then hold), shown once, dismissible at any step
/// (Skip, Escape, or "Got it" on the last) and replayable through <see cref="CoachController.Replay"/>. The card enters by the motion plan of
/// the current level: a fade of 90 ms or less when reduced, otherwise a short ease with a slight scale. The overlay follows the controller; it holds no
/// state of its own, so the "seen" flag stays a client setting.
/// </summary>
public sealed class CoachOverlay : Grid
{
    private readonly CoachController _controller;
    private readonly MotionSettings _motion;
    private readonly IFrameDriver? _frames;
    private readonly PhoneCoachOptions _options;
    private readonly Border _card;
    private readonly TextBlock _glyph;
    private readonly TextBlock _title;
    private readonly TextBlock _body;
    private readonly TextBlock _progress;
    private readonly Button _skip;
    private readonly Button _next;
    private int _generation;

    /// <param name="controller">The coach state.</param>
    /// <param name="motion">The motion level and tokens; at the reduced level the entrance is a short fade only.</param>
    /// <param name="frames">Animation frames; null shows the card at once with no entrance.</param>
    /// <param name="options">The sizes; null uses the defaults.</param>
    public CoachOverlay(CoachController controller, MotionSettings motion, IFrameDriver? frames = null, PhoneCoachOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(motion);
        _controller = controller;
        _motion = motion;
        _frames = frames;
        _options = options ?? PhoneCoachOptions.Default;

        var text = ThemeTokens.Brush(PhoneCoachOptions.CoachTextToken, Brushes.White);
        _glyph = new TextBlock { FontSize = _options.CoachTitleSize * 2, HorizontalAlignment = HorizontalAlignment.Center, Foreground = text };
        _title = new TextBlock { FontSize = _options.CoachTitleSize, FontWeight = FontWeight.SemiBold, Foreground = text, TextWrapping = TextWrapping.Wrap };
        _body = new TextBlock { FontSize = _options.CoachBodySize, Foreground = text, TextWrapping = TextWrapping.Wrap };
        _progress = new TextBlock { FontSize = _options.CoachBodySize, Foreground = text, Opacity = _options.MutedOpacity, VerticalAlignment = VerticalAlignment.Center };
        _skip = new Button { Content = "Skip" };
        _next = new Button { Content = "Next" };
        AutomationProperties.SetName(_skip, "Skip the tour");
        _skip.Click += (_, _) => _controller.Dismiss();
        _next.Click += (_, _) => _controller.Next();

        var buttons = new DockPanel { LastChildFill = false, Children = { _progress, _skip, _next } };
        DockPanel.SetDock(_progress, Dock.Left);
        DockPanel.SetDock(_next, Dock.Right);
        DockPanel.SetDock(_skip, Dock.Right);
        _skip.Margin = new Thickness(0, 0, _options.CoachPaddingDp / 2, 0);

        _card = new Border
        {
            MaxWidth = _options.CoachMaxWidthDp,
            Padding = new Thickness(_options.CoachPaddingDp),
            Margin = new Thickness(_options.CoachPaddingDp),
            CornerRadius = new CornerRadius(_options.CoachCornerDp),
            Background = ThemeTokens.Brush(PhoneCoachOptions.CoachSurfaceToken, Brushes.DimGray),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = RelativePoint.Center,
            Child = new StackPanel { Spacing = _options.CoachPaddingDp / 2, Children = { _glyph, _title, _body, buttons } },
        };
        // The scrim also stops swipes and holds reaching the pager while the coach is up.
        Background = ThemeTokens.Brush(PhoneCoachOptions.CoachScrimToken, Brushes.Black);
        Children.Add(_card);
        Focusable = true;
        AutomationProperties.SetName(this, "Gesture tips");
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _controller.Dismiss();
                e.Handled = true;
            }
        };

        _controller.Changed += (_, _) => Refresh(animate: true);
        _motion.Changed += (_, _) => _generation++;
        Refresh(animate: false);
    }

    /// <summary>True while the overlay is on screen.</summary>
    public bool IsOpen => IsVisible;

    /// <summary>The prompt title on screen.</summary>
    public string Title => _title.Text ?? "";

    /// <summary>The prompt body on screen.</summary>
    public string Body => _body.Text ?? "";

    /// <summary>The text on the primary button: "Next", or "Got it" on the last prompt.</summary>
    public string PrimaryLabel => _next.Content as string ?? "";

    /// <summary>Taps the primary button (Next or Got it).</summary>
    public void PressNext() => _controller.Next();

    /// <summary>Taps Skip.</summary>
    public void PressSkip() => _controller.Dismiss();

    private void Refresh(bool animate)
    {
        var step = _controller.CurrentStep;
        bool wasVisible = IsVisible;
        IsVisible = step is not null;
        if (step is null)
        {
            _generation++;
            return;
        }

        _glyph.Text = step.Gesture == CoachGesture.Swipe ? "↔" : "●";
        _title.Text = step.Title;
        _body.Text = step.Body;
        _progress.Text = $"{_controller.StepIndex + 1} of {_controller.Steps.Count}";
        _next.Content = _controller.IsLastStep ? "Got it" : "Next";
        AutomationProperties.SetName(_next, _controller.IsLastStep ? "Got it, close the tour" : "Next tip");
        AutomationProperties.SetHelpText(_card, $"{step.Title}. {step.Body}");

        if (!wasVisible)
        {
            _next.Focus();
            if (animate)
            {
                Enter();
            }
            else
            {
                _card.Opacity = 1;
                _card.RenderTransform = null;
            }
        }
    }

    // Opacity (and scale, above the reduced level) eases from the plan's start to rest. No frame source means no entrance.
    private void Enter()
    {
        var plan = CoachMotionPlan.For(_motion.Effective, _motion.Tokens);
        if (_frames is null || plan.Duration <= TimeSpan.Zero)
        {
            _card.Opacity = 1;
            _card.RenderTransform = null;
            return;
        }

        int generation = ++_generation;
        TimeSpan? start = null;
        Apply(0);

        void Step(TimeSpan now)
        {
            if (generation != _generation)
            {
                return;
            }

            start ??= now;
            double t = Math.Clamp((now - start.Value).TotalMilliseconds / plan.Duration.TotalMilliseconds, 0, 1);
            Apply(t);
            if (t < 1)
            {
                _frames.RequestFrame(Step);
            }
        }

        _frames.RequestFrame(Step);

        void Apply(double t)
        {
            // Ease out: fast at first, settling at the end.
            double eased = 1 - Math.Pow(1 - t, 3);
            _card.Opacity = eased;
            double scale = plan.StartScale + (1 - plan.StartScale) * eased;
            _card.RenderTransform = t >= 1 || plan.StartScale >= 1 ? null : new ScaleTransform(scale, scale);
        }
    }
}
