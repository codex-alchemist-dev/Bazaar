using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmDisplay.Audit.Tests;

public class AuditSinkTests
{
    private const string _secretText = "typed-text-marker-4f2a";
    private const string _backendRef = "backend-ref-marker-9c1d";
    private const string _sessionId = "browser-session-marker-77aa";

    private static readonly DisplayTarget _target =
        new("11111111-2222-3333-4444-555555555555", Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), DisplayGuestOs.Linux, _backendRef, _sessionId);

    private sealed class RecordingLog : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingLog(string message) : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => throw new InvalidOperationException(message);
    }

    private sealed class HangingLog : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => new TaskCompletionSource().Task;
    }

    private static AuditLogDisplayAuditSink Sink(IAuditLog? log, FakeTimeProvider? time = null, DisplayAuditOptions? options = null) =>
        new(log, time ?? new FakeTimeProvider(new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero)), options ?? new DisplayAuditOptions());

    [Fact]
    public void Captured_writes_size_source_and_hash()
    {
        var log = new RecordingLog();
        string sha = new('a', 64);

        Sink(log).Captured(_target, DisplaySourceKind.Guest, 1280, 720, 4096, sha);

        AuditEntry entry = Assert.Single(log.Entries);
        Assert.Equal(AuditLogDisplayAuditSink.CapturedKind, entry.Kind);
        Assert.Equal($"agent:{_target.AgentId}", entry.Actor);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero), entry.Ts);
        JsonObject payload = JsonNode.Parse(entry.Payload)!.AsObject();
        Assert.Equal(_target.AgentId, (string?)payload["agent_id"]);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", (string?)payload["vm_id"]);
        Assert.Equal("Linux", (string?)payload["os"]);
        Assert.Equal("Guest", (string?)payload["source"]);
        Assert.Equal(1280, (int?)payload["width"]);
        Assert.Equal(720, (int?)payload["height"]);
        Assert.Equal(4096, (int?)payload["bytes"]);
        Assert.Equal(sha, (string?)payload["sha256"]);
    }

    [Fact]
    public void InputSent_writes_only_the_count()
    {
        var log = new RecordingLog();

        Sink(log).InputSent(_target, DisplaySourceKind.Browser, 3);

        AuditEntry entry = Assert.Single(log.Entries);
        Assert.Equal(AuditLogDisplayAuditSink.InputSentKind, entry.Kind);
        JsonObject payload = JsonNode.Parse(entry.Payload)!.AsObject();
        Assert.Equal(3, (int?)payload["action_count"]);
        Assert.Equal("Browser", (string?)payload["source"]);
    }

    [Fact]
    public void Refused_writes_operation_and_reason()
    {
        var log = new RecordingLog();

        Sink(log).Refused(_target, "capture", "no display gate configured");

        AuditEntry entry = Assert.Single(log.Entries);
        Assert.Equal(AuditLogDisplayAuditSink.RefusedKind, entry.Kind);
        JsonObject payload = JsonNode.Parse(entry.Payload)!.AsObject();
        Assert.Equal("capture", (string?)payload["operation"]);
        Assert.Equal("no display gate configured", (string?)payload["reason"]);
    }

    [Fact]
    public void Rows_never_carry_the_backend_reference_or_the_browser_session()
    {
        var log = new RecordingLog();
        AuditLogDisplayAuditSink sink = Sink(log);

        sink.Captured(_target, DisplaySourceKind.Hypervisor, 10, 10, 10, new string('b', 64));
        sink.InputSent(_target, DisplaySourceKind.Hypervisor, 1);
        sink.Refused(_target, "input", "refused");

        Assert.Equal(3, log.Entries.Count);
        foreach (AuditEntry entry in log.Entries)
        {
            Assert.DoesNotContain(_backendRef, entry.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain(_sessionId, entry.Payload, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_bad_hash_value_cannot_carry_text_into_a_row()
    {
        var log = new RecordingLog();

        Sink(log).Captured(_target, DisplaySourceKind.Guest, 1, 1, 1, "ABC-" + _secretText + "-def0");

        JsonObject payload = JsonNode.Parse(Assert.Single(log.Entries).Payload)!.AsObject();
        Assert.DoesNotContain(_secretText, (string?)payload["sha256"], StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]*$", (string?)payload["sha256"]);
    }

    [Fact]
    public void A_reason_is_stripped_of_control_characters_and_cut()
    {
        var log = new RecordingLog();
        var options = new DisplayAuditOptions { MaxReasonLength = 10 };

        Sink(log, options: options).Refused(_target, "capture", "ab\ncd\u0000ef" + new string('x', 100));

        JsonObject payload = JsonNode.Parse(Assert.Single(log.Entries).Payload)!.AsObject();
        Assert.Equal("abcdefxxxx", (string?)payload["reason"]);
    }

    [Fact]
    public void A_missing_log_fails_closed_for_every_call()
    {
        AuditLogDisplayAuditSink sink = Sink(null);

        Assert.Throws<DisplayAuditException>(() => sink.Captured(_target, DisplaySourceKind.Guest, 1, 1, 1, "ab"));
        Assert.Throws<DisplayAuditException>(() => sink.InputSent(_target, DisplaySourceKind.Guest, 1));
        Assert.Throws<DisplayAuditException>(() => sink.Refused(_target, "capture", "x"));
    }

    [Fact]
    public void A_failing_log_fails_closed_and_its_message_is_not_echoed()
    {
        AuditLogDisplayAuditSink sink = Sink(new ThrowingLog("provider echoed " + _secretText));

        DisplayAuditException e = Assert.Throws<DisplayAuditException>(() => sink.InputSent(_target, DisplaySourceKind.Guest, 1));

        Assert.DoesNotContain(_secretText, e.Message, StringComparison.Ordinal);
        Assert.Null(e.InnerException);
    }

    [Fact]
    public async Task A_log_that_never_completes_times_out_closed()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero));
        AuditLogDisplayAuditSink sink = Sink(new HangingLog(), time, new DisplayAuditOptions { WriteTimeoutSeconds = 2 });

        Task<DisplayAuditException> call = Task.Run(() =>
            Assert.Throws<DisplayAuditException>(() => sink.Captured(_target, DisplaySourceKind.Guest, 1, 1, 1, "ab")));
        while (!call.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(3));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        DisplayAuditException e = await call;
        Assert.Contains("TimeoutException", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_target_is_rejected()
    {
        AuditLogDisplayAuditSink sink = Sink(new RecordingLog());

        Assert.Throws<ArgumentNullException>(() => sink.Captured(null!, DisplaySourceKind.Guest, 1, 1, 1, "ab"));
        Assert.Throws<ArgumentNullException>(() => sink.InputSent(null!, DisplaySourceKind.Guest, 1));
        Assert.Throws<ArgumentNullException>(() => sink.Refused(null!, "capture", "x"));
    }
}
