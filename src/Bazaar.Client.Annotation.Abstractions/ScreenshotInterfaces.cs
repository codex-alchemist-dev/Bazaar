namespace Bazaar.Client.Annotation;

/// <summary>An image the user chose to annotate: the bytes as the capture gave them (PNG or JPEG).</summary>
/// <param name="Bytes">The encoded image.</param>
/// <param name="MediaType">Its media type, for example <c>image/png</c>.</param>
public sealed record CapturedImage(ReadOnlyMemory<byte> Bytes, string MediaType);

/// <summary>The annotated image, flattened to PNG, ready to upload.</summary>
/// <param name="Bytes">The PNG bytes.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public sealed record FlattenedImage(ReadOnlyMemory<byte> Bytes, int Width, int Height);

/// <summary>
/// Supplies the screenshot to annotate. Each head implements it: Android uses the system photo picker, a desktop head a file
/// dialog. The module never captures on its own and calls this only after the user asks to annotate.
/// </summary>
public interface IScreenshotCapture
{
    /// <summary>Asks the user for an image. Returns null when the user cancels.</summary>
    Task<CapturedImage?> CaptureAsync(CancellationToken cancellationToken);
}

/// <summary>Draws the annotations onto the captured image and returns one flat PNG.</summary>
public interface IAnnotationFlattener
{
    /// <summary>Renders <paramref name="annotations"/> over <paramref name="source"/>. Does not change either input.</summary>
    Task<FlattenedImage> FlattenAsync(CapturedImage source, AnnotationDocument annotations, CancellationToken cancellationToken);
}
