using Bazaar.Contracts.Telemetry;
using Bazaar.Diagnostics.Ingestion;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Diagnostics.Tests;

/// <summary>The pure upload assembler: order, size, expiry and memory limits, with no I/O.</summary>
public sealed class AttachmentUploadAssemblerTests
{
    private static readonly Guid _connection = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid _other = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid _attachment = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid _report = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset _t0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private static AttachmentUploadAssembler Assembler(FakeTimeProvider clock, AttachmentUploadOptions? options = null) =>
        new(options ?? new AttachmentUploadOptions(), clock);

    private static CrashAttachmentBegin Begin(long total = 6, string kind = "screenshot", string type = "image/png") =>
        new(_attachment, _report, kind, type, total);

    private static CrashAttachmentChunk Chunk(int index, params byte[] data) => new(_attachment, index, data);

    private static CrashAttachmentEnd End(int count) => new(_attachment, count, new string('0', 64));

    [Fact]
    public void In_order_chunks_assemble_to_the_bytes_and_the_upload_closes()
    {
        var uploads = Assembler(new FakeTimeProvider(_t0));

        Assert.Null(uploads.Begin(_connection, Begin()));
        Assert.Null(uploads.Chunk(_connection, Chunk(0, 1, 2, 3)));
        Assert.Null(uploads.Chunk(_connection, Chunk(1, 4, 5, 6)));
        var (refusal, attachment) = uploads.End(_connection, End(2));

        Assert.Null(refusal);
        Assert.NotNull(attachment);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, attachment.Data.ToArray());
        Assert.Equal(_attachment, attachment.Begin.AttachmentId);
        Assert.Equal(0, uploads.OpenBytes);
        Assert.Equal(AttachmentUploadRefusal.UnknownSession, uploads.Chunk(_connection, Chunk(2, 7)));
    }

    [Fact]
    public void An_out_of_order_chunk_is_refused_and_the_expected_chunk_is_still_accepted()
    {
        var uploads = Assembler(new FakeTimeProvider(_t0));
        uploads.Begin(_connection, Begin());
        uploads.Chunk(_connection, Chunk(0, 1, 2, 3));

        var skipped = uploads.Chunk(_connection, Chunk(2, 4, 5, 6));
        var repeated = uploads.Chunk(_connection, Chunk(0, 1, 2, 3));
        var expected = uploads.Chunk(_connection, Chunk(1, 4, 5, 6));

        Assert.Equal(AttachmentUploadRefusal.OutOfOrder, skipped);
        Assert.Equal(AttachmentUploadRefusal.OutOfOrder, repeated);
        Assert.Null(expected);
    }

    [Fact]
    public void A_declared_size_over_the_cap_is_refused_before_any_memory_is_taken()
    {
        var uploads = Assembler(new FakeTimeProvider(_t0));

        var refusal = uploads.Begin(_connection, Begin(total: IngestionLimits.MaxAttachmentBytes + 1));

        Assert.Equal(AttachmentUploadRefusal.TooLarge, refusal);
        Assert.Equal(0, uploads.OpenBytes);
    }

    [Fact]
    public void A_begin_with_a_bad_kind_size_or_type_is_refused()
    {
        var uploads = Assembler(new FakeTimeProvider(_t0));

        Assert.Equal(AttachmentUploadRefusal.InvalidBegin, uploads.Begin(_connection, Begin(kind: "video")));
        Assert.Equal(AttachmentUploadRefusal.InvalidBegin, uploads.Begin(_connection, Begin(total: 0)));
        Assert.Equal(AttachmentUploadRefusal.UnsupportedType, uploads.Begin(_connection, Begin(type: "image/gif")));
    }

    [Fact]
    public void A_second_begin_with_the_same_id_on_one_connection_is_refused_but_another_connection_may_use_it()
    {
        var uploads = Assembler(new FakeTimeProvider(_t0));
        uploads.Begin(_connection, Begin());

        Assert.Equal(AttachmentUploadRefusal.DuplicateSession, uploads.Begin(_connection, Begin()));
        Assert.Null(uploads.Begin(_other, Begin()));
    }

    [Fact]
    public void Chunks_that_would_exceed_the_declared_size_are_refused()
    {
        var uploads = Assembler(new FakeTimeProvider(_t0));
        uploads.Begin(_connection, Begin(total: 4));
        uploads.Chunk(_connection, Chunk(0, 1, 2, 3));

        Assert.Equal(AttachmentUploadRefusal.OverDeclaredSize, uploads.Chunk(_connection, Chunk(1, 4, 5)));
        Assert.Null(uploads.Chunk(_connection, Chunk(1, 4)));
    }

    [Fact]
    public void Empty_and_oversized_chunks_are_refused()
    {
        var uploads = Assembler(new FakeTimeProvider(_t0));
        uploads.Begin(_connection, Begin(total: IngestionLimits.MaxAttachmentBytes));

        Assert.Equal(AttachmentUploadRefusal.EmptyChunk, uploads.Chunk(_connection, Chunk(0)));
        Assert.Equal(
            AttachmentUploadRefusal.ChunkTooLarge,
            uploads.Chunk(_connection, Chunk(0, new byte[CrashReportLimits.MaxAttachmentChunkBytes + 1])));
    }

    [Fact]
    public void A_chunk_count_over_the_cap_is_refused()
    {
        var uploads = Assembler(new FakeTimeProvider(_t0), new AttachmentUploadOptions { MaxChunkCount = 2 });
        uploads.Begin(_connection, Begin(total: 3));
        uploads.Chunk(_connection, Chunk(0, 1));
        uploads.Chunk(_connection, Chunk(1, 2));

        Assert.Equal(AttachmentUploadRefusal.TooManyChunks, uploads.Chunk(_connection, Chunk(2, 3)));
    }

    [Fact]
    public void An_end_that_names_a_different_chunk_count_is_refused_and_closes_the_upload()
    {
        var uploads = Assembler(new FakeTimeProvider(_t0));
        uploads.Begin(_connection, Begin());
        uploads.Chunk(_connection, Chunk(0, 1, 2, 3, 4, 5, 6));

        var (refusal, attachment) = uploads.End(_connection, End(2));

        Assert.Equal(AttachmentUploadRefusal.ChunkCountMismatch, refusal);
        Assert.Null(attachment);
        Assert.Equal(0, uploads.OpenBytes);
        Assert.Equal(AttachmentUploadRefusal.UnknownSession, uploads.End(_connection, End(1)).Refusal);
    }

    [Fact]
    public void An_upload_that_is_never_finished_expires_and_its_bytes_are_released()
    {
        var clock = new FakeTimeProvider(_t0);
        var uploads = Assembler(clock, new AttachmentUploadOptions { SessionLifetime = TimeSpan.FromMinutes(5) });
        uploads.Begin(_connection, Begin());
        uploads.Chunk(_connection, Chunk(0, 1, 2, 3));

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Null(uploads.Chunk(_connection, Chunk(1, 4, 5, 6)));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(AttachmentUploadRefusal.UnknownSession, uploads.Chunk(_connection, Chunk(2, 7)));
        Assert.Equal(AttachmentUploadRefusal.UnknownSession, uploads.End(_connection, End(2)).Refusal);
        Assert.Equal(0, uploads.OpenBytes);
    }

    [Fact]
    public void A_connection_may_hold_only_its_session_limit_of_open_uploads()
    {
        var uploads = Assembler(new FakeTimeProvider(_t0), new AttachmentUploadOptions { MaxSessionsPerConnection = 2 });
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        Assert.Null(uploads.Begin(_connection, new CrashAttachmentBegin(first, _report, "screenshot", "image/png", 4)));
        Assert.Null(uploads.Begin(_connection, new CrashAttachmentBegin(second, _report, "screenshot", "image/png", 4)));
        Assert.Equal(
            AttachmentUploadRefusal.SessionLimit,
            uploads.Begin(_connection, new CrashAttachmentBegin(third, _report, "screenshot", "image/png", 4)));
        Assert.Null(uploads.Begin(_other, new CrashAttachmentBegin(third, _report, "screenshot", "image/png", 4)));
    }

    [Fact]
    public void Open_bytes_across_all_connections_are_capped()
    {
        var uploads = Assembler(new FakeTimeProvider(_t0), new AttachmentUploadOptions { MaxOpenBytes = 4 });
        uploads.Begin(_connection, Begin(total: 6));
        uploads.Begin(_other, Begin(total: 6));
        Assert.Null(uploads.Chunk(_connection, Chunk(0, 1, 2, 3)));

        var refusal = uploads.Chunk(_other, Chunk(0, 1, 2));

        Assert.Equal(AttachmentUploadRefusal.MemoryLimit, refusal);
        Assert.Equal(3, uploads.OpenBytes);
    }

    [Fact]
    public void Forget_drops_every_open_upload_of_a_connection()
    {
        var uploads = Assembler(new FakeTimeProvider(_t0));
        uploads.Begin(_connection, Begin());
        uploads.Chunk(_connection, Chunk(0, 1, 2, 3));
        uploads.Begin(_other, Begin());

        uploads.Forget(_connection);

        Assert.Equal(0, uploads.OpenBytes);
        Assert.Equal(AttachmentUploadRefusal.UnknownSession, uploads.Chunk(_connection, Chunk(1, 4)));
        Assert.Null(uploads.Chunk(_other, Chunk(0, 1)));
    }
}

