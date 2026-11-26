using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Bazaar.Client.Annotation;

/// <summary>
/// Captures the app's own window as an image: it renders the visual the head names (the main view or the top level) into a
/// bitmap and encodes PNG. Nothing outside the app is read, and no system permission is needed. The user asked for the
/// capture by choosing Annotate; the module still sends nothing until Send (doc 29, work package 11).
/// </summary>
/// <param name="target">Returns the visual to capture, or null when none is showing. Read at each capture.</param>
public sealed class VisualScreenshotCapture(Func<Visual?> target) : IScreenshotCapture
{
    private readonly Func<Visual?> _target = target ?? throw new ArgumentNullException(nameof(target));

    /// <inheritdoc />
    /// <remarks>Call on the UI thread: the visual is rendered where it lives. Returns null when there is nothing sized to capture.</remarks>
    public Task<CapturedImage?> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var visual = _target();
        if (visual is null || visual.Bounds.Width < 1 || visual.Bounds.Height < 1)
        {
            return Task.FromResult<CapturedImage?>(null);
        }

        var scale = TopLevel.GetTopLevel(visual)?.RenderScaling ?? 1.0;
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(visual.Bounds.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(visual.Bounds.Height * scale)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(visual);

        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return Task.FromResult<CapturedImage?>(new CapturedImage(stream.ToArray(), "image/png"));
    }
}
