namespace Bazaar.Client.Annotation;

/// <summary>Bytes to store as a blob on the server. The uploader computes the SHA-256 itself.</summary>
/// <param name="Content">The complete blob.</param>
public sealed record BlobUploadRequest(ReadOnlyMemory<byte> Content);

/// <summary>The stored blob: its content hash (lowercase SHA-256 hex) and size in bytes.</summary>
public sealed record BlobUploadResult(string Hash, long Size);

/// <summary>Bytes sent so far out of the total.</summary>
/// <param name="SentBytes">Bytes the server has accepted.</param>
/// <param name="TotalBytes">Size of the blob.</param>
public sealed record BlobUploadProgress(long SentBytes, long TotalBytes);

/// <summary>Why an annotation or upload did not finish. Shared by the uploader and the session, so one list reaches the UI.</summary>
public enum AnnotationFailure
{
    /// <summary>The image is larger than <see cref="AnnotationOptions.MaxCaptureBytes"/> or the server's limit.</summary>
    TooLarge,

    /// <summary>The image could not be decoded, or is too large to draw.</summary>
    Render,

    /// <summary>The server refused the upload (a client or auth error); retrying the same bytes will not help.</summary>
    Rejected,

    /// <summary>The server reports a different hash than the bytes sent.</summary>
    HashMismatch,

    /// <summary>The server or network stayed unavailable through every retry.</summary>
    Unavailable,

    /// <summary>The server answered with something the protocol does not allow.</summary>
    Protocol,
}

/// <summary>Stores a blob over the authenticated HTTP blob channel (doc 02). Implementations are fakes in tests.</summary>
public interface IBlobUploader
{
    /// <summary>
    /// Uploads the bytes. Reports progress as chunks are accepted. Throws <see cref="BlobUploadException"/> on a failure
    /// the caller should show, and <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires.
    /// </summary>
    Task<BlobUploadResult> UploadAsync(BlobUploadRequest request, IProgress<BlobUploadProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>An upload that failed for a reason the user can see.</summary>
public sealed class BlobUploadException : Exception
{
    /// <param name="failure">The failure class.</param>
    /// <param name="message">A short message without paths or server internals.</param>
    /// <param name="statusCode">The HTTP status of the last response, when there was one.</param>
    public BlobUploadException(AnnotationFailure failure, string message, int? statusCode = null)
        : base(message)
    {
        Failure = failure;
        StatusCode = statusCode;
    }

    /// <summary>The failure class.</summary>
    public AnnotationFailure Failure { get; }

    /// <summary>The HTTP status of the last response, if there was one.</summary>
    public int? StatusCode { get; }
}
