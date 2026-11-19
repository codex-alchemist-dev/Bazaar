namespace Bazaar.Diagnostics.Ingestion;

/// <summary>
/// Limits the ingestion checks apply on top of the report limits in <c>Bazaar.Contracts</c> (doc 43, sections 5.2 and
/// 12.1). Values that doc 43 does not set are chosen here and named as such.
/// </summary>
public static class IngestionLimits
{
    /// <summary>Largest width or height of a screenshot, in pixels. Chosen here; doc 43 does not set one.</summary>
    public const int MaxImageDimension = 8192;

    /// <summary>Largest screenshot in bytes. Chosen here; doc 43 does not set a total. Chunks are capped separately.</summary>
    public const long MaxAttachmentBytes = 16L * 1024 * 1024;
}
