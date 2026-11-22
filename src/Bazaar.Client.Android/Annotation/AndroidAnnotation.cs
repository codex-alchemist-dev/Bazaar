using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Annotation;

namespace Bazaar.Client.Android.Annotation;

/// <summary>
/// Screenshot annotation for the phone (doc 29, work package 11). <see cref="ShowAsync"/> captures the app's own window, shows
/// the annotation view in its place, and puts the previous view back when the session is sent or discarded. The image goes
/// to the server only from the view's Send button, over the authenticated HTTP blob channel (<see cref="HttpBlobUploader"/>);
/// the bearer credential is in the client's header and is never logged.
/// </summary>
/// <remarks>
/// The radial menu does not exist in the head yet, so nothing calls <see cref="ShowAsync"/> today; the Annotate sector
/// (<c>AndroidSettings.RadialSectors</c>) is its caller once the radial is wired.
/// </remarks>
public sealed class AndroidAnnotation
{
    private readonly ContentControl _host;
    private readonly AnnotationOptions _options;
    private readonly HttpBlobUploader _uploader;
    private bool _open;

    /// <param name="host">The control that holds the app's main view. Its content is captured, then replaced while annotating.</param>
    /// <param name="endpoint">The company server the blob goes to.</param>
    /// <param name="options">Limits; the defaults are doc 29's.</param>
    public AndroidAnnotation(ContentControl host, ServerEndpoint endpoint, AnnotationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(endpoint);
        _host = host;
        _options = options ?? new AnnotationOptions();
        _uploader = new HttpBlobUploader(
            AnnotationFlow.CreateBlobClient(endpoint.CreateHandler(), endpoint.BaseAddress, endpoint.Credential),
            _options);
    }

    /// <summary>
    /// Captures the window and opens the annotation view. Call on the UI thread. Returns false when it is already open or
    /// there was nothing to capture.
    /// </summary>
    public async Task<bool> ShowAsync(CancellationToken cancellationToken = default)
    {
        if (_open)
        {
            return false;
        }

        var previous = _host.Content;
        ScreenshotAnnotationSession? session = null;
        var view = await AnnotationFlow.OpenAsync(
            new VisualScreenshotCapture(() => previous as Visual), _uploader, _options, cancellationToken, created => session = created);
        if (view is null || session is null)
        {
            return false;
        }

        _open = true;
        _host.Content = view;
        session.Changed += (_, _) =>
        {
            if (session.Phase is AnnotationPhase.Sent or AnnotationPhase.Discarded)
            {
                // The session raises Changed where it changed; the view swap belongs on the UI thread.
                Dispatcher.UIThread.Post(() =>
                {
                    _host.Content = previous;
                    view.Dispose();
                    _open = false;
                });
            }
        };
        return true;
    }
}
