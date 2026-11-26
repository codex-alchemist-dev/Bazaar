using System.Text.Json.Nodes;
using Bazaar.Core;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.BrowserPool.Audit.Tests;

public class AuditSinkTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    private static readonly PoolSession _session = new(
        "0123456789abcdef0123456789abcdef", "agent-7", 3, PoolDisplayMode.Headed, _now.AddMinutes(-5), _now.AddMinutes(25));

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

    private static AuditLogPoolAuditSink Sink(IAuditLog? log, FakeTimeProvider? time = null, PoolAuditOptions? options = null) =>
        new(log, time ?? new FakeTimeProvider(_now), options ?? new PoolAuditOptions());

    private static JsonObject Payload(AuditEntry e) => JsonNode.Parse(e.Payload)!.AsObject();

    [Fact]
    public void Opened_writes_ids_slot_mode_and_times()
    {
        var log = new RecordingLog();

        Sink(log).Opened(_session);

        AuditEntry entry = Assert.Single(log.Entries);
        Assert.Equal(AuditLogPoolAuditSink.OpenedKind, entry.Kind);
        Assert.Equal("agent:agent-7", entry.Actor);
        Assert.Equal(_now, entry.Ts);
        JsonObject p = Payload(entry);
        Assert.Equal("agent-7", (string?)p["agent_id"]);
        Assert.Null(p["session_id"]);
        Assert.Equal(3, (int?)p["slot"]);
        Assert.Equal("Headed", (string?)p["mode"]);
        Assert.NotNull(p["started_at"]);
        Assert.NotNull(p["expires_at"]);
    }

    [Fact]
    public void Closed_writes_the_reason()
    {
        var log = new RecordingLog();

        Sink(log).Closed(_session, PoolCloseReason.KillSwitch);

        AuditEntry entry = Assert.Single(log.Entries);
        Assert.Equal(AuditLogPoolAuditSink.ClosedKind, entry.Kind);
        JsonObject p = Payload(entry);
        Assert.Equal("KillSwitch", (string?)p["reason"]);
        Assert.Equal(3, (int?)p["slot"]);
        Assert.Null(p["session_id"]);
    }

    [Fact]
    public void Refused_writes_the_failure_name_and_the_detail()
    {
        var log = new RecordingLog();

        Sink(log).Refused("agent-7", PoolFailure.AgentBlocked, "The agent is blocked.");

        AuditEntry entry = Assert.Single(log.Entries);
        Assert.Equal(AuditLogPoolAuditSink.RefusedKind, entry.Kind);
        Assert.Equal("agent:agent-7", entry.Actor);
        JsonObject p = Payload(entry);
        Assert.Equal("AgentBlocked", (string?)p["failure"]);
        Assert.Equal("The agent is blocked.", (string?)p["detail"]);
    }

    [Fact]
    public void SlotQuarantined_writes_slot_and_detail_without_the_session_id()
    {
        var log = new RecordingLog();

        Sink(log).SlotQuarantined(2, "sess", "agent-7", "Cleanup failed.");

        AuditEntry entry = Assert.Single(log.Entries);
        Assert.Equal(AuditLogPoolAuditSink.SlotQuarantinedKind, entry.Kind);
        JsonObject p = Payload(entry);
        Assert.Equal(2, (int?)p["slot"]);
        Assert.Null(p["session_id"]);
        Assert.Equal("Cleanup failed.", (string?)p["detail"]);
    }

    [Fact]
    public void Detail_loses_control_characters_and_is_cut()
    {
        var log = new RecordingLog();
        var sink = Sink(log, options: new PoolAuditOptions { MaxDetailLength = 10 });

        sink.Refused("agent-7", PoolFailure.LaunchFailed, "ab\r\ncd\u0000ef\tghijklmnop");

        Assert.Equal("abcdefghij", (string?)Payload(Assert.Single(log.Entries))["detail"]);
    }

    [Fact]
    public void A_crafted_agent_id_cannot_inject_control_characters_into_the_actor()
    {
        var log = new RecordingLog();

        Sink(log).Refused("agent\n-x", PoolFailure.Disabled, "x");

        Assert.Equal("agent:agent-x", Assert.Single(log.Entries).Actor);
    }

    [Fact]
    public void A_failing_log_does_not_throw_and_is_counted_without_echoing_its_message()
    {
        var sink = Sink(new ThrowingLog("secret-marker-5521"));

        sink.Opened(_session);
        sink.Closed(_session, PoolCloseReason.Requested);
        sink.Refused("agent-7", PoolFailure.Disabled, "x");
        sink.SlotQuarantined(0, "s", "agent-7", "x");

        Assert.Equal(4, sink.FailedWrites);
    }

    [Fact]
    public void A_missing_log_does_not_throw_and_is_counted()
    {
        var sink = Sink(null);

        sink.Opened(_session);

        Assert.Equal(1, sink.FailedWrites);
    }

    [Fact]
    public void A_hanging_log_times_out_instead_of_hanging_the_pool()
    {
        var time = new FakeTimeProvider(_now);
        var sink = Sink(new HangingLog(), time, new PoolAuditOptions { WriteTimeoutSeconds = 2 });

        Task write = Task.Run(() => sink.Opened(_session), TestContext.Current.CancellationToken);
        SpinWait.SpinUntil(() => time.GetUtcNow() > _now || write.IsCompleted, 200);
        while (!write.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            Thread.Sleep(10);
        }

        Assert.True(write.IsCompletedSuccessfully);
        Assert.Equal(1, sink.FailedWrites);
    }

    [Fact]
    public void Rows_hold_no_url_proxy_or_page_text_for_a_session_with_a_start_url()
    {
        var log = new RecordingLog();
        var sink = Sink(log);

        sink.Opened(_session);
        sink.Closed(_session, PoolCloseReason.IdleTimeout);

        foreach (AuditEntry e in log.Entries)
        {
            Assert.DoesNotContain("http", e.Payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("proxy", e.Payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("url", e.Payload, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Options_are_validated()
    {
        static IConfiguration Config(params (string, string)[] v) =>
            new ConfigurationBuilder().AddInMemoryCollection(v.ToDictionary(x => x.Item1, x => (string?)x.Item2)).Build();

        Assert.Equal(5, PoolAuditOptions.Bind(Config()).WriteTimeoutSeconds);
        Assert.Equal(50, PoolAuditOptions.Bind(Config(("Bazaar:BrowserPoolAudit:MaxDetailLength", "50"))).MaxDetailLength);
        Assert.Throws<InvalidOperationException>(() => PoolAuditOptions.Bind(Config(("Bazaar:BrowserPoolAudit:WriteTimeoutSeconds", "0"))));
        Assert.Throws<InvalidOperationException>(() => PoolAuditOptions.Bind(Config(("Bazaar:BrowserPoolAudit:MaxDetailLength", "5000"))));
        Assert.Throws<InvalidOperationException>(() => PoolAuditOptions.Bind(Config(("Bazaar:BrowserPoolAudit:MaxDetailLength", "x"))));
    }
}
