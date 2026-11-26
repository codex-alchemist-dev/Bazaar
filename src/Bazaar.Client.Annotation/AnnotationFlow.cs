using System.Net.Http.Headers;

namespace Bazaar.Client.Annotation;

/// <summary>
/// Puts the annotation pieces together for a head: capture, a session on the flattener and the HTTP blob uploader, and the
/// view that drives it. The head shows the returned view and removes it when the session ends (<see cref="ScreenshotAnnotationSession.Phase"/>
/// is <see cref="AnnotationPhase.Sent"/> or <see cref="AnnotationPhase.Discarded"/>). Nothing is uploaded here: only the view's Send button does that.
/// </summary>
public static class AnnotationFlow
{
    /// <summary>
    /// Captures the image and returns the annotation view with the image opened, or null when the capture gave nothing
    /// (the user cancelled, or nothing was showing).
    /// </summary>
    /// <param name="capture">Supplies the image.</param>
    /// <param name="uploader">Stores the flat image as a blob.</param>
    /// <param name="options">Limits and ink.</param>
    /// <param name="cancellationToken">Cancels the capture.</param>
    /// <param name="session">The session the returned view drives, so the head can watch its phase.</param>
    public static async Task<AnnotationView?> OpenAsync(
        IScreenshotCapture capture,
        IBlobUploader uploader,
        AnnotationOptions options,
        CancellationToken cancellationToken,
        Action<ScreenshotAnnotationSession>? session = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(uploader);
        ArgumentNullException.ThrowIfNull(options);

        var image = await capture.CaptureAsync(cancellationToken).ConfigureAwait(true);
        if (image is null)
        {
            return null;
        }

        var created = new ScreenshotAnnotationSession(new AvaloniaAnnotationFlattener(options), uploader, options);
        created.Open(image);
        session?.Invoke(created);
        return new AnnotationView(created, options);
    }

    /// <summary>
    /// An HTTP client for the blob channel: the server origin as base address, the handler the endpoint chose (pinned for
    /// https), and the bearer credential on every request. The credential is held only in the header; nothing logs it.
    /// </summary>
    /// <param name="handler">The endpoint's handler, which enforces the key-hash pin.</param>
    /// <param name="baseAddress">The server origin.</param>
    /// <param name="credential">Bearer credential, or null when the endpoint has none (loopback development).</param>
    public static HttpClient CreateBlobClient(HttpMessageHandler handler, Uri baseAddress, string? credential)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(baseAddress);

        var http = new HttpClient(handler) { BaseAddress = baseAddress };
        if (!string.IsNullOrEmpty(credential))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }

        return http;
    }
}
