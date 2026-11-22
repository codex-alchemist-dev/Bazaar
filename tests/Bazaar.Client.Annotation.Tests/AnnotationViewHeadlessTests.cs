using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Bazaar.Client.Annotation.Tests;

/// <summary>
/// The annotation screen under Avalonia.Headless. A gesture adds a mark, and only the Send button uploads.
/// The send runs on a worker thread (the flatten), so these tests pump the UI thread until the phase changes.
/// </summary>
public class AnnotationViewHeadlessTests
{
    private static readonly AnnotationOptions _options = new(RetryDelay: TimeSpan.Zero);

    private sealed class Screen
    {
        public required Window Window { get; init; }
        public required AnnotationView View { get; init; }
        public required ScreenshotAnnotationSession Session { get; init; }
        public required FakeBlobUploader Uploader { get; init; }
    }

    /// <summary>A window showing the view with a 100 by 100 white image open, laid out and ready for pointer input.</summary>
    private static Screen Open(FakeBlobUploader? uploader = null)
    {
        uploader ??= new FakeBlobUploader();
        var session = new ScreenshotAnnotationSession(new AvaloniaAnnotationFlattener(_options), uploader, _options);
        var view = new AnnotationView(session, _options);
        var window = new Window { Width = 400, Height = 500, Content = view };
        session.Open(TestImages.WhitePng(100, 100));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new Screen { Window = window, View = view, Session = session, Uploader = uploader };
    }

    /// <summary>A point inside the image, at the given fractions, in window coordinates.</summary>
    private static Point InImage(Screen screen, double fx, double fy)
    {
        var rect = screen.View.Canvas.ImageRect;
        var local = new Point(rect.X + rect.Width * fx, rect.Y + rect.Height * fy);
        return screen.View.Canvas.TranslatePoint(local, screen.Window)!.Value;
    }

    /// <summary>Runs the UI thread until the condition holds, or fails after a few seconds.</summary>
    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > deadline) Assert.Fail("The UI did not reach the expected state in time.");
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void A_drag_on_the_canvas_adds_one_stroke_and_uploads_nothing()
    {
        var screen = Open();
        var start = InImage(screen, 0.1, 0.5);
        var end = InImage(screen, 0.9, 0.5);

        screen.Window.MouseDown(start, MouseButton.Left);
        screen.Window.MouseMove(InImage(screen, 0.5, 0.5));
        screen.Window.MouseMove(end);
        screen.Window.MouseUp(end, MouseButton.Left);

        var stroke = Assert.IsType<StrokeItem>(Assert.Single(screen.Session.Annotations.Items));
        Assert.True(stroke.Points.Count >= 2);
        Assert.Equal(0, screen.Uploader.Calls);
        Assert.Equal(AnnotationPhase.Annotating, screen.Session.Phase);
        screen.Window.Close();
    }

    [AvaloniaFact]
    public void Text_mode_places_the_typed_text_where_the_press_is()
    {
        var screen = Open();
        screen.View.TextModeToggle.IsChecked = true;
        screen.View.TextBox.Text = "  check the header  ";
        var at = InImage(screen, 0.25, 0.25);

        screen.Window.MouseDown(at, MouseButton.Left);
        screen.Window.MouseUp(at, MouseButton.Left);

        var text = Assert.IsType<TextItem>(Assert.Single(screen.Session.Annotations.Items));
        Assert.Equal("check the header", text.Text);
        Assert.InRange(text.At.X, 0.2, 0.3);
        Assert.Equal(0, screen.Uploader.Calls);
        screen.Window.Close();
    }

    [AvaloniaFact]
    public void Send_button_flattens_and_uploads_a_png_and_then_shows_sent()
    {
        var screen = Open();
        screen.Session.TryAddText(new NormPoint(0.1, 0.1), "note");
        Assert.Equal(0, screen.Uploader.Calls);

        screen.View.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => screen.Session.Phase == AnnotationPhase.Sent);

        var upload = Assert.Single(screen.Uploader.Uploads);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, upload[..4]);
        Assert.Contains("Sent", screen.View.Status.Text, StringComparison.Ordinal);
        Assert.False(screen.View.SendButton.IsEnabled);
        screen.Window.Close();
    }

    [AvaloniaFact]
    public void Cancel_send_stops_the_upload_and_keeps_the_marks()
    {
        var uploader = new FakeBlobUploader { Block = true };
        var screen = Open(uploader);
        screen.Session.TryAddText(new NormPoint(0.1, 0.1), "keep");

        screen.View.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => screen.Session.Phase == AnnotationPhase.Uploading);
        Assert.True(screen.View.CancelSendButton.IsVisible);
        Assert.False(screen.View.SendButton.IsEnabled);

        screen.View.CancelSendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => screen.Session.Phase == AnnotationPhase.Annotating);

        Assert.Equal(1, screen.Session.Annotations.Count);
        Assert.Null(screen.Session.SentHash);
        Assert.False(screen.View.CancelSendButton.IsVisible);
        screen.Window.Close();
    }

    [AvaloniaFact]
    public void A_failed_send_says_so_and_offers_send_again()
    {
        var uploader = new FakeBlobUploader { Fault = new BlobUploadException(AnnotationFailure.Unavailable, "down") };
        var screen = Open(uploader);

        screen.View.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => screen.Session.Phase == AnnotationPhase.Failed);

        Assert.Contains("unreachable", screen.View.Status.Text, StringComparison.Ordinal);
        Assert.True(screen.View.SendButton.IsEnabled);
        screen.Window.Close();
    }

    [AvaloniaFact]
    public void Discard_drops_the_screenshot_without_uploading()
    {
        var screen = Open();

        screen.View.DiscardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(AnnotationPhase.Discarded, screen.Session.Phase);
        Assert.Equal(0, screen.Uploader.Calls);
        Assert.Contains("Nothing was sent", screen.View.Status.Text, StringComparison.Ordinal);
        Assert.False(screen.View.SendButton.IsEnabled);
        screen.Window.Close();
    }
}
