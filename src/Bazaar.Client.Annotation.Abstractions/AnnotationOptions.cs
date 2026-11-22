namespace Bazaar.Client.Annotation;

/// <summary>
/// Client-side limits and defaults for screenshot annotation (doc 29, work package 11). Every value is an operator or
/// user setting with a safe default (CLAUDE.md, configurability rule). The server enforces its own ceilings as well:
/// <c>gc.blob_max_bytes</c> (25 MiB by default) and the 4 MiB chunk ceiling of the HTTP blob surface (doc 02).
/// </summary>
/// <param name="MaxItems">Most strokes and text notes on one screenshot.</param>
/// <param name="MaxPointsPerStroke">Most points one stroke may hold.</param>
/// <param name="MaxTextLength">Longest text note, in characters, after trimming.</param>
/// <param name="MaxCaptureBytes">Largest captured or flattened image in bytes. Keep at or below the server's blob limit.</param>
/// <param name="MaxPixelCount">Largest image in pixels, so a huge photo cannot exhaust memory while it is drawn.</param>
/// <param name="ChunkBytes">Bytes per upload chunk. Keep at or below the server's chunk ceiling.</param>
/// <param name="MaxAttemptsPerRequest">Tries for one request or chunk before the upload gives up as unavailable.</param>
/// <param name="RetryDelay">Pause before a retry. Null means <see cref="DefaultRetryDelay"/>.</param>
/// <param name="InkArgb">Default ink colour as 0xAARRGGBB.</param>
/// <param name="StrokeWidthFraction">Default pen width as a fraction of the shorter side.</param>
/// <param name="TextSizeFraction">Default text size as a fraction of the shorter side.</param>
public sealed record AnnotationOptions(
    int MaxItems = 200,
    int MaxPointsPerStroke = 2000,
    int MaxTextLength = 280,
    long MaxCaptureBytes = 26_214_400,
    long MaxPixelCount = 25_000_000,
    int ChunkBytes = 1_048_576,
    int MaxAttemptsPerRequest = 3,
    TimeSpan? RetryDelay = null,
    uint InkArgb = 0xFFE53935,
    double StrokeWidthFraction = 0.008,
    double TextSizeFraction = 0.04)
{
    /// <summary>The retry pause used when none is given.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>The pause before the first retry.</summary>
    public TimeSpan EffectiveRetryDelay => RetryDelay ?? DefaultRetryDelay;

    /// <summary>Lists every setting that is out of range. Empty when the options are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MaxItems < 1 || MaxItems > 2000) errors.Add("MaxItems must be between 1 and 2000.");
        if (MaxPointsPerStroke < 2 || MaxPointsPerStroke > 10_000) errors.Add("MaxPointsPerStroke must be between 2 and 10000.");
        if (MaxTextLength < 1 || MaxTextLength > 2000) errors.Add("MaxTextLength must be between 1 and 2000.");
        if (MaxCaptureBytes < 1 || MaxCaptureBytes > 1L << 30) errors.Add("MaxCaptureBytes must be between 1 and 1 GiB.");
        if (MaxPixelCount < 1) errors.Add("MaxPixelCount must be positive.");
        if (ChunkBytes < 1 || ChunkBytes > 4 * 1024 * 1024) errors.Add("ChunkBytes must be between 1 byte and 4 MiB.");
        if (MaxAttemptsPerRequest < 1 || MaxAttemptsPerRequest > 10) errors.Add("MaxAttemptsPerRequest must be between 1 and 10.");
        if (EffectiveRetryDelay < TimeSpan.Zero) errors.Add("RetryDelay must not be negative.");
        if (StrokeWidthFraction is < 0.001 or > 0.1) errors.Add("StrokeWidthFraction must be between 0.001 and 0.1.");
        if (TextSizeFraction is < 0.01 or > 0.2) errors.Add("TextSizeFraction must be between 0.01 and 0.2.");
        return errors;
    }
}