/// <summary>The pending store: holds each half only for its own device, within the count and byte caps, and until it expires.</summary>
public sealed class InMemoryPendingAttachmentStoreTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private static IngestionAttachment Upload(int size = 3)
    {
        var id = Guid.NewGuid();
        return new IngestionAttachment(
            new CrashAttachmentBegin(id, Guid.NewGuid(), "screenshot", "image/png", size),
            new CrashAttachmentEnd(id, 1, new string('0', 64)),
            new byte[size]);
    }

    [Fact]
    public void A_held_upload_is_handed_back_only_to_the_device_that_sent_it()
    {
        var store = new InMemoryPendingAttachmentStore(new PendingAttachmentOptions(), new FakeTimeProvider(_t0));
        var upload = Upload();
        var id = upload.Begin.AttachmentId;

        Assert.True(store.HoldUpload("device-a", upload));

        Assert.False(store.TryTakeUpload("device-b", id, out _));
        Assert.True(store.TryTakeUpload("device-a", id, out var taken));
        Assert.Same(upload, taken);
        Assert.False(store.TryTakeUpload("device-a", id, out _));
    }

    [Fact]
    public void A_held_report_is_handed_back_only_to_the_device_that_sent_it()
    {
        var store = new InMemoryPendingAttachmentStore(new PendingAttachmentOptions(), new FakeTimeProvider(_t0));
        var report = new PendingReport(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.True(store.HoldReport("device-a", report));

        Assert.False(store.TryTakeReport("device-b", report.AttachmentId, out _));
        Assert.True(store.TryTakeReport("device-a", report.AttachmentId, out var taken));
        Assert.Equal(report, taken);
    }

    [Fact]
    public void An_entry_that_is_never_claimed_expires()
    {
        var clock = new FakeTimeProvider(_t0);
        var store = new InMemoryPendingAttachmentStore(new PendingAttachmentOptions { Lifetime = TimeSpan.FromMinutes(10) }, clock);
        var upload = Upload();
        store.HoldUpload("device-a", upload);

        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.False(store.TryTakeUpload("device-a", upload.Begin.AttachmentId, out _));
    }

    [Fact]
    public void The_entry_cap_and_the_byte_cap_refuse_new_holds()
    {
        var byCount = new InMemoryPendingAttachmentStore(new PendingAttachmentOptions { MaxEntries = 1 }, new FakeTimeProvider(_t0));
        Assert.True(byCount.HoldUpload("device-a", Upload()));
        Assert.False(byCount.HoldUpload("device-a", Upload()));

        var byBytes = new InMemoryPendingAttachmentStore(new PendingAttachmentOptions { MaxHeldBytes = 5 }, new FakeTimeProvider(_t0));
        Assert.True(byBytes.HoldUpload("device-a", Upload(size: 3)));
        Assert.False(byBytes.HoldUpload("device-a", Upload(size: 3)));
    }
}
