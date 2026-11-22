namespace Bazaar.Client.Annotation;

/// <summary>Where an annotation session is.</summary>
public enum AnnotationPhase
{
    /// <summary>No image yet.</summary>
    Empty,

    /// <summary>The user is adding marks. Nothing has left the device.</summary>
    Annotating,

    /// <summary>The user asked to send; the flat image is being uploaded.</summary>
    Uploading,

    /// <summary>The flat image is stored on the server. Terminal.</summary>
    Sent,

    /// <summary>The send failed. The marks are kept, and the user may send again.</summary>
    Failed,

    /// <summary>The user dropped the screenshot without sending it. Terminal.</summary>
    Discarded,
}

/// <summary>
/// One screenshot being annotated. It is a state machine the view drives: open an image, add marks, then send.
/// Nothing is uploaded except by <see cref="SendAsync"/>, which only the view calls from the Send button (doc 29: never
/// sends without an explicit user action). Changes raise <see cref="Changed"/> on the thread that made them.
/// </summary>
public sealed class ScreenshotAnnotationSession
{
    private readonly IAnnotationFlattener _flattener;
    private readonly IBlobUploader _uploader;
    private readonly AnnotationOptions _options;
    private CancellationTokenSource? _sendCancel;

    /// <param name="flattener">Draws the marks onto the image.</param>
    /// <param name="uploader">Stores the flat image as a blob.</param>
    /// <param name="options">Limits; validated by the caller.</param>
    public ScreenshotAnnotationSession(IAnnotationFlattener flattener, IBlobUploader uploader, AnnotationOptions options)
    {
        ArgumentNullException.ThrowIfNull(flattener);
        ArgumentNullException.ThrowIfNull(uploader);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Validate() is { Count: > 0 } errors) throw new ArgumentException(string.Join(' ', errors), nameof(options));

        _flattener = flattener;
        _uploader = uploader;
        _options = options;
        Annotations = new AnnotationDocument(options);
    }

    /// <summary>Raised after every change of phase, marks or progress.</summary>
    public event EventHandler? Changed;

    /// <summary>Where the session is.</summary>
    public AnnotationPhase Phase { get; private set; } = AnnotationPhase.Empty;

    /// <summary>The image being annotated, or null before <see cref="Open"/>.</summary>
    public CapturedImage? Source { get; private set; }

    /// <summary>The marks on the image.</summary>
    public AnnotationDocument Annotations { get; }

    /// <summary>Progress of the current upload, or null when no upload is running.</summary>
    public BlobUploadProgress? Progress { get; private set; }

    /// <summary>Why the last send failed, or null.</summary>
    public AnnotationFailure? Failure { get; private set; }

    /// <summary>Content hash of the stored image once <see cref="Phase"/> is <see cref="AnnotationPhase.Sent"/>.</summary>
    public string? SentHash { get; private set; }

    /// <summary>Starts annotating an image. Only valid from <see cref="AnnotationPhase.Empty"/>.</summary>
    public void Open(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (Phase != AnnotationPhase.Empty) throw new InvalidOperationException($"Cannot open an image in phase {Phase}.");

        Source = image;
        Phase = AnnotationPhase.Annotating;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a stroke. False when refused by the limits or when the session is not annotating.</summary>
    public bool TryAddStroke(IReadOnlyList<NormPoint> points)
    {
        if (Phase != AnnotationPhase.Annotating || !Annotations.TryAddStroke(points)) return false;

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Adds a text note. False when refused by the limits or when the session is not annotating.</summary>
    public bool TryAddText(NormPoint at, string text)
    {
        if (Phase != AnnotationPhase.Annotating || !Annotations.TryAddText(at, text)) return false;

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Removes the last mark. False when there is none or the session is not annotating.</summary>
    public bool Undo()
    {
        if (Phase != AnnotationPhase.Annotating || !Annotations.Undo()) return false;

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Flattens the marks and uploads the image. Called only by the Send action. From <see cref="AnnotationPhase.Failed"/>
    /// it tries again with the same marks. Ends in <see cref="AnnotationPhase.Sent"/>, <see cref="AnnotationPhase.Failed"/>, or
    /// back to <see cref="AnnotationPhase.Annotating"/> when the user cancels with <see cref="CancelSend"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session is not in a phase that can send.</exception>
    public async Task SendAsync(CancellationToken cancellationToken = default)
    {
        if (Phase is not (AnnotationPhase.Annotating or AnnotationPhase.Failed) || Source is null)
        {
            throw new InvalidOperationException($"Cannot send in phase {Phase}.");
        }

        _sendCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _sendCancel.Token;
        Phase = AnnotationPhase.Uploading;
        Failure = null;
        Progress = null;
        Changed?.Invoke(this, EventArgs.Empty);

        try
        {
            var flat = await FlattenAsync(Source, token);
            if (flat is null) return;

            var result = await _uploader.UploadAsync(new BlobUploadRequest(flat.Bytes), new SendProgress(this), token);
            SentHash = result.Hash;
            Phase = AnnotationPhase.Sent;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The user cancelled the send. Nothing was stored; the marks stay for another try.
            Phase = AnnotationPhase.Annotating;
        }
        catch (BlobUploadException ex)
        {
            Fail(ex.Failure);
        }
        catch (Exception)
        {
            // Anything else from the uploader is treated as an unreachable server; the detail stays out of the UI.
            Fail(AnnotationFailure.Unavailable);
        }
        finally
        {
            Progress = null;
            _sendCancel.Dispose();
            _sendCancel = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Cancels the send in progress, if any. The session returns to annotating. False when no send is running.</summary>
    public bool CancelSend()
    {
        if (Phase != AnnotationPhase.Uploading || _sendCancel is null) return false;

        _sendCancel.Cancel();
        return true;
    }

    /// <summary>Drops the screenshot without sending. Refused (false) while an upload is running.</summary>
    public bool Discard()
    {
        if (Phase is AnnotationPhase.Uploading or AnnotationPhase.Discarded) return false;

        Phase = AnnotationPhase.Discarded;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void ReportProgress(BlobUploadProgress progress)
    {
        Progress = progress;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Fail(AnnotationFailure failure)
    {
        Failure = failure;
        Phase = AnnotationPhase.Failed;
    }

    /// <summary>Flattens the marks. Returns null, with the phase set to failed, when the image is too large or cannot be drawn.</summary>
    private async Task<FlattenedImage?> FlattenAsync(CapturedImage source, CancellationToken token)
    {
        if (source.Bytes.Length > _options.MaxCaptureBytes)
        {
            Fail(AnnotationFailure.TooLarge);
            return null;
        }

        FlattenedImage flat;
        try
        {
            flat = await _flattener.FlattenAsync(source, Annotations, token);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            Fail(AnnotationFailure.Render);
            return null;
        }

        if (flat.Bytes.Length > _options.MaxCaptureBytes)
        {
            Fail(AnnotationFailure.TooLarge);
            return null;
        }
        return flat;
    }

    /// <summary>Forwards upload progress to the session synchronously, so the phase the session ends in is never overwritten.</summary>
    private sealed class SendProgress(ScreenshotAnnotationSession session) : IProgress<BlobUploadProgress>
    {
        public void Report(BlobUploadProgress value) => session.ReportProgress(value);
    }
}
