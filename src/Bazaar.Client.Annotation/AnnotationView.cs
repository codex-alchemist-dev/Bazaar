using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Annotation;

/// <summary>
/// The screenshot annotation screen: the image with its marks, a toolbar (text mode, text, undo, send, cancel send,
/// discard), and the send progress. Send is the only control that uploads, and it is disabled while an upload runs.
/// Status text states what has left the device, so the user always knows whether the image was sent.
/// </summary>
public sealed class AnnotationView : UserControl, IDisposable
{
    private readonly ScreenshotAnnotationSession _session;

    /// <param name="session">The session the view drives. The head opens the image on it before showing the view.</param>
    /// <param name="options">The ink and limits used by the canvas.</param>
    public AnnotationView(ScreenshotAnnotationSession session, AnnotationOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        _session = session;

        TextModeToggle = new ToggleButton { Content = "Text" };
        TextBox = new TextBox { PlaceholderText = "Text to place", MinWidth = 160, MaxLength = options.MaxTextLength };
        UndoButton = new Button { Content = "Undo" };
        SendButton = new Button { Content = "Send" };
        CancelSendButton = new Button { Content = "Cancel send" };
        DiscardButton = new Button { Content = "Discard" };
        Canvas = new AnnotationCanvas(session, options, () => TextBox.Text ?? string.Empty);
        Progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6, IsVisible = false };
        Status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

        TextModeToggle.IsCheckedChanged += (_, _) => Canvas.TextMode = TextModeToggle.IsChecked == true;
        UndoButton.Click += (_, _) => _session.Undo();
        SendButton.Click += OnSendClicked;
        CancelSendButton.Click += (_, _) => _session.CancelSend();
        DiscardButton.Click += (_, _) => _session.Discard();
        _session.Changed += (_, _) => Refresh();

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(TextModeToggle);
        toolbar.Children.Add(TextBox);
        toolbar.Children.Add(UndoButton);
        toolbar.Children.Add(SendButton);
        toolbar.Children.Add(CancelSendButton);
        toolbar.Children.Add(DiscardButton);

        var footer = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(Progress);
        footer.Children.Add(Status);

        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(toolbar);
        layout.Children.Add(footer);
        layout.Children.Add(Canvas);
        Content = layout;

        Refresh();
    }

    /// <summary>Switches the canvas between drawing and placing text.</summary>
    public ToggleButton TextModeToggle { get; }

    /// <summary>Holds the text placed in text mode.</summary>
    public TextBox TextBox { get; }

    /// <summary>Removes the last mark.</summary>
    public Button UndoButton { get; }

    /// <summary>Flattens and uploads the image. The only control that sends anything.</summary>
    public Button SendButton { get; }

    /// <summary>Stops an upload in progress. The marks stay.</summary>
    public Button CancelSendButton { get; }

    /// <summary>Drops the screenshot without sending.</summary>
    public Button DiscardButton { get; }

    /// <summary>The drawing surface.</summary>
    public AnnotationCanvas Canvas { get; }

    /// <summary>Shows the progress of an upload.</summary>
    public ProgressBar Progress { get; }

    /// <summary>The line that says what has happened.</summary>
    public TextBlock Status { get; }

    /// <summary>Releases the decoded image held by the canvas.</summary>
    public void Dispose() => Canvas.Dispose();

    private void OnSendClicked(object? sender, RoutedEventArgs e)
    {
        // The session reports its own outcome through Changed, so the task is not awaited here.
        _ = SendAsync();
    }

    private async Task SendAsync()
    {
        try
        {
            await _session.SendAsync();
        }
        catch (InvalidOperationException)
        {
            // The button is disabled outside the phases that can send, so this only fires on a race; the status stays accurate.
            Refresh();
        }
    }

    private void Refresh()
    {
        var phase = _session.Phase;
        var annotating = phase == AnnotationPhase.Annotating;
        var uploading = phase == AnnotationPhase.Uploading;

        TextModeToggle.IsEnabled = annotating;
        TextBox.IsEnabled = annotating;
        UndoButton.IsEnabled = annotating && _session.Annotations.Count > 0;
        SendButton.IsEnabled = annotating || phase == AnnotationPhase.Failed;
        CancelSendButton.IsVisible = uploading;
        DiscardButton.IsEnabled = phase is not (AnnotationPhase.Uploading or AnnotationPhase.Discarded);

        Progress.IsVisible = uploading;
        var progress = _session.Progress;
        Progress.IsIndeterminate = progress is null;
        if (progress is { TotalBytes: > 0 } p)
        {
            Progress.Value = 100.0 * p.SentBytes / p.TotalBytes;
        }

        Status.Text = StatusText(phase, _session.Failure);
    }

    private static string StatusText(AnnotationPhase phase, AnnotationFailure? failure) => phase switch
    {
        AnnotationPhase.Empty => "No screenshot is open.",
        AnnotationPhase.Annotating => "Add marks, then press Send. Nothing leaves this device until you press Send.",
        AnnotationPhase.Uploading => "Sending the annotated screenshot...",
        AnnotationPhase.Sent => "Sent. The screenshot is stored.",
        AnnotationPhase.Failed => FailureText(failure),
        AnnotationPhase.Discarded => "Discarded. Nothing was sent.",
        _ => string.Empty,
    };

    private static string FailureText(AnnotationFailure? failure) => failure switch
    {
        AnnotationFailure.TooLarge => "The image is too large to send. Your marks are kept.",
        AnnotationFailure.Render => "The image could not be drawn. Your marks are kept.",
        AnnotationFailure.Rejected => "The server refused the screenshot. Your marks are kept.",
        AnnotationFailure.HashMismatch => "The server stored different bytes. Press Send to try again.",
        AnnotationFailure.Unavailable => "The server is unreachable. Press Send to try again.",
        AnnotationFailure.Protocol => "The server replied in an unexpected way. Press Send to try again.",
        _ => "The screenshot was not sent. Press Send to try again.",
    };
}
