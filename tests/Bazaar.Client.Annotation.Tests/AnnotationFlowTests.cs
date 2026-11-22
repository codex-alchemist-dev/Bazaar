using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace Bazaar.Client.Annotation.Tests;

public class AnnotationFlowTests
{
    private static readonly AnnotationOptions _options = new(RetryDelay: TimeSpan.Zero);

    private sealed class FixedCapture(CapturedImage? image) : IScreenshotCapture
    {
        public int Calls { get; private set; }

        public Task<CapturedImage?> CaptureAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(image);
        }
    }

    [AvaloniaFact]
    public void The_window_capture_renders_the_visual_to_a_png()
    {
        var content = new Border { Background = Brushes.White, Width = 120, Height = 80 };
        var window = new Window { Width = 120, Height = 80, Content = content };
        window.Show();

        var image = new VisualScreenshotCapture(() => content).CaptureAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.NotNull(image);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, image.Bytes.Span[..4].ToArray());
    }

    [AvaloniaFact]
    public void With_nothing_showing_the_window_capture_gives_null()
    {
        Assert.Null(new VisualScreenshotCapture(() => null).CaptureAsync(CancellationToken.None).GetAwaiter().GetResult());
        Assert.Null(new VisualScreenshotCapture(() => new Border()).CaptureAsync(CancellationToken.None).GetAwaiter().GetResult());
    }

    [AvaloniaFact]
    public async Task A_capture_opens_the_view_and_uploads_nothing_until_send()
    {
        var uploader = new FakeBlobUploader();
        ScreenshotAnnotationSession? session = null;

        var view = await AnnotationFlow.OpenAsync(
            new FixedCapture(TestImages.WhitePng(100, 100)), uploader, _options, CancellationToken.None, s => session = s);

        Assert.NotNull(view);
        Assert.NotNull(session);
        Assert.Equal(AnnotationPhase.Annotating, session.Phase);
        Assert.Equal(0, uploader.Calls);
    }

    [AvaloniaFact]
    public async Task A_cancelled_capture_opens_no_view()
    {
        var uploader = new FakeBlobUploader();

        var view = await AnnotationFlow.OpenAsync(new FixedCapture(null), uploader, _options, CancellationToken.None);

        Assert.Null(view);
        Assert.Equal(0, uploader.Calls);
    }

    [Fact]
    public void The_blob_client_sends_the_bearer_credential_and_keeps_the_base_address()
    {
        using var http = AnnotationFlow.CreateBlobClient(new HttpClientHandler(), new Uri("http://127.0.0.1:8080/"), "secret-token");

        Assert.Equal("http://127.0.0.1:8080/", http.BaseAddress!.ToString());
        Assert.Equal("Bearer", http.DefaultRequestHeaders.Authorization!.Scheme);
        Assert.Equal("secret-token", http.DefaultRequestHeaders.Authorization.Parameter);
    }

    [Fact]
    public void The_blob_client_has_no_authorization_without_a_credential()
    {
        using var http = AnnotationFlow.CreateBlobClient(new HttpClientHandler(), new Uri("http://127.0.0.1:8080/"), null);

        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }
}
