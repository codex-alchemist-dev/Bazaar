using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Linux.Updates;

/// <summary>
/// A non-blocking strip at the top of the main window (doc 30 section 7): the coordinator's latest <see cref="UpdateNotice"/>
/// (checking, up to date, update staged, failed with the reason), a Dismiss button and, for a staged update, "Restart to apply".
/// Colours come from theme tokens through resource bindings, so a theme switch restyles it. Call <see cref="Show"/> on the UI thread.
/// </summary>
public sealed class UpdateNoticeBanner : Border
{
    private const string _surfaceToken = "Bazaar.brush.color.background.surface";
    private const string _borderToken = "Bazaar.brush.color.border.default";
    private const string _textToken = "Bazaar.brush.color.text.primary";

    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _restart = new() { Content = "Restart to apply", IsVisible = false };
    private readonly Button _dismiss = new() { Content = "Dismiss" };
    private readonly Action? _onRestart;
    private UpdateNotice? _dismissed;

    /// <summary>Creates the hidden banner. <paramref name="onRestart"/> null means no restart button is ever offered.</summary>
    public UpdateNoticeBanner(Action? onRestart = null)
    {
        _onRestart = onRestart;
        IsVisible = false;
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(16, 8);
        this.Bind(BackgroundProperty, this.GetResourceObservable(_surfaceToken));
        this.Bind(BorderBrushProperty, this.GetResourceObservable(_borderToken));
        _text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(_textToken));

        AutomationProperties.SetName(this, "Update notice");
        AutomationProperties.SetName(_dismiss, "Dismiss update notice");
        AutomationProperties.SetLiveSetting(_text, AutomationLiveSetting.Polite);

        _dismiss.Click += (_, _) => Dismiss();
        _restart.Click += (_, _) => _onRestart?.Invoke();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        buttons.Children.Add(_restart);
        buttons.Children.Add(_dismiss);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(_text);
        Child = row;
    }

    /// <summary>The notice last given to <see cref="Show"/>, or null.</summary>
    public UpdateNotice? Current { get; private set; }

    /// <summary>The line on screen.</summary>
    public string? DisplayedText => _text.Text;

    /// <summary>True when "Restart to apply" is on offer.</summary>
    public bool RestartOffered => _restart.IsVisible;

    /// <summary>The restart button.</summary>
    public Button RestartButton => _restart;

    /// <summary>The dismiss button.</summary>
    public Button DismissButton => _dismiss;

    /// <summary>Shows <paramref name="notice"/>; null hides the banner. A notice the user dismissed stays hidden until a different one arrives.</summary>
    public void Show(UpdateNotice? notice)
    {
        Current = notice;
        if (notice?.Kind == UpdateNoticeKind.Checking)
        {
            _dismissed = null; // a new check always shows its outcome
        }

        if (notice is null || notice.Kind == UpdateNoticeKind.Disabled || notice == _dismissed)
        {
            IsVisible = false;
            return;
        }

        _text.Text = notice.Text;
        bool offerRestart = _onRestart is not null && notice.Kind == UpdateNoticeKind.ReadyAfterRestart;
        _restart.IsVisible = offerRestart;
        if (offerRestart)
        {
            AutomationProperties.SetName(_restart, $"Restart to apply update {notice.Version}");
        }

        IsVisible = true;
    }

    /// <summary>Hides the banner until a different notice arrives.</summary>
    public void Dismiss()
    {
        _dismissed = Current;
        IsVisible = false;
    }
}
