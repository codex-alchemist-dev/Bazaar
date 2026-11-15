using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Telemetry;

namespace Bazaar.Contracts.Tests.Telemetry;

public class CrashReportTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 13, 9, 14, 2, TimeSpan.Zero);
    private static readonly Guid _reportId = Guid.Parse("0196f3a2-0000-7000-8000-000000000001");

    private static CrashReport SampleReport(IReadOnlyList<CrashLogEntry>? logs = null, int? flagged = 0) =>
        new(
            CrashReportSchemas.V1,
            _reportId,
            _at,
            Guid.Parse("0196f3a2-0000-7000-8000-000000000002"),
            new CrashClientInfo("windows", "0.1.0+a1b2c3d", "Beta", ".NET 11"),
            new CrashErrorInfo("BZ-GC-0042", "Bazaar.Client.Core.GcViewException", "<redacted:1>", null),
            [new CrashFrame("Bazaar.Client.Core", "b3", "Bazaar.Client.Core.Panes.GcView", "RenderPage", 100663302, 118, null, null, true)],
            flagged,
            [new CrashModuleInfo("Bazaar.Client.Core", "0.1.0", "b3")],
            logs ?? [],
            new CrashDeviceInfo("windows", "10.0.26100", "x64", "16-32 GiB", 1.5, "desktop", "open", "en-GB"),
            new CrashScreenInfo(null, new CrashMaskingInfo(0, 0)),
            new CrashRedactionInfo("1.0.0", new CrashRedactionCounts(1, 0, 0, 0), "pass"),
            "fp1:9c1e",
            new CrashConsentInfo("ask", "sent", "none"));

    [Fact]
    public void Schema_identifier_is_bazaar_crash_v1() => Assert.Equal("bazaar.crash/1", CrashReportSchemas.V1);

    [Fact]
    public void Report_serialises_with_the_snake_case_names_of_section_5_1()
    {
        var json = JsonSerializer.Serialize(SampleReport(), CrashContractsJsonContext.Default.CrashReport);

        foreach (var name in new[]
        {
            "\"schema\":\"bazaar.crash/1\"", "\"report_id\":", "\"created_at\":", "\"install_id\":", "\"client\":",
            "\"error\":", "\"stack\":", "\"module_build_id\":", "\"method_token\":", "\"il_offset\":", "\"flagged_frame\":0",
            "\"modules\":", "\"logs\":", "\"device\":", "\"os_family\":", "\"ram_bucket\":", "\"display_scale\":1.5",
            "\"device_class\":", "\"screen\":", "\"attachment_id\":null", "\"panes_unknown\":", "\"redaction\":",
            "\"rules_version\":", "\"residual_scan\":", "\"fingerprint\":\"fp1:9c1e\"", "\"consent\":", "\"screenshot\":",
        })
        {
            Assert.Contains(name, json);
        }

        Assert.Contains("\"code\":\"BZ-GC-0042\"", json);
    }

    [Fact]
    public void Null_fields_are_written_so_the_report_keeps_every_schema_field()
    {
        var report = SampleReport() with { Error = new CrashErrorInfo(null, "T", "m", null), Fingerprint = null };

        var json = JsonSerializer.Serialize(report, CrashContractsJsonContext.Default.CrashReport);

        Assert.Contains("\"code\":null", json);
        Assert.Contains("\"fingerprint\":null", json);
    }

    [Fact]
    public void Report_round_trips_through_the_generated_context()
    {
        var logs = new[]
        {
            new CrashLogEntry(_at, "Warning", "Bazaar.Client.Core.Wire", "reconnect after <redacted:token>",
                new CrashLogProperties(3, null, 250, null, null, null)),
        };
        var report = SampleReport(logs);

        var json = JsonSerializer.Serialize(report, CrashContractsJsonContext.Default.CrashReport);
        var back = JsonSerializer.Deserialize(json, CrashContractsJsonContext.Default.CrashReport);

        Assert.NotNull(back);
        Assert.Equal(report.ReportId, back!.ReportId);
        Assert.Equal(report.Error, back.Error);
        Assert.Equal(report.Stack, back.Stack);
        Assert.Equal(report.Logs, back.Logs);
        Assert.Equal(report.Device, back.Device);
        Assert.Equal(report.Consent, back.Consent);
    }

    [Fact]
    public void Log_properties_write_only_the_keys_that_are_set()
    {
        var json = JsonSerializer.Serialize(
            new CrashLogEntry(_at, "Info", "c", "m", new CrashLogProperties(3, null, 250, null, null, null)),
            CrashContractsJsonContext.Default.CrashLogEntry);

        Assert.Contains("\"props\":{\"attempt\":3,\"duration_ms\":250}", json);
    }

    [Fact]
    public void Allow_list_matches_the_json_names_of_the_log_properties_type()
    {
        var all = new CrashLogProperties(1, 2, 3, "s", "m", "e");

        var json = JsonSerializer.Serialize(
            new CrashLogEntry(_at, "Info", "c", "m", all),
            CrashContractsJsonContext.Default.CrashLogEntry);
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.GetProperty("props").EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(
            CrashLogPropertyKeys.AllowedKeys.OrderBy(k => k, StringComparer.Ordinal),
            keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Unlisted_log_property_keys_are_dropped_on_read()
    {
        const string json = """
            {"t":"2026-11-13T09:13:58Z","level":"Warning","cat":"c","msg":"m",
             "props":{"attempt":3,"user_name":"someone","path":"C:\\x"}}
            """;

        var entry = JsonSerializer.Deserialize(json, CrashContractsJsonContext.Default.CrashLogEntry);

        Assert.NotNull(entry?.Props);
        Assert.Equal(3, entry!.Props!.Attempt);
        Assert.Null(entry.Props.Module);
    }

    [Fact]
    public void Valid_report_has_no_problems() => Assert.Empty(CrashReportValidation.Validate(SampleReport()));

    [Fact]
    public void Report_with_another_schema_is_rejected()
    {
        var report = SampleReport() with { Schema = "bazaar.crash/2" };

        var problem = Assert.Single(CrashReportValidation.Validate(report));
        Assert.Contains("Unsupported schema", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_error_code_is_rejected_and_null_code_is_allowed()
    {
        var bad = SampleReport() with { Error = new CrashErrorInfo("GC-0042", "T", "m", null) };
        var none = SampleReport() with { Error = new CrashErrorInfo(null, "T", "m", null) };

        Assert.Single(CrashReportValidation.Validate(bad));
        Assert.Empty(CrashReportValidation.Validate(none));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Flagged_frame_outside_the_stack_is_rejected(int flagged)
    {
        var report = SampleReport(flagged: flagged) with { Stack = [] };

        var problem = Assert.Single(CrashReportValidation.Validate(report));
        Assert.Contains("Flagged frame", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_log_array_is_rejected()
    {
        var text = new string('x', 1024);
        var logs = Enumerable.Range(0, 80)
            .Select(_ => new CrashLogEntry(_at, "Info", "c", text, null))
            .ToList();

        var problems = CrashReportValidation.Validate(SampleReport(logs));

        Assert.Contains(problems, p => p.StartsWith("Log array", StringComparison.Ordinal));
    }

    [Fact]
    public void Oversized_report_is_rejected_with_a_problem_and_no_exception()
    {
        var huge = new string('y', CrashReportLimits.MaxReportBytes);
        var report = SampleReport() with { Error = new CrashErrorInfo("BZ-GC-0042", "T", huge, null) };

        var problems = CrashReportValidation.Validate(report);

        Assert.Contains(problems, p => p.StartsWith("Report is", StringComparison.Ordinal));
    }

    [Fact]
    public void Attachment_chunk_must_be_non_empty_and_within_the_limit()
    {
        var empty = new CrashAttachmentChunk(Guid.NewGuid(), 0, []);
        var big = new CrashAttachmentChunk(Guid.NewGuid(), 0, new byte[CrashReportLimits.MaxAttachmentChunkBytes + 1]);
        var ok = new CrashAttachmentChunk(Guid.NewGuid(), 0, [1, 2, 3]);
        var negative = new CrashAttachmentChunk(Guid.NewGuid(), -1, [1]);

        Assert.Single(CrashReportValidation.ValidateAttachmentChunk(empty));
        Assert.Single(CrashReportValidation.ValidateAttachmentChunk(big));
        Assert.Single(CrashReportValidation.ValidateAttachmentChunk(negative));
        Assert.Empty(CrashReportValidation.ValidateAttachmentChunk(ok));
    }

    [Fact]
    public void Attachment_end_needs_chunks_and_a_lower_case_sha256()
    {
        var hash = new string('a', CrashReportLimits.Sha256HexLength);
        var upper = new string('A', CrashReportLimits.Sha256HexLength);

        Assert.Empty(CrashReportValidation.ValidateAttachmentEnd(new CrashAttachmentEnd(Guid.NewGuid(), 1, hash)));
        Assert.Single(CrashReportValidation.ValidateAttachmentEnd(new CrashAttachmentEnd(Guid.NewGuid(), 0, hash)));
        Assert.Single(CrashReportValidation.ValidateAttachmentEnd(new CrashAttachmentEnd(Guid.NewGuid(), 1, upper)));
        Assert.Single(CrashReportValidation.ValidateAttachmentEnd(new CrashAttachmentEnd(Guid.NewGuid(), 1, hash[..10])));
    }

    [Fact]
    public void Wire_payloads_round_trip_through_the_crash_context()
    {
        var ack = new CrashAck(_reportId, "group-1");
        var chunk = new CrashAttachmentChunk(Guid.NewGuid(), 2, Encoding.UTF8.GetBytes("pixels"));
        var submit = new CrashSubmit(SampleReport());

        var ackJson = JsonSerializer.Serialize(ack, CrashContractsJsonContext.Default.CrashAck);
        var chunkJson = JsonSerializer.Serialize(chunk, CrashContractsJsonContext.Default.CrashAttachmentChunk);
        var submitJson = JsonSerializer.Serialize(submit, CrashContractsJsonContext.Default.CrashSubmit);

        Assert.Equal(ack, JsonSerializer.Deserialize(ackJson, CrashContractsJsonContext.Default.CrashAck));
        Assert.Contains("\"data\":\"cGl4ZWxz\"", chunkJson);
        Assert.Equal(chunk.Data, JsonSerializer.Deserialize(chunkJson, CrashContractsJsonContext.Default.CrashAttachmentChunk)!.Data);
        Assert.Contains("\"report\":{", submitJson);
    }

    [Fact]
    public void Wire_type_names_match_section_16()
    {
        Assert.Equal("crash.submit", CrashWireTypes.Submit);
        Assert.Equal("crash.ack", CrashWireTypes.Ack);
        Assert.Equal("crash.attachment.begin", CrashWireTypes.AttachmentBegin);
        Assert.Equal("crash.attachment.chunk", CrashWireTypes.AttachmentChunk);
        Assert.Equal("crash.attachment.end", CrashWireTypes.AttachmentEnd);
    }
}
