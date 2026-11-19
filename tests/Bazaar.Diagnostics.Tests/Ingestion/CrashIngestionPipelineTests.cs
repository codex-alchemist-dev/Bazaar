using System.Buffers.Binary;
using System.Security.Cryptography;
using Bazaar.Contracts.Telemetry;
using Bazaar.Diagnostics.Fingerprinting;
using Bazaar.Diagnostics.Ingestion;
using Bazaar.Diagnostics.Redaction;

namespace Bazaar.Diagnostics.Tests.Ingestion;

public class CrashIngestionPipelineTests
{
    private const string _canary = "canary-7f3a9c41";
    private static readonly Guid _reportId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _attachmentId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class FakeOwnership(bool owns) : IInstallOwnershipResolver
    {
        public bool OwnsInstall(Guid installId) => owns;
    }

    private sealed class FakeSymbolicator(SymbolsState state) : ISymbolicator
    {
        public int Calls { get; private set; }

        public SymbolsState Resolve(CrashReport report)
        {
            Calls++;
            return state;
        }
    }

    private static CrashIngestionPipeline Pipeline(bool owns = true, FakeSymbolicator? symbolicator = null) =>
        new(new FakeOwnership(owns), new Redactor([_canary]), symbolicator ?? new FakeSymbolicator(SymbolsState.Resolved));

    private static CrashReport Report(
        string schema = CrashReportSchemas.V1,
        string? code = "BZ-NET-0001",
        string message = "connection refused",
        string residualScan = "pass",
        int? flagged = 0,
        string? hint = null,
        Guid? attachmentId = null,
        IReadOnlyList<CrashLogEntry>? logs = null) => new(
        schema,
        _reportId,
        new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        new CrashClientInfo("windows", "0.1.0+a1b2c3d", "beta", ".NET 11.0"),
        new CrashErrorInfo(code, "System.InvalidOperationException", message, null),
        [new CrashFrame("Bazaar.Client.Core", "abc123", "Bazaar.Client.Core.Session", "Connect", 100, 4, 42, "Session.cs", true)],
        flagged,
        [new CrashModuleInfo("Bazaar.Client.Core", "0.1.0", "abc123")],
        logs ?? [],
        new CrashDeviceInfo("windows", "11", "x64", "16-32 GiB", 1.0, "desktop", "normal", "en-US"),
        new CrashScreenInfo(attachmentId, new CrashMaskingInfo(0, 0)),
        new CrashRedactionInfo("1.0.0", new CrashRedactionCounts(0, 0, 0, 0), residualScan),
        hint,
        new CrashConsentInfo("auto", "sent", "off"));

