namespace Bazaar.Client.Annotation.Tests;

/// <summary>The send flow: nothing leaves the device without <see cref="ScreenshotAnnotationSession.SendAsync"/>, and every failure ends in a known phase.</summary>
public class ScreenshotAnnotationSessionTests
{
    private static readonly CapturedImage _image = new(new byte[] { 9, 9, 9 }, "image/png");
    private static readonly NormPoint _a = new(0.1, 0.1);
    private static readonly NormPoint _b = new(0.9, 0.9);

    private static (ScreenshotAnnotationSession Session, FakeFlattener Flattener, FakeBlobUploader Uploader) Create(AnnotationOptions? options = null)
    {
        var flattener = new FakeFlattener();
        var uploader = new FakeBlobUploader();
        return (new ScreenshotAnnotationSession(flattener, uploader, options ?? new AnnotationOptions()), flattener, uploader);
    }

    [Fact]
    public void A_session_refuses_options_that_do_not_validate()
    {
        Assert.Throws<ArgumentException>(() =>
            new ScreenshotAnnotationSession(new FakeFlattener(), new FakeBlobUploader(), new AnnotationOptions(ChunkBytes: 0)));
    }

    [Fact]
    public void Open_moves_from_empty_to_annotating_and_only_works_once()
    {
        var (session, _, _) = Create();
        Assert.Equal(AnnotationPhase.Empty, session.Phase);

        session.Open(_image);

        Assert.Equal(AnnotationPhase.Annotating, session.Phase);
        Assert.Same(_image, session.Source);
        Assert.Throws<InvalidOperationException>(() => session.Open(_image));
    }

    [Fact]
    public void Marks_are_added_and_undone_only_while_annotating()
    {
        var (session, _, _) = Create();
        Assert.False(session.TryAddText(_a, "too early"));

        session.Open(_image);
        Assert.True(session.TryAddStroke([_a, _b]));
        Assert.True(session.TryAddText(_a, "note"));
        Assert.True(session.Undo());
        Assert.Equal(1, session.Annotations.Count);
    }

    [Fact]
    public void Editing_and_adding_nothing_uploads_nothing()
    {
        var (session, flattener, uploader) = Create();
        session.Open(_image);
        session.TryAddStroke([_a, _b]);
        session.TryAddText(_a, "draft");
        session.Undo();
        session.Discard();

        Assert.Equal(0, flattener.Calls);
        Assert.Equal(0, uploader.Calls);
        Assert.Equal(AnnotationPhase.Discarded, session.Phase);
    }

    [Fact]
    public async Task Send_flattens_then_uploads_the_flat_bytes_and_ends_sent()
    {
        var (session, flattener, uploader) = Create();
        session.Open(_image);
        session.TryAddStroke([_a, _b]);
        uploader.ReportProgress = true;
        var phases = new List<AnnotationPhase>();
        session.Changed += (_, _) => phases.Add(session.Phase);

        await session.SendAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, flattener.Calls);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Assert.Single(uploader.Uploads));
        Assert.Equal(AnnotationPhase.Sent, session.Phase);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3, 4 })), session.SentHash);
        Assert.Contains(AnnotationPhase.Uploading, phases);
        Assert.Null(session.Progress);
    }

    [Fact]
    public async Task A_failed_send_keeps_the_marks_and_a_retry_can_succeed()
    {
        var (session, _, uploader) = Create();
        session.Open(_image);
        session.TryAddText(_a, "keep me");
        uploader.Fault = new BlobUploadException(AnnotationFailure.Unavailable, "down");

        await session.SendAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AnnotationPhase.Failed, session.Phase);
        Assert.Equal(AnnotationFailure.Unavailable, session.Failure);
        Assert.Equal(1, session.Annotations.Count);

        uploader.Fault = null;
        await session.SendAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AnnotationPhase.Sent, session.Phase);
        Assert.Null(session.Failure);
        Assert.Equal(2, uploader.Calls);
    }

    [Fact]
    public async Task An_unexpected_upload_exception_is_reported_as_unavailable()
    {
        var (session, _, uploader) = Create();
        session.Open(_image);
        uploader.Fault = new InvalidOperationException("internal detail");

        await session.SendAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AnnotationPhase.Failed, session.Phase);
        Assert.Equal(AnnotationFailure.Unavailable, session.Failure);
    }

    [Fact]
    public async Task Cancelling_a_send_returns_to_annotating_without_a_stored_hash()
    {
        var (session, _, uploader) = Create();
        session.Open(_image);
        uploader.Block = true;

        var send = session.SendAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AnnotationPhase.Uploading, session.Phase);
        Assert.True(session.CancelSend());
        await send;

        Assert.Equal(AnnotationPhase.Annotating, session.Phase);
        Assert.Null(session.SentHash);
        Assert.Null(session.Failure);
        Assert.False(session.CancelSend());
    }

    [Fact]
    public async Task An_image_over_the_limit_fails_as_too_large_without_uploading()
    {
        var (session, _, uploader) = Create(new AnnotationOptions(MaxCaptureBytes: 2));
        session.Open(_image);

        await session.SendAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AnnotationFailure.TooLarge, session.Failure);
        Assert.Equal(0, uploader.Calls);
    }

    [Fact]
    public async Task A_flatten_that_throws_fails_as_render()
    {
        var (session, flattener, uploader) = Create();
        session.Open(_image);
        flattener.Fault = new InvalidOperationException("cannot decode");

        await session.SendAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AnnotationFailure.Render, session.Failure);
        Assert.Equal(0, uploader.Calls);
    }

    [Fact]
    public async Task Send_outside_an_editable_phase_throws()
    {
        var (session, _, _) = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Discard_is_refused_during_an_upload_and_ends_the_session()
    {
        var (session, _, _) = Create();
        session.Open(_image);

        Assert.True(session.Discard());
        Assert.Equal(AnnotationPhase.Discarded, session.Phase);
        Assert.False(session.Discard());
        Assert.False(session.TryAddText(_a, "late"));
    }
}
