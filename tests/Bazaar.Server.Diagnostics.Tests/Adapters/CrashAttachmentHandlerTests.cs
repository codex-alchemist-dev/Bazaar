using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Telemetry;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Diagnostics.Adapters;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Diagnostics.Tests.Adapters;

/// <summary>The screenshot upload handlers end to end: upload and report in either order, with the ingestion checks applied.</summary>
public sealed class CrashAttachmentHandlerTests
{
    private sealed class UploadConnection(Guid connectionId, string deviceId, IWireCodec codec) : IWireConnection
    {
        public Guid ConnectionId => connectionId;

        public PrincipalRef Principal { get; } = new(Guid.NewGuid(), "human");

        public string DeviceId => deviceId;

        public string DeviceClass => "windows";

        public IWireCodec Codec => codec;

        public IReadOnlyList<StreamCursor> HelloCursors => [];

        public Bazaar.Server.Wire.Sync.SubscriptionSession? Subscriptions => null;

        public CancellationToken Closed => CancellationToken.None;

        public bool TryEnqueueEvent(Envelope envelope) => true;

        public bool TryEnqueueLagged(Lagged lagged) => true;

        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;
    }

    private sealed class BindingRegistry : ICrashInstallRegistry
    {
        private readonly Dictionary<Guid, Guid> _bound = [];

        public Task<bool> TryBindAsync(Guid installId, Guid deviceId, bool isShared, CancellationToken ct = default) =>
            Task.FromResult(_bound.TryAdd(installId, deviceId) || _bound[installId] == deviceId);
    }

    /// <summary>One server: its store, audit, upload assembler, pending store and handlers over them.</summary>
    private sealed class Harness
    {
        private readonly FakeSealer _sealer = new();
        private readonly CrashIntakeFactory _factory;

        public Harness()
        {
            _factory = new CrashIntakeFactory(Store, _sealer, Audit, new ResolvedSymbols(), TimeProvider.System, new CrashFilingOptions { IssueRepo = "o/r" });
            Submit = new CrashSubmitHandler(_factory, Registry, Pending);
            Begin = new CrashAttachmentBeginHandler(Uploads);
            Chunk = new CrashAttachmentChunkHandler(Uploads);
            End = new CrashAttachmentEndHandler(Uploads, Pending, _factory, Registry);
        }

        public InMemoryCrashStore Store { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public BindingRegistry Registry { get; } = new();

        public AttachmentUploadAssembler Uploads { get; } = new(new AttachmentUploadOptions(), TimeProvider.System);

        public InMemoryPendingAttachmentStore Pending { get; } = new(new PendingAttachmentOptions(), TimeProvider.System);

        public CrashSubmitHandler Submit { get; }

        public CrashAttachmentBeginHandler Begin { get; }

        public CrashAttachmentChunkHandler Chunk { get; }

        public CrashAttachmentEndHandler End { get; }
    }

    private static readonly byte[] _pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static WireRequestContext Ctx(UploadConnection connection, string type, byte[] payload) =>
        new(connection, new Envelope(1, type, EnvelopeKind.Request, "1", null, null, null, null, payload));

    private static UploadConnection Device(string? device = null) =>
        new(Guid.NewGuid(), device ?? Guid.NewGuid().ToString("D"), new JsonWireCodec());