    private static byte[] Chunk(string type, byte[] body)
    {
        var bytes = new byte[12 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), (uint)body.Length);
        System.Text.Encoding.ASCII.GetBytes(type, bytes.AsSpan(4, 4));
        body.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] Png()
    {
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 16);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 16);
        header[8] = 8;
        header[9] = 2;
        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", header), .. Chunk("IDAT", [1, 2, 3]), .. Chunk("IEND", [])];
    }

    private static IngestionAttachment Attachment(byte[] data, string contentType = "image/png", string? sha = null, long? total = null, Guid? reportId = null) => new(
        new CrashAttachmentBegin(_attachmentId, reportId ?? _reportId, "screenshot", contentType, total ?? data.Length),
        new CrashAttachmentEnd(_attachmentId, 1, sha ?? Convert.ToHexStringLower(SHA256.HashData(data))),
        data);

    private static IngestionRefused Refused(IngestionOutcome outcome) => Assert.IsType<IngestionRefused>(outcome);

    [Fact]
    public void Clean_report_is_accepted_with_the_server_fingerprint()
    {
        var report = Report();

        var accepted = Assert.IsType<IngestionAccepted>(Pipeline().Process(new IngestionSubmission(report)));

        Assert.Equal(Fingerprint.Compute(CrashReportMapping.ToFingerprintInput(report)), accepted.Fingerprint);
        Assert.StartsWith("fp1:", accepted.Fingerprint, StringComparison.Ordinal);
        Assert.False(accepted.FingerprintHintMismatch);
        Assert.Equal(SymbolsState.Resolved, accepted.SymbolsState);
        Assert.Null(accepted.Attachment);
    }

    [Fact]
    public void Install_not_owned_is_refused_first_and_nothing_after_it_runs()
    {
        var symbolicator = new FakeSymbolicator(SymbolsState.Resolved);

        var refused = Refused(Pipeline(owns: false, symbolicator).Process(new IngestionSubmission(Report(schema: "bad"))));

        Assert.Equal(IngestionStep.InstallOwnership, refused.Step);
        Assert.Equal(IngestionRefusalCode.InstallNotOwned, refused.Code);
        Assert.Equal(0, symbolicator.Calls);
    }

    [Fact]
    public void Unknown_schema_is_refused()
    {
        var refused = Refused(Pipeline().Process(new IngestionSubmission(Report(schema: "bazaar.crash/2"))));

        Assert.Equal(IngestionStep.SchemaAndSize, refused.Step);
        Assert.Equal(IngestionRefusalCode.UnknownSchema, refused.Code);
    }

    [Fact]
    public void Malformed_error_code_is_refused()
    {
        var refused = Refused(Pipeline().Process(new IngestionSubmission(Report(code: "not-a-code"))));

        Assert.Equal(IngestionRefusalCode.MalformedReport, refused.Code);
    }

    [Fact]
    public void Flagged_frame_outside_the_stack_is_refused()
    {
        var refused = Refused(Pipeline().Process(new IngestionSubmission(Report(flagged: 5))));

        Assert.Equal(IngestionRefusalCode.MalformedReport, refused.Code);
    }

    [Fact]
    public void Oversized_logs_are_refused()
    {
        var logs = Enumerable.Range(0, 400)
            .Select(i => new CrashLogEntry(DateTimeOffset.UnixEpoch, "info", "net", new string('x', 300) + i, null))
            .ToArray();

        var refused = Refused(Pipeline().Process(new IngestionSubmission(Report(logs: logs))));

        Assert.Equal(IngestionRefusalCode.LogsTooLarge, refused.Code);
    }

    [Fact]
    public void Device_reported_residual_failure_is_refused()
    {
        var refused = Refused(Pipeline().Process(new IngestionSubmission(Report(residualScan: "fail"))));

        Assert.Equal(IngestionStep.Redaction, refused.Step);
        Assert.Equal(IngestionRefusalCode.RedactionReportedFailed, refused.Code);
    }

    [Fact]
    public void Planted_canary_in_the_message_is_refused_and_the_value_is_not_echoed()
    {
        var refused = Refused(Pipeline().Process(new IngestionSubmission(Report(message: $"token {_canary} leaked"))));

        Assert.Equal(IngestionRefusalCode.RedactionResidual, refused.Code);
        Assert.DoesNotContain(_canary, refused.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Planted_canary_in_a_log_message_is_refused()
    {
        var logs = new[] { new CrashLogEntry(DateTimeOffset.UnixEpoch, "info", "net", $"hello {_canary}", null) };

        var refused = Refused(Pipeline().Process(new IngestionSubmission(Report(logs: logs))));

        Assert.Equal(IngestionRefusalCode.RedactionResidual, refused.Code);
        Assert.Contains("logs[0].msg", refused.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Device_hint_that_differs_is_flagged_but_the_server_value_is_used()
    {
        var accepted = Assert.IsType<IngestionAccepted>(Pipeline().Process(new IngestionSubmission(Report(hint: "fp1:00000000000000000000000000000000"))));

        Assert.True(accepted.FingerprintHintMismatch);
        Assert.NotEqual("fp1:00000000000000000000000000000000", accepted.Fingerprint);
    }

    [Fact]
    public void Missing_symbols_never_refuse_the_report()
    {
        var accepted = Assert.IsType<IngestionAccepted>(Pipeline(symbolicator: new FakeSymbolicator(SymbolsState.Missing)).Process(new IngestionSubmission(Report())));

        Assert.Equal(SymbolsState.Missing, accepted.SymbolsState);
    }

    [Fact]
    public void Valid_attachment_is_kept()
    {
        var outcome = Pipeline().Process(new IngestionSubmission(Report(attachmentId: _attachmentId), Attachment(Png())));

        var accepted = Assert.IsType<IngestionAccepted>(outcome);
        Assert.True(accepted.Attachment!.Kept);
    }

    [Fact]
    public void Bad_attachments_are_dropped_and_the_report_is_still_accepted()
    {
        var png = Png();
        var cases = new (IngestionAttachment Attachment, IngestionRefusalCode Expected)[]
        {
            (Attachment(png, sha: new string('0', 64)), IngestionRefusalCode.AttachmentHashMismatch),
            (Attachment(png, total: png.Length + 1), IngestionRefusalCode.AttachmentSizeMismatch),
            (Attachment(png, contentType: "image/gif"), IngestionRefusalCode.AttachmentTypeNotAllowed),
            (Attachment(png, contentType: "image/jpeg"), IngestionRefusalCode.AttachmentMagicMismatch),
            (Attachment(png, reportId: Guid.NewGuid()), IngestionRefusalCode.AttachmentNotReferenced),
            (Attachment([.. png, 0x00]), IngestionRefusalCode.AttachmentMetadataPresent),
        };

        foreach (var (attachment, expected) in cases)
        {
            var accepted = Assert.IsType<IngestionAccepted>(Pipeline().Process(new IngestionSubmission(Report(attachmentId: _attachmentId), attachment)));

            Assert.False(accepted.Attachment!.Kept);
            Assert.Equal(expected, accepted.Attachment.DropReason);
        }
    }

    [Fact]
    public void Attachment_for_a_report_that_names_none_is_dropped_as_not_referenced()
    {
        var accepted = Assert.IsType<IngestionAccepted>(Pipeline().Process(new IngestionSubmission(Report(attachmentId: null), Attachment(Png()))));

        Assert.Equal(IngestionRefusalCode.AttachmentNotReferenced, accepted.Attachment!.DropReason);
    }

    [Fact]
    public void Mapping_builds_the_occurrence_from_the_report()
    {
        var occurrence = CrashReportMapping.ToOccurrence(Report(hint: "fp1:abc"));

        Assert.Equal("0.1.0+a1b2c3d", occurrence.Version);
        Assert.Equal("desktop", occurrence.DeviceClass);
        Assert.Equal("windows", occurrence.OsFamily);
        Assert.Equal("fp1:abc", occurrence.DeviceFingerprintHint);
        Assert.Equal("BZ-NET-0001", occurrence.Input.ErrorCode);
    }
}