    private static byte[] PngChunk(string type, byte[] body)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length);
        return [.. length, .. Encoding.ASCII.GetBytes(type), .. body, 0, 0, 0, 0];
    }

    private static byte[] Png(uint width = 2, uint height = 2, params byte[][] extraBeforeData)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        return [.. _pngSignature, .. PngChunk("IHDR", header), .. extraBeforeData.SelectMany(c => c), .. PngChunk("IDAT", [1, 2, 3]), .. PngChunk("IEND", [])];
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static CrashReport ReportFor(Guid reportId, Guid attachmentId) => TestReports.Report(reportId: reportId, attachmentId: attachmentId);

    private static byte[] SubmitPayload(CrashReport report) =>
        JsonSerializer.SerializeToUtf8Bytes(new CrashSubmit(report), CrashContractsJsonContext.Default.CrashSubmit);

    /// <summary>Sends begin, the chunks of <paramref name="bytes"/> in order, and end. Returns the end reply; the begin and chunk replies must be ok.</summary>
    private static async Task<WireReply> UploadAsync(
        Harness server, UploadConnection connection, Guid attachmentId, Guid reportId, byte[] bytes, string sha, string type = "image/png")
    {
        var ct = TestContext.Current.CancellationToken;
        var begin = new CrashAttachmentBegin(attachmentId, reportId, "screenshot", type, bytes.Length);
        var beginReply = await server.Begin.HandleAsync(
            Ctx(connection, CrashWireTypes.AttachmentBegin, JsonSerializer.SerializeToUtf8Bytes(begin, CrashContractsJsonContext.Default.CrashAttachmentBegin)), ct);
        Assert.True(beginReply.IsOk);

        const int chunkSize = 64;
        var count = 0;
        for (var offset = 0; offset < bytes.Length; offset += chunkSize, count++)
        {
            var piece = bytes.AsSpan(offset, Math.Min(chunkSize, bytes.Length - offset)).ToArray();
            var chunk = new CrashAttachmentChunk(attachmentId, count, piece);
            var chunkReply = await server.Chunk.HandleAsync(
                Ctx(connection, CrashWireTypes.AttachmentChunk, JsonSerializer.SerializeToUtf8Bytes(chunk, CrashContractsJsonContext.Default.CrashAttachmentChunk)), ct);
            Assert.True(chunkReply.IsOk);
        }

        var end = new CrashAttachmentEnd(attachmentId, count, sha);
        return await server.End.HandleAsync(
            Ctx(connection, CrashWireTypes.AttachmentEnd, JsonSerializer.SerializeToUtf8Bytes(end, CrashContractsJsonContext.Default.CrashAttachmentEnd)), ct);
    }

    private static async Task<WireReply> SubmitAsync(Harness server, UploadConnection connection, CrashReport report) =>
        await server.Submit.HandleAsync(Ctx(connection, CrashWireTypes.Submit, SubmitPayload(report)), TestContext.Current.CancellationToken);

    private static bool Dropped(Harness server, string code) =>
        server.Audit.Entries.Any(e => e.Kind == "crash.attachment.dropped" && e.Payload.Contains(code, StringComparison.Ordinal));

    [Fact]
    public async Task A_screenshot_that_finishes_before_its_report_is_attached_when_the_report_arrives()
    {
        var server = new Harness();
        var device = Device();
        var reportId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var bytes = Png();

        var end = await UploadAsync(server, device, attachmentId, reportId, bytes, Sha(bytes));
        var submit = await SubmitAsync(server, device, ReportFor(reportId, attachmentId));

        Assert.True(end.IsOk);
        Assert.True(submit.IsOk);
        Assert.Single(server.Store.Reports);
        var stored = Assert.Single(server.Store.Attachments);
        Assert.Equal(attachmentId, stored.AttachmentId);
        Assert.Equal(reportId, stored.ReportId);
        Assert.Equal("image/png", stored.ContentType);
        Assert.Equal(Sha(bytes), stored.Sha256);
    }

    [Fact]
    public async Task A_report_whose_screenshot_is_still_uploading_gets_the_screenshot_when_it_finishes()
    {
        var server = new Harness();
        var device = Device();
        var reportId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var bytes = Png();

        var submit = await SubmitAsync(server, device, ReportFor(reportId, attachmentId));
        Assert.True(submit.IsOk);
        Assert.Single(server.Store.Reports);
        Assert.Empty(server.Store.Attachments);

        var end = await UploadAsync(server, device, attachmentId, reportId, bytes, Sha(bytes));

        Assert.True(end.IsOk);
        var stored = Assert.Single(server.Store.Attachments);
        Assert.Equal(attachmentId, stored.AttachmentId);
        Assert.Equal(reportId, stored.ReportId);
        Assert.Contains(server.Audit.Entries, e => e.Kind == "crash.attachment.attached");
    }

    [Fact]
    public async Task A_screenshot_from_another_device_does_not_attach_to_the_report()
    {
        var server = new Harness();
        var owner = Device();
        var stranger = Device();
        var reportId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var bytes = Png();

        await SubmitAsync(server, owner, ReportFor(reportId, attachmentId));
        var end = await UploadAsync(server, stranger, attachmentId, reportId, bytes, Sha(bytes));

        // The id is already held for the owner's report, so the stranger's finished upload cannot be held under it either.
        Assert.False(end.IsOk);
        Assert.Single(server.Store.Reports);
        Assert.Empty(server.Store.Attachments);
    }

    [Fact]
    public async Task A_hash_mismatch_is_dropped_before_the_report_and_the_report_is_kept_without_it()
    {
        var server = new Harness();
        var device = Device();
        var reportId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var bytes = Png();

        var end = await UploadAsync(server, device, attachmentId, reportId, bytes, new string('0', 64));
        var submit = await SubmitAsync(server, device, ReportFor(reportId, attachmentId));

        Assert.True(end.IsOk);
        Assert.True(submit.IsOk);
        Assert.Single(server.Store.Reports);
        Assert.Empty(server.Store.Attachments);
        Assert.True(Dropped(server, "attachment_hash_mismatch"));
    }

    [Fact]
    public async Task A_hash_mismatch_after_the_report_is_refused_with_its_code_and_stores_nothing()
    {
        var server = new Harness();
        var device = Device();
        var reportId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var bytes = Png();
        await SubmitAsync(server, device, ReportFor(reportId, attachmentId));

        var end = await UploadAsync(server, device, attachmentId, reportId, bytes, new string('0', 64));

        Assert.False(end.IsOk);
        Assert.Equal(WireErrorCodes.BadRequest, end.Error!.Code);
        Assert.Equal("attachment_hash_mismatch", end.Error.Message);
        Assert.Empty(server.Store.Attachments);
        Assert.Single(server.Store.Reports);
    }

    [Fact]
    public async Task Bytes_that_are_not_the_declared_image_type_are_dropped_by_the_magic_check()
    {
        var server = new Harness();
        var device = Device();
        var reportId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var bytes = "not an image"u8.ToArray();

        await UploadAsync(server, device, attachmentId, reportId, bytes, Sha(bytes));
        await SubmitAsync(server, device, ReportFor(reportId, attachmentId));

        Assert.Empty(server.Store.Attachments);
        Assert.True(Dropped(server, "attachment_magic_mismatch"));
    }

    [Fact]
    public async Task A_png_with_a_metadata_chunk_is_dropped_by_the_header_check()
    {
        var server = new Harness();
        var device = Device();
        var reportId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var bytes = Png(2, 2, PngChunk("tEXt", "Comment\0hi"u8.ToArray()));

        await UploadAsync(server, device, attachmentId, reportId, bytes, Sha(bytes));
        await SubmitAsync(server, device, ReportFor(reportId, attachmentId));

        Assert.Empty(server.Store.Attachments);
        Assert.True(Dropped(server, "attachment_metadata_present"));
    }

    [Fact]
    public async Task A_png_over_the_dimension_cap_is_dropped_by_the_header_check()
    {
        var server = new Harness();
        var device = Device();
        var reportId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var bytes = Png(width: 9000, height: 2);

        await UploadAsync(server, device, attachmentId, reportId, bytes, Sha(bytes));
        await SubmitAsync(server, device, ReportFor(reportId, attachmentId));

        Assert.Empty(server.Store.Attachments);
        Assert.True(Dropped(server, "attachment_dimensions_exceeded"));
    }

    [Fact]
    public async Task A_valid_png_after_the_report_is_stored_sealed_and_linked()
    {
        var server = new Harness();
        var device = Device();
        var reportId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var bytes = Png();
        await SubmitAsync(server, device, ReportFor(reportId, attachmentId));

        var end = await UploadAsync(server, device, attachmentId, reportId, bytes, Sha(bytes));

        Assert.True(end.IsOk);
        var stored = Assert.Single(server.Store.Attachments);
        Assert.Equal(attachmentId, stored.AttachmentId);
        Assert.NotEmpty(stored.Data.Ciphertext);
    }

    [Fact]
    public async Task A_chunk_for_an_upload_that_was_never_begun_is_refused()
    {
        var server = new Harness();
        var device = Device();
        var chunk = new CrashAttachmentChunk(Guid.NewGuid(), 0, [1, 2, 3]);

        var reply = await server.Chunk.HandleAsync(
            Ctx(device, CrashWireTypes.AttachmentChunk, JsonSerializer.SerializeToUtf8Bytes(chunk, CrashContractsJsonContext.Default.CrashAttachmentChunk)),
            TestContext.Current.CancellationToken);

        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
        Assert.Equal("attachment_unknown", reply.Error.Message);
    }

    [Fact]
    public async Task An_end_that_names_the_wrong_chunk_count_is_refused()
    {
        var server = new Harness();
        var device = Device();
        var attachmentId = Guid.NewGuid();
        var begin = new CrashAttachmentBegin(attachmentId, Guid.NewGuid(), "screenshot", "image/png", 3);
        await server.Begin.HandleAsync(
            Ctx(device, CrashWireTypes.AttachmentBegin, JsonSerializer.SerializeToUtf8Bytes(begin, CrashContractsJsonContext.Default.CrashAttachmentBegin)),
            TestContext.Current.CancellationToken);
        await server.Chunk.HandleAsync(
            Ctx(device, CrashWireTypes.AttachmentChunk, JsonSerializer.SerializeToUtf8Bytes(new CrashAttachmentChunk(attachmentId, 0, [1, 2, 3]), CrashContractsJsonContext.Default.CrashAttachmentChunk)),
            TestContext.Current.CancellationToken);

        var end = new CrashAttachmentEnd(attachmentId, 2, new string('0', 64));
        var reply = await server.End.HandleAsync(
            Ctx(device, CrashWireTypes.AttachmentEnd, JsonSerializer.SerializeToUtf8Bytes(end, CrashContractsJsonContext.Default.CrashAttachmentEnd)),
            TestContext.Current.CancellationToken);

        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
        Assert.Equal("attachment_chunk_count_mismatch", reply.Error.Message);
    }

    [Fact]
    public async Task A_begin_with_a_type_other_than_png_or_jpeg_is_refused_with_its_wire_name()
    {
        var server = new Harness();
        var device = Device();
        var begin = new CrashAttachmentBegin(Guid.NewGuid(), Guid.NewGuid(), "screenshot", "image/gif", 3);

        var reply = await server.Begin.HandleAsync(
            Ctx(device, CrashWireTypes.AttachmentBegin, JsonSerializer.SerializeToUtf8Bytes(begin, CrashContractsJsonContext.Default.CrashAttachmentBegin)),
            TestContext.Current.CancellationToken);

        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
        Assert.Equal("attachment_type_not_allowed", reply.Error.Message);
    }

    [Fact]
    public async Task Garbage_payloads_are_bad_requests_on_all_three_handlers()
    {
        var server = new Harness();
        var device = Device();
        var garbage = "{not json"u8.ToArray();
        var ct = TestContext.Current.CancellationToken;

        var begin = await server.Begin.HandleAsync(Ctx(device, CrashWireTypes.AttachmentBegin, garbage), ct);
        var chunk = await server.Chunk.HandleAsync(Ctx(device, CrashWireTypes.AttachmentChunk, garbage), ct);
        var end = await server.End.HandleAsync(Ctx(device, CrashWireTypes.AttachmentEnd, garbage), ct);

        Assert.Equal(WireErrorCodes.BadRequest, begin.Error!.Code);
        Assert.Equal(WireErrorCodes.BadRequest, chunk.Error!.Code);
        Assert.Equal(WireErrorCodes.BadRequest, end.Error!.Code);
        Assert.Empty(server.Store.Attachments);
    }
}
